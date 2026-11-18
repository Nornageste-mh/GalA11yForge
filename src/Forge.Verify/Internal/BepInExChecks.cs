using GalA11yForge.Core.Models;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Internal;

/// <summary>
/// BepInEx 安装布局与日志检查（b-01..b-06）。
/// 只读目录和日志，不加载 BepInEx、不启动游戏；skip 表示本次无法验证，不是通过。
/// </summary>
internal static class BepInExChecks
{
    public static async Task RunAsync(
        VerificationRun run,
        PreparedContext context,
        VerifyOptions options,
        DeployManifest manifest,
        CancellationToken cancellationToken)
    {
        var files = manifest.Files ?? new List<DeployFileEntry>();

        var ledgerHasBepInEx = files.Any(entry =>
            entry is not null
            && entry.Action != DeployFileAction.Remove
            && ManifestRules.NormalizeRelative(entry.RelativePath) is { } relative
            && relative.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase));

        var bepInExDirectory = Path.Combine(context.GameDirectory, "BepInEx");
        var directoryExists = Directory.Exists(bepInExDirectory);

        var shouldCheck = context.InstallerKind switch
        {
            VerifyInstallerKind.BepInEx => true,
            VerifyInstallerKind.Auto => ledgerHasBepInEx || directoryExists,
            _ => false,
        };

        if (!shouldCheck)
        {
            run.Skip(
                "b-01",
                "BepInEx 安装布局",
                VerifyPriority.P1,
                "未检测到 BepInEx 安装（账本无 BepInEx/ 条目且游戏目录无 BepInEx/），跳过 BepInEx 专项检查。");
            run.Unverified("未检测到 BepInEx 安装布局，BepInEx 加载与插件日志检查未执行。");
            return;
        }

        if (!directoryExists)
        {
            run.Fail(
                "b-01",
                "BepInEx 安装布局",
                VerifyPriority.P0,
                $"游戏目录缺少 BepInEx/ 目录：{bepInExDirectory}");
            run.Skip("b-02", "BepInEx 核心 DLL", VerifyPriority.P0, "BepInEx/ 目录缺失，无法检查核心 DLL。");
            run.Skip("b-03", "BepInEx 插件 DLL", VerifyPriority.P0, "BepInEx/ 目录缺失，无法检查插件 DLL。");
            run.Skip("b-04", "BepInEx 加载器", VerifyPriority.P1, "BepInEx/ 目录缺失，无法检查加载器。");
            run.Skip("b-05", "BepInEx 日志存在", VerifyPriority.P1, "BepInEx/ 目录缺失，无法检查日志。");
            run.Skip("b-06", "BepInEx 日志关键行", VerifyPriority.P0, "BepInEx/ 目录缺失，无法检查日志。");
            return;
        }

        run.Pass(
            "b-01",
            "BepInEx 安装布局",
            VerifyPriority.P1,
            $"BepInEx/ 目录存在：{bepInExDirectory}");

        var coreCandidates = new[]
        {
            Path.Combine(bepInExDirectory, "core", "BepInEx.dll"),
            Path.Combine(bepInExDirectory, "core", "BepInEx.Core.dll"),
            Path.Combine(bepInExDirectory, "core", "BepInEx.Preloader.Core.dll"),
        };
        var coreDll = coreCandidates.FirstOrDefault(File.Exists);
        if (coreDll is null)
        {
            run.Fail(
                "b-02",
                "BepInEx 核心 DLL",
                VerifyPriority.P0,
                "BepInEx/core/ 下未找到 BepInEx.dll、BepInEx.Core.dll 或 BepInEx.Preloader.Core.dll。");
        }
        else
        {
            run.Pass(
                "b-02",
                "BepInEx 核心 DLL",
                VerifyPriority.P1,
                $"BepInEx 核心 DLL 就位：{coreDll}");
        }

