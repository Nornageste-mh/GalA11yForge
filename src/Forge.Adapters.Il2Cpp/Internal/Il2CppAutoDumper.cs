using System.Diagnostics;
using System.Text;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Tools;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>自动 dump 的一次尝试结果：成功只代表“产物可用”，失败带稳定错误码与可排查详情。</summary>
internal sealed record Il2CppDumpAttempt(
    bool Attempted,
    string? DumpPath,
    string? Note,
    ForgeError? Error)
{
    public static Il2CppDumpAttempt NotAttempted { get; } = new(false, null, null, null);

    public static Il2CppDumpAttempt Success(string dumpPath, string note)
        => new(true, dumpPath, note, null);

    public static Il2CppDumpAttempt Failure(ForgeError error, string? note = null)
        => new(true, null, note, error);
}

/// <summary>
/// 自动把“没有任何静态输入”的 IL2CPP 游戏补上 dump.cs：
/// 找 Il2CppDumper.exe → 校验 GameAssembly.dll + global-metadata.dat → 输出到工作区（不写游戏目录）→ 校验产物。
///
/// <para>
/// 实测注意（2026-11-15，Il2CppDumper 6.7.46）：
/// 1) 该版本目标 .NET 6，本机只有 .NET 8，必须设 DOTNET_ROLL_FORWARD=LatestMajor，否则起不来；
/// 2) 结尾 Console.ReadKey 在重定向 stdin 下会抛异常、退出码异常，但 dump.cs 完整——
///    **成功判定必须看产物**，退出码只写进报告供排查。
/// </para>
/// </summary>
internal static class Il2CppAutoDumper
{
    private const int MaxWaitMilliseconds = 10 * 60 * 1000;
    private const long MinDumpBytes = 100 * 1024;

    public static Il2CppDumpAttempt Produce(
        GameContext context,
        Il2CppOptions options,
        Il2CppLayout layout,
        CancellationToken cancellationToken)
    {
        if (layout.GameAssemblyPath is null || layout.MetadataPath is null)
        {
            var missing = layout.GameAssemblyPath is null ? "GameAssembly.dll" : "global-metadata.dat";
            return Il2CppDumpAttempt.Failure(
                ForgeError.Create(
                    ForgeErrorCodes.DependencyMissing,
                    "自动 dump 缺少输入文件：" + missing + "。",
                    "确认 --game 指向 IL2CPP 游戏根目录；本产品不会修改游戏目录。"));
        }

        var outputDirectory = ResolveOutputDirectory(context);
        // 复用工作区里已有的 dump.cs：同一游戏、同一工作区第二次跑 analyze/plan 时
        // 不该再花 20 秒重新 dump，也不该因为“这次没传 dumper 路径”就失败。
        // 只有 dump 比游戏二进制旧时才重做（游戏更新后旧 dump 可能对不上）。
        var existingDump = FindDumpFile(outputDirectory);
        if (existingDump is not null && IsDumpFresh(existingDump, layout))
        {
            return Il2CppDumpAttempt.Success(
                existingDump,
                "复用工作区已有 dump.cs（" + new FileInfo(existingDump).Length + " B，未重新运行 dumper）。");
        }

        var dumper = ResolveDumper(context, options);
        if (!dumper.Ok)
        {
            return Il2CppDumpAttempt.Failure(dumper.Error!);
        }

        var dumperPath = dumper.Value!;
        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Il2CppDumpAttempt.Failure(
                ForgeError.Create(
                    ForgeErrorCodes.Io,
                    "无法创建自动 dump 输出目录：" + outputDirectory + "（" + ex.Message + "）。",
                    "换一个可写的工作区：--workspace <目录>。"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var log = new StringBuilder();
        int exitCode;
        try
        {
            exitCode = RunDumper(dumperPath, layout.GameAssemblyPath, layout.MetadataPath, outputDirectory, log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Il2CppDumpAttempt.Failure(
                ForgeError.Create(
                    ForgeErrorCodes.Partial,
                    "Il2CppDumper 启动失败：" + ex.Message + "。",
                    "检查该文件是否完整、是否被杀毒软件拦截，或换 --option il2cpp.dumperPath=<exe>。"));
        }

        stopwatch.Stop();
        var dumpPath = FindDumpFile(outputDirectory);
        if (dumpPath is null)
        {
            return Il2CppDumpAttempt.Failure(
                ForgeError.Create(
                    ForgeErrorCodes.Partial,
                    "Il2CppDumper 运行了，但没有产出可用的 dump.cs。",
                    "看下方日志尾部与退出码；多数情况是游戏版本/metadata 不受该 dumper 支持，换 dumper 版本或人工生成 dump.cs 后用 --option il2cpp.dumpPath 指定。",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dumper"] = dumperPath,
                        ["output"] = outputDirectory,
                        ["exitCode"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["logTail"] = Tail(log.ToString(), 1200),
                    }),
                "产物缺失");
        }

        var size = new FileInfo(dumpPath).Length;
        if (size < MinDumpBytes)
        {
            return Il2CppDumpAttempt.Failure(
                ForgeError.Create(
                    ForgeErrorCodes.Partial,
                    "dump.cs 太小（" + size + " 字节），不像完整产物。",
                    "确认 Il2CppDumper 与游戏版本匹配；必要时人工跑一次看日志。",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dumper"] = dumperPath,
                        ["dump"] = dumpPath,
                        ["exitCode"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }),
                "产物过小");
        }

        var note = "已自动生成 dump.cs：" + dumpPath + "（" + (size / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                   + "MB，" + stopwatch.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                   + "s，dumper 退出码 " + exitCode + "；该版本退出码不可靠，以产物为准）";
        return Il2CppDumpAttempt.Success(dumpPath, note);
    }

    /// <summary>把“自动 dump 也失败”收敛成一条用户看得懂的错误，保留原始失败原因。</summary>
    public static ForgeError NoInputError(Il2CppLayout layout, Il2CppDumpAttempt attempt)
    {
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["autoDump"] = attempt.Attempted ? "attempted" : "disabled",
        };
        if (attempt.Note is not null)
        {
            details["dumpNote"] = attempt.Note;
        }

        if (attempt.Error is not null)
        {
            details["dumpError"] = attempt.Error.Message;
        }

        if (layout.Notes.Count > 0)
        {
            details["layout"] = string.Join("；", layout.Notes);
        }

        return ForgeError.Create(
            ForgeErrorCodes.DependencyMissing,
            "没有可用的静态输入，自动 dump 也没成功。",
            "三条路任选：① 安装 BepInEx 6.0.0-be.788 并启动一次，生成 BepInEx/interop（实测约 229 秒）；" +
            "② 用 Il2CppDumper 生成 dump.cs，再 --option il2cpp.dumpPath=<dump.cs 路径>；" +
            "③ 给 --option il2cpp.dumperPath=<Il2CppDumper.exe>，或在工作区 tools/re/il2cppdumper 放工具后重试。",
            details);
    }

    private static ForgeResult<string> ResolveDumper(GameContext context, Il2CppOptions options)
    {
        // 两种写法都认：--option il2cpp.dumperPath=<...>（适配器内）与 --option re.il2cppdumper.path=<...>（forge tools 同一套键）。
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options.AsDictionary())
        {
            if (pair.Key.StartsWith("re.", StringComparison.OrdinalIgnoreCase))
            {
                overrides[pair.Key] = pair.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.DumperPath))
        {
            overrides[ReToolCatalog.PathOptionKey("il2cppdumper")] = options.DumperPath!;
        }

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            roots.Add(context.WorkspaceRoot);
        }

        roots.Add(ForgeWorkspace.DefaultRootPath);
        roots.Add(AppContext.BaseDirectory);
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDirectory))
        {
            roots.Add(processDirectory!);
        }