        var pluginEntries = files
            .Where(entry => entry is not null
                && entry.Action != DeployFileAction.Remove
                && ManifestRules.NormalizeRelative(entry.RelativePath) is { } relative
                && relative.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase)
                && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pluginEntries.Count == 0)
        {
            run.Skip(
                "b-03",
                "BepInEx 插件 DLL",
                VerifyPriority.P0,
                "账本未声明任何 BepInEx/plugins/*.dll，无法核对插件是否就位。");
            run.Unverified("账本未声明 BepInEx 插件 DLL，插件就位与加载情况未验证。");
        }
        else
        {
            var missingPlugins = new List<string>();
            foreach (var entry in pluginEntries)
            {
                if (!ManifestRules.TryResolveGamePath(context.GameDirectory, entry.RelativePath, out var fullPath, out _)
                    || !File.Exists(fullPath))
                {
                    missingPlugins.Add(entry.RelativePath);
                }
            }

            if (missingPlugins.Count == 0)
            {
                run.Pass(
                    "b-03",
                    "BepInEx 插件 DLL",
                    VerifyPriority.P0,
                    $"{pluginEntries.Count} 个账本声明的插件 DLL 均存在。",
                    metrics: new Dictionary<string, double> { ["pluginDlls"] = pluginEntries.Count });
            }
            else
            {
                run.Fail(
                    "b-03",
                    "BepInEx 插件 DLL",
                    VerifyPriority.P0,
                    $"插件 DLL 缺失：{ManifestRules.Summarize(missingPlugins)}",
                    metrics: new Dictionary<string, double>
                    {
                        ["pluginDlls"] = pluginEntries.Count,
                        ["missingPluginDlls"] = missingPlugins.Count,
                    });
            }
        }

        var loaderCandidates = new[]
        {
            Path.Combine(context.GameDirectory, "winhttp.dll"),
            Path.Combine(context.GameDirectory, "version.dll"),
            Path.Combine(context.GameDirectory, "doorstop_config.ini"),
            Path.Combine(context.GameDirectory, ".doorstop_version"),
        };
        var loaders = loaderCandidates.Where(File.Exists).Select(Path.GetFileName).ToList();
        if (loaders.Count == 0)
        {
            run.Skip(
                "b-04",
                "BepInEx 加载器",
                VerifyPriority.P1,
                "游戏根目录未发现 winhttp.dll、version.dll、doorstop_config.ini 或 .doorstop_version，可能是自定义注入方式。");
            run.Unverified("未发现常见 BepInEx 加载器文件，BepInEx 是否随游戏启动未验证。");
        }
        else
        {
            run.Pass(
                "b-04",
                "BepInEx 加载器",
                VerifyPriority.P1,
                $"发现加载器文件：{string.Join("、", loaders)}");
        }

        var logExists = File.Exists(context.LogPath);
        long logSize = 0;
        if (logExists)
        {
            try
            {
                logSize = new FileInfo(context.LogPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logSize = 0;
            }
        }

        if (!logExists || logSize == 0)
        {
            var preloaderLogs = TryFindPreloaderCrashLogs(context.GameDirectory);
            var preloaderNote = preloaderLogs.Count == 0
                ? string.Empty
                : $"；发现预加载器日志：{string.Join("、", preloaderLogs.Select(Path.GetFileName))}（BepInEx 很可能没有加载成功）";

            // 用户显式提供了外部语音日志（--speech-log）时，游戏自身日志缺失不应算失败：
            // 可能日志被移走/改名/清理，而朗读是否发生由 s-01..s-03 核对外部日志即可。
            if (!string.IsNullOrWhiteSpace(options.SpeechLogPath))
            {
                run.Skip(
                    "b-05",
                    "BepInEx 日志存在",
                    VerifyPriority.P1,
                    logExists
                        ? $"游戏自身日志为空：{context.LogPath}{preloaderNote}；已指定外部语音日志，不作为失败。"
                        : $"游戏自身日志不存在：{context.LogPath}{preloaderNote}；已指定外部语音日志，不作为失败。");
                run.Skip(
                    "b-06",
                    "BepInEx 日志关键行",
                    VerifyPriority.P0,
                    "游戏自身日志缺失；改由 s-01..s-03 核对外部语音日志里的朗读输出。");
                run.Unverified("游戏自身日志缺失：插件是否被游戏加载未直接核对；已改用外部语音日志核对朗读输出。");
                return;
            }

            // 日志缺失不是“未验证”，而是无法确认补丁被加载：按失败处理，避免把静默失败报成通过。
            // 若用户还没来得及启动游戏，失败信息也会明确要求启动后重跑。
            run.Fail(
                "b-05",
                "BepInEx 日志存在",
                VerifyPriority.P1,
                logExists
                    ? $"日志为空：{context.LogPath}{preloaderNote}"
                    : $"日志不存在：{context.LogPath}{preloaderNote}");
            run.Fail(
                "b-06",
                "BepInEx 日志关键行",
                VerifyPriority.P0,
                $"无法核对插件加载行：日志不存在或为空{preloaderNote}。启动一次游戏后重跑 verify；这不等于通过。");
            run.Unverified("日志不存在或为空：可能尚未启动游戏，或 BepInEx 加载器崩溃（mscorlib 被裁剪时曾出现 MissingMethodException）；需要启动游戏后重跑验证。");
            return;
        }

        run.Pass(
            "b-05",
            "BepInEx 日志存在",
            VerifyPriority.P1,
            $"日志存在：{context.LogPath}",
            metrics: new Dictionary<string, double> { ["logBytes"] = logSize });

        LogSample sample;
        try
        {
            sample = await BoundedLogReader
                .ReadAsync(context.LogPath, context.LogTailLines, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            run.Skip(
                "b-06",
                "BepInEx 日志关键行",
                VerifyPriority.P0,
                $"日志读取失败，无法核对关键行：{ex.Message}");
            run.Unverified("BepInEx 日志读取失败，补丁是否被游戏加载未验证。");
            return;
        }

        var tokens = pluginEntries
            .Select(entry => Path.GetFileNameWithoutExtension(entry.RelativePath))
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tokens.Count == 0)
        {
            run.Skip(
                "b-06",
                "BepInEx 日志关键行",
                VerifyPriority.P0,
                "账本未声明插件 DLL，无法把日志里的加载行对应到本补丁。");
            run.Unverified("账本未声明插件 DLL，日志中的插件加载行未核对。");
            return;
        }

        var lines = sample.AllLines.ToList();
        var hasBepInExHeader = lines.Any(line => line.Contains("BepInEx", StringComparison.OrdinalIgnoreCase));
        var loadedTokens = tokens
            .Where(token => lines.Any(line =>
                line.Contains(token, StringComparison.OrdinalIgnoreCase)
                && (line.Contains("Loading", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("Loaded", StringComparison.OrdinalIgnoreCase))))
            .ToList();

        // 兜底：BepInEx 的加载行打印的是插件的显示名（BepInPlugin 的 name），
        // 不一定包含 DLL 文件名（例如 DLL 是 Build.A11y.dll，日志里是「Build 无障碍补丁」）。
        // 本产品生成的插件约定显示名以「无障碍补丁」结尾，因此按该后缀识别，并在 detail 里注明是弱匹配。
        var displayNameMatched = loadedTokens.Count == 0 && lines.Any(line =>
            line.Contains("无障碍补丁", StringComparison.Ordinal)
            && (line.Contains("Loading", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Loaded", StringComparison.OrdinalIgnoreCase)));

        if (hasBepInExHeader && (loadedTokens.Count > 0 || displayNameMatched))
        {
            var matched = loadedTokens.Count > 0
                ? $"并找到插件加载行：{string.Join("、", loadedTokens)}。"
                : "并找到带「无障碍补丁」显示名的插件加载行（日志显示名与 DLL 名不同，按约定后缀匹配）。";
            run.Pass(
                "b-06",
                "BepInEx 日志关键行",
                VerifyPriority.P0,
                $"日志含 BepInEx 头，{matched}",
                metrics: new Dictionary<string, double>
                {
                    ["logLines"] = lines.Count,
                    ["loadedPlugins"] = loadedTokens.Count,
                });
            run.Unverified("日志只能证明最近一次运行加载过插件；本次安装后是否成功进入游戏仍需人工实测。");
        }
        else if (sample.Truncated)
        {
            run.Skip(
                "b-06",
                "BepInEx 日志关键行",
                VerifyPriority.P0,
                "日志较大，只读取了头尾有界窗口；未在窗口内找到 BepInEx 头与插件加载行，不能判定失败。");
            run.Unverified("日志被截断，插件加载行可能位于未读取的中段；需要人工检查完整日志或重跑验证。");
        }
        else
        {
            var reasons = new List<string>();
            if (!hasBepInExHeader)
            {
                reasons.Add("未找到 BepInEx 头");
            }

            if (loadedTokens.Count == 0)
            {
                reasons.Add($"未找到插件加载行（期望包含：{string.Join("、", tokens)}）");
            }

            var preloaderLogs = TryFindPreloaderCrashLogs(context.GameDirectory);
            if (preloaderLogs.Count > 0)
            {
                reasons.Add($"发现预加载器日志：{string.Join("、", preloaderLogs.Select(Path.GetFileName))}（加载器可能崩溃）");
            }

            run.Fail(
                "b-06",
                "BepInEx 日志关键行",
                VerifyPriority.P0,
                $"日志存在但{string.Join("，", reasons)}；补丁可能未被游戏加载。",
                metrics: new Dictionary<string, double> { ["logLines"] = lines.Count });
        }
    }

    /// <summary>查找游戏根目录下的 BepInEx 预加载器日志（崩溃时才会生成）。</summary>
    private static List<string> TryFindPreloaderCrashLogs(string gameDirectory)
    {
        try
        {
            if (!Directory.Exists(gameDirectory))
            {
                return new List<string>();
            }

            return Directory
                .EnumerateFiles(gameDirectory, "preloader_*.log", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }
}