        var status = ReToolLocator
            .Locate(roots, overrides)
            .FirstOrDefault(static tool => tool.Id == "il2cppdumper");
        if (status is null || !status.Found || string.IsNullOrWhiteSpace(status.Path))
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "没有找到 Il2CppDumper.exe，无法自动生成 dump.cs。",
                "下载 https://github.com/Perfare/Il2CppDumper/releases（MIT），解压到工作区 tools/re/il2cppdumper/ 或 forge.exe 旁 tools/re/il2cppdumper/；" +
                "也可以用 --option il2cpp.dumperPath=<exe 或目录> 指定。注意本机需 .NET 运行时（本产品会自动设 DOTNET_ROLL_FORWARD=LatestMajor）。"));
        }

        return ForgeResult<string>.Success(status.Path);
    }

    private static string ResolveOutputDirectory(GameContext context)
    {
        var root = string.IsNullOrWhiteSpace(context.WorkspaceRoot)
            ? ForgeWorkspace.DefaultRootPath
            : context.WorkspaceRoot;
        var slug = string.IsNullOrWhiteSpace(context.Slug)
            ? PathSafety.SlugFromGameDirectory(context.GameDirectory)
            : context.Slug;
        return Path.Combine(root, "games", slug, "il2cpp-dump");
    }

    private static int RunDumper(
        string dumperPath,
        string gameAssemblyPath,
        string metadataPath,
        string outputDirectory,
        StringBuilder log,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dumperPath,
            WorkingDirectory = Path.GetDirectoryName(dumperPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        startInfo.ArgumentList.Add(gameAssemblyPath);
        startInfo.ArgumentList.Add(metadataPath);
        startInfo.ArgumentList.Add(outputDirectory);
        // Il2CppDumper 6.7.46 是 .NET 6 单文件；本机只有 .NET 8 时必须允许前滚。
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";

        using var process = new Process { StartInfo = startInfo };
        var gate = new object();
        DataReceivedEventHandler append = (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (gate)
            {
                log.AppendLine(args.Data);
                if (log.Length > 200_000)
                {
                    log.Remove(0, log.Length - 100_000);
                }
            }
        };
        process.OutputDataReceived += append;
        process.ErrorDataReceived += append;
        if (!process.Start())
        {
            throw new InvalidOperationException("进程未能启动。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // 重定向 stdin 只用于避免 ReadKey 阻塞；关不掉不影响产物判定。
        }

        var waited = 0;
        while (!process.WaitForExit(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            waited += 500;
            if (waited >= MaxWaitMilliseconds)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 已退出。
                }

                lock (gate)
                {
                    log.AppendLine("[forge] 超过 10 分钟仍未结束，已终止。");
                }

                throw new TimeoutException("Il2CppDumper 超时（10 分钟）。");
            }
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static string? FindDumpFile(string outputDirectory)
    {
        var direct = Path.Combine(outputDirectory, "dump.cs");
        if (File.Exists(direct))
        {
            return direct;
        }

        try
        {
            return Directory
                .EnumerateFiles(outputDirectory, "dump.cs", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDumpFresh(string dumpPath, Il2CppLayout layout)
    {
        try
        {
            var size = new FileInfo(dumpPath).Length;
            if (size < MinDumpBytes)
            {
                return false;
            }

            var dumpTime = File.GetLastWriteTimeUtc(dumpPath);
            var newestInput = dumpTime;
            foreach (var path in new[] { layout.GameAssemblyPath, layout.MetadataPath })
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                var inputTime = File.GetLastWriteTimeUtc(path);
                if (inputTime > newestInput)
                {
                    newestInput = inputTime;
                }
            }

            return dumpTime >= newestInput;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Tail(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[^maxLength..];
    }
}
