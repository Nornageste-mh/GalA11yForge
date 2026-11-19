using System.Text.Json;
using GalA11yForge.Analyzers.Ghidra;
using GalA11yForge.Build;
using GalA11yForge.Core;
using GalA11yForge.Core.Adapters;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Tools;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Detection;
using GalA11yForge.Detection.Fingerprints;

namespace GalA11yForge.Cli;

/// <summary>统一的 CLI 返回信封；--json 时写到 stdout，便于脚本与后续 GUI 复用。</summary>
public sealed class CliResponse
{
    public bool Ok { get; set; }

    public string Command { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string? Message { get; set; }

    public string? Suggestion { get; set; }

    /// <summary>结构化补充信息（例如回滚部分失败时每个文件的原因）；没有就不输出。</summary>
    public IReadOnlyDictionary<string, string>? Details { get; set; }

    public object? Data { get; set; }
}

public sealed class DoctorCheck
{
    public string Name { get; set; } = string.Empty;

    public bool Passed { get; set; }

    /// <summary>可选检查：未通过不影响 doctor 总体结论（例如未安装 Ghidra）。</summary>
    public bool Optional { get; set; }

    public string Detail { get; set; } = string.Empty;
}

public sealed class DoctorReport
{
    public bool Ok { get; set; }

    public string ToolVersion { get; set; } = ForgeInfo.Version;

    public string Workspace { get; set; } = string.Empty;

    public List<DoctorCheck> Checks { get; set; } = new();
}

/// <summary>
/// CLI 执行入口。W2-A 阶段除 doctor/help/version 外的命令一律返回明确的“未实现”，
/// 绝不创建假的报告或假的成功结果。
/// </summary>
public static class CliApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var parsed = CommandLineParser.Parse(args);
        if (parsed.HelpRequested || parsed.Options?.Command == CliCommand.Help)
        {
            WriteUsage(stdout);
            return ExitCodes.Success;
        }

        if (parsed.VersionRequested || parsed.Options?.Command == CliCommand.Version)
        {
            await stdout.WriteLineAsync($"{ForgeInfo.ProductName} {ForgeInfo.Version}（{ForgeInfo.ProductNameChinese}）").ConfigureAwait(false);
            return ExitCodes.Success;
        }

        if (parsed.Error is not null)
        {
            // 0.2.58：解析失败时 parsed.Options 可能为 null，但用户显式要求 --json 时仍要回 JSON。
            var jsonRequested = parsed.Options?.Json == true || Array.Exists(args, a => string.Equals(a, "--json", StringComparison.Ordinal));
            WriteError(parsed.Error, stdout, stderr, json: jsonRequested);
            return ExitCodes.ArgumentError;
        }

        var options = parsed.Options!;
        try
        {
            return options.Command switch
            {
                CliCommand.Doctor => await RunDoctorAsync(options, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Tools => await RunToolsAsync(options, stdout, stderr).ConfigureAwait(false),
                CliCommand.Detect => await RunDetectAsync(options, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Analyze or CliCommand.Plan or CliCommand.Generate
                    or CliCommand.Build or CliCommand.Install or CliCommand.Verify
                    or CliCommand.Report
                    => await PipelineCommands.RunAsync(options, stdout, stderr, cancellationToken).ConfigureAwait(false),
                _ => await RunPipelinePlaceholderAsync(options, stdout, stderr).ConfigureAwait(false),
            };
        }
        catch (ForgePathException ex)
        {
            WriteError(ex.Error, stdout, stderr, options.Json);
            return ExitCodes.FromError(ex.Error);
        }
        catch (Exception ex)
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.Internal,
                $"未预期的内部错误：{ex.Message}",
                "用 --verbose 复现并提交日志；这属于工具缺陷，不是游戏问题。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.InternalError;
        }
    }

    private static async Task<int> RunPipelinePlaceholderAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (string.IsNullOrWhiteSpace(options.GamePath))
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"命令 {CommandText(options.Command)} 需要 --game <游戏目录>。",
                "传入已安装游戏的目录；工具不会写入该目录。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.ArgumentError;
        }

        string gameDirectory;
        try
        {
            gameDirectory = Path.GetFullPath(options.GamePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var error = ForgeError.Create(ForgeErrorCodes.ArgumentInvalid, $"游戏目录不合法：{options.GamePath}。", "传入一个有效的绝对或相对目录。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.ArgumentError;
        }

        if (!Directory.Exists(gameDirectory))
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{gameDirectory}。",
                "检查 --game 指向的安装目录，注意路径中的空格与引号。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.ArgumentError;
        }

        var workspaceRoot = string.IsNullOrWhiteSpace(options.WorkspacePath)
            ? ForgeWorkspace.DefaultRootPath
            : Path.GetFullPath(options.WorkspacePath);

        var notImplemented = ForgeError.Create(
            ForgeErrorCodes.NotImplemented,
            $"命令 {CommandText(options.Command)} 尚未实现：当前版本只有契约层与 CLI 框架，尚未接入引擎适配器。",
            "这不是成功结果。等待适配器实现后重试；现在可运行 forge doctor 检查环境，或查看 docs/06-接口契约-v1.md。");

        var data = new Dictionary<string, object?>
        {
            ["game"] = gameDirectory,
            ["workspace"] = workspaceRoot,
            ["dryRun"] = options.DryRun,
            ["advanced"] = options.Advanced,
        };

        await WriteResultAsync(
            new CliResponse
            {
                Ok = false,
                Command = CommandText(options.Command),
                Code = notImplemented.Code,
                Message = notImplemented.Message,
                Suggestion = notImplemented.Suggestion,
                Details = notImplemented.Details,
                Data = data,
            },
            stdout,
            stderr,
            options.Json).ConfigureAwait(false);
        return ExitCodes.DependencyMissing;
    }

    private static async Task<int> RunDetectAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.GamePath))
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                "命令 detect 需要 --game <游戏目录>。",
                "传入已安装游戏的目录；本命令只读该目录，不会写入。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.ArgumentError;
        }

        string gameDirectory;
        try
        {
            gameDirectory = Path.GetFullPath(options.GamePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不合法：{options.GamePath}。",
                "传入一个有效的绝对或相对目录。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.ArgumentError;
        }

        if (!Directory.Exists(gameDirectory))
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{gameDirectory}。",
                "检查 --game 指向的安装目录，注意路径中的空格与引号。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.ArgumentError;
        }

        DetectionResult result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = new EngineDetector().Detect(gameDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"检测时目录已不存在：{gameDirectory}。",
                "确认 --game 路径；本命令不会创建目录。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.ArgumentError;
        }
        catch (FingerprintFormatException ex)
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                $"内置指纹库损坏或版本不兼容：{ex.Message}",
                "重新安装工具或报告缺陷；这是工具自身问题，不是游戏问题。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.DependencyMissing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = ForgeError.Create(
                ForgeErrorCodes.Io,
                $"读取游戏目录失败：{ex.Message}",
                "检查目录权限；游戏目录必须是可读的本地目录。");
            WriteError(error, stdout, stderr, options.Json);
            return ExitCodes.DependencyMissing;
        }

        var contractFamily = EngineAliasMap.ToContractId(result.Family);
        var loaderCompat = LoaderCompatProbe.Probe(gameDirectory);
        var message = result.IsKnown
            ? $"识别为 {contractFamily}（置信度 {result.Confidence:P0}，{result.Verdict}）。"
            : "未识别出已知引擎（unknown，置信度 0）；这不是失败，但后续只能走通用/人工路线。";

        if (!options.Json)
        {
            await stdout.WriteLineAsync($"[完成] {message}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  游戏目录：{gameDirectory}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  研究 id：{result.Family}　契约 id：{contractFamily}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  版本：{result.Primary.Version ?? "未识别"}　位数：{(result.Primary.Bitness == 0 ? "未识别" : result.Primary.Bitness.ToString())}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  扫描：{result.FilesScanned} 文件 / {result.DirectoriesScanned} 目录，耗时 {result.Elapsed.TotalMilliseconds:F0} ms{(result.Truncated ? "（已截断，结果可能不全）" : string.Empty)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  加载器兼容性：{loaderCompat.VerdictText}").ConfigureAwait(false);
            foreach (var todo in loaderCompat.ManualTodos)
            {
                await stdout.WriteLineAsync($"    人工待办：{todo}").ConfigureAwait(false);
            }
            if (loaderCompat.IsUnsupported)
            {
                await stderr.WriteLineAsync("注意：该游戏运行库被裁剪（mscorlib 缺 GetPEKind），BepInEx 无法加载；build/install 会明确拒绝，不会假装成功。").ConfigureAwait(false);
            }
            foreach (var evidence in result.Primary.Evidence.Take(8))
            {
                await stdout.WriteLineAsync($"  证据：{evidence}").ConfigureAwait(false);
            }

            if (result.Candidates.Count > 0)
            {
                await stdout.WriteLineAsync("  候选：").ConfigureAwait(false);
                foreach (var candidate in result.Candidates.Take(5))
                {
                    var suppressed = candidate.IsSuppressed ? $"（被 {candidate.SuppressedBy} 抑制）" : string.Empty;
                    await stdout.WriteLineAsync($"    {candidate.Engine} {candidate.Score} 分 {candidate.EngineName} {suppressed}").ConfigureAwait(false);
                }
            }

            if (result.IsSuspected)
            {
                await stderr.WriteLineAsync("注意：置信度低于 50%，只算疑似，不建议直接据此生成补丁。").ConfigureAwait(false);
            }

            return ExitCodes.Success;
        }

        var candidates = result.Candidates
            .Select(candidate => new
            {
                engine = candidate.Engine,
                contractId = EngineAliasMap.ToContractId(candidate.Engine),
                engineName = candidate.EngineName,
                engineFamily = candidate.EngineFamily,
                difficulty = candidate.Difficulty,
                ruleId = candidate.RuleId,
                score = candidate.Score,
                confidence = candidate.Confidence,
                suppressedBy = candidate.SuppressedBy,
                evidence = candidate.Evidence,
            })
            .ToList();

        var data = new Dictionary<string, object?>
        {
            ["game"] = gameDirectory,
            ["family"] = contractFamily,
            ["researchFamily"] = result.Family,
            ["version"] = result.Primary.Version,
            ["bitness"] = result.Primary.Bitness,
            ["confidence"] = result.Confidence,
            ["verdict"] = result.Verdict,
            ["known"] = result.IsKnown,
            ["suspected"] = result.IsSuspected,
            ["filesScanned"] = result.FilesScanned,
            ["directoriesScanned"] = result.DirectoriesScanned,
            ["truncated"] = result.Truncated,
            ["elapsedMs"] = Math.Round(result.Elapsed.TotalMilliseconds, 1),
            ["evidence"] = result.Primary.Evidence,
            ["candidates"] = candidates,
            ["loaderCompat"] = new
            {
                verdict = loaderCompat.Verdict,
                verdictText = loaderCompat.VerdictText,
                dataDirectory = loaderCompat.DataDirectory,
                mscorlibHasGetPEKind = loaderCompat.MscorlibHasGetPEKind,
                mscorlibSizeBytes = loaderCompat.MscorlibSizeBytes,
                inputHasAnyKeyDown = loaderCompat.InputHasAnyKeyDown,
                manualTodos = loaderCompat.ManualTodos,
                evidence = loaderCompat.Evidence,
            },
        };

        await WriteResultAsync(
            new CliResponse
            {
                Ok = true,
                Command = "detect",
                Message = message,
                Data = data,
            },
            stdout,
            stderr,
            options.Json).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static async Task<int> RunDoctorAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var workspaceRoot = string.IsNullOrWhiteSpace(options.WorkspacePath)
            ? ForgeWorkspace.DefaultRootPath
            : Path.GetFullPath(options.WorkspacePath);

        ForgeWorkspace workspace;
        try
        {
            workspace = new ForgeWorkspace(workspaceRoot, options.GamePath);
        }
        catch (ForgePathException ex)
        {
            WriteError(ex.Error, stdout, stderr, options.Json);
            return ExitCodes.FromError(ex.Error);
        }

        var report = new DoctorReport { Workspace = workspace.RootPath };
        var runtimeOk = Environment.Version.Major >= 8;
        report.Checks.Add(new DoctorCheck
        {
            Name = "dotnet-runtime",
            Passed = runtimeOk,
            Detail = $"当前运行时 {Environment.Version}；要求 .NET 8 或更高。",
        });

        var ensure = workspace.EnsureCreated();
        var writable = false;
        string? writeError = null;
        if (ensure.Ok)
        {
            try
            {
                var probe = Path.Combine(workspace.RootPath, $".doctor-probe-{Guid.NewGuid():N}");
                await File.WriteAllTextAsync(probe, "probe", cancellationToken).ConfigureAwait(false);
                File.Delete(probe);
                writable = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeError = ex.Message;
            }
        }

        report.Checks.Add(new DoctorCheck
        {
            Name = "workspace",
            Passed = ensure.Ok && writable,
            Detail = ensure.Ok && writable
                ? $"工作区可写：{workspace.RootPath}"
                : $"工作区不可写：{(ensure.Error?.Message ?? writeError ?? "未知原因")}",
        });

        var registryResult = AdapterRegistry.LoadFromWorkspace(workspace);
        var builtInCount = BuiltInAdapters.Count;
        var registryDetail = registryResult.Ok
            ? $"外置注册表已加载，共 {registryResult.Value!.Adapters.Count} 个适配器。"
            : registryResult.Error!.Message;
        report.Checks.Add(new DoctorCheck
        {
            Name = "adapters",
            Passed = builtInCount > 0 || registryResult.Ok,
            Detail = builtInCount > 0
                ? $"内置适配器 {builtInCount} 个（{string.Join("、", BuiltInAdapters.EngineIds)}）；{registryDetail}"
                : registryDetail,
        });

        report.Checks.Add(new DoctorCheck
        {
            Name = "reports",
            Passed = Directory.Exists(workspace.ReportsRoot),
            Detail = $"报告目录：{workspace.ReportsRoot}；flow 日志：{workspace.FlowLogPath}",
        });

        var explicitGhidra = options.OptionOverrides.TryGetValue(GhidraLocator.OptionPathKey, out var configuredGhidra)
            ? configuredGhidra
            : null;
        var ghidra = GhidraLocator.Locate(new GhidraLocationRequest { ExplicitPath = explicitGhidra });
        report.Checks.Add(new DoctorCheck
        {
            Name = "ghidra",
            Passed = ghidra.Available,
            Optional = true,
            Detail = ghidra.Available
                ? $"可选：已找到 Ghidra {ghidra.Version ?? "(版本未知)"}（{ghidra.HomeDirectory}，来源 {ghidra.Source}）。"
                : "可选：未找到 Ghidra；analyze --advanced 会跳过原生分析并写入 unverified。可用 --option ghidra.path=<目录> 指定。",
        });

        var bepInExRoots = new List<string> { workspace.RootPath, AppContext.BaseDirectory };
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDirectory))
        {
            bepInExRoots.Add(processDirectory!);
        }

        var bepInExCandidates = BepInExLocator.FindCandidates(bepInExRoots);
        var bepInExArchive = bepInExCandidates.Ok && bepInExCandidates.Value!.Count > 0
            ? bepInExCandidates.Value[0]
            : null;
        report.Checks.Add(new DoctorCheck
        {
            Name = "bepinex-bundle",
            Passed = bepInExArchive is not null,
            Optional = true,
            Detail = bepInExArchive is not null
                ? $"可选：已找到 BepInEx 包 {bepInExArchive}；Mono 的 build/install 会自动使用它。"
                : "可选：未找到 BepInEx 5.4.23.5 zip；Mono 的 build/install 需要它。把它放到工作区、forge.exe 旁的 bepinex\\ 目录，或用 --bepinex <zip> 指定。",
        });

        var bepInEx6Candidates = BepInEx6Locator.FindCandidates(bepInExRoots);
        var bepInEx6Archive = bepInEx6Candidates.Ok && bepInEx6Candidates.Value!.Count > 0
            ? bepInEx6Candidates.Value[0]
            : null;
        report.Checks.Add(new DoctorCheck
        {
            Name = "bepinex6-bundle",
            Passed = bepInEx6Archive is not null,
            Optional = true,
            Detail = bepInEx6Archive is not null
                ? $"可选：已找到 BepInEx 6 包 {bepInEx6Archive}；IL2CPP 的 install 会自动使用它。"
                : "可选：未找到 BepInEx 6.0.0-be.788 zip；IL2CPP 的 install 需要它。把它放到工作区、forge.exe 旁的 bepinex6\\ 目录，或用 --bepinex <zip> 指定。",
        });

        var voices = VoiceProbe.Probe();
        report.Checks.Add(new DoctorCheck
        {
            Name = "japanese-voice",
            Passed = voices.HasJapanese,
            Optional = true,
            Detail = voices.HasJapanese
                ? $"可选：已找到 {voices.JapaneseVoices.Count} 个日语语音（{string.Join("、", voices.JapaneseVoices.Take(3))}）。"
                : "可选：系统没有日语语音；日语台词会被按中文/英文念，或读不出来。可在 Windows 设置里的“时间和语言 → 语音”安装日语语音后重试。",
        });

        report.Ok = report.Checks.All(c => c.Passed || c.Optional);
        var data = new Dictionary<string, object?>
        {
            ["workspace"] = report.Workspace,
            ["toolVersion"] = report.ToolVersion,
            ["checks"] = report.Checks,
        };

        if (report.Ok)
        {
            await WriteResultAsync(
                new CliResponse
                {
                    Ok = true,
                    Command = "doctor",
                    Message = "环境检查通过。",
                    Data = data,
                },
                stdout,
                stderr,
                options.Json).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        var failed = report.Checks.Where(c => !c.Passed && !c.Optional).Select(c => c.Name).ToArray();
        var error = ForgeError.Create(
            ForgeErrorCodes.DependencyMissing,
            $"环境检查未通过：{string.Join("、", failed)}。",
            "按上方每项 Detail 修复；适配器缺失时先放置 adapters/adapter.json。");
        await WriteResultAsync(
            new CliResponse
            {
                Ok = false,
                Command = "doctor",
                Code = error.Code,
                Message = error.Message,
                Suggestion = error.Suggestion,
                Data = data,
            },
            stdout,
            stderr,
            options.Json).ConfigureAwait(false);
        return ExitCodes.DependencyMissing;
    }

    private static async Task<int> RunToolsAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr)
    {
        var workspaceRoot = string.IsNullOrWhiteSpace(options.WorkspacePath)
            ? ForgeWorkspace.DefaultRootPath
            : Path.GetFullPath(options.WorkspacePath);

        var searchRoots = new List<string> { workspaceRoot, AppContext.BaseDirectory };
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(processDirectory))
        {
            searchRoots.Add(processDirectory!);
        }

        var tools = ReToolLocator.Locate(searchRoots, options.OptionOverrides);
        var found = tools.Count(static tool => tool.Found);
        var data = new Dictionary<string, object?>
        {
            ["workspace"] = workspaceRoot,
            ["found"] = found,
            ["total"] = tools.Count,
            ["searchRoots"] = searchRoots,
            ["tools"] = tools,
        };

        await WriteResultAsync(
            new CliResponse
            {
                Ok = true,
                Command = "tools",
                Message = $"RE 工具检查完成：找到 {found}/{tools.Count}。缺少的工具不影响已有引擎的补丁流程。",
                Data = data,
            },
            stdout,
            stderr,
            options.Json).ConfigureAwait(false);
        return ExitCodes.Success;
    }

    private static async Task WriteResultAsync(CliResponse response, TextWriter stdout, TextWriter stderr, bool json)
    {
        if (json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(response, ForgeJson.Compact)).ConfigureAwait(false);
            return;
        }

        if (response.Ok)
        {
            await stdout.WriteLineAsync($"[完成] {response.Message}").ConfigureAwait(false);
            if (response.Data is Dictionary<string, object?> data)
            {
                foreach (var pair in data)
                {
                    if (pair.Value is IEnumerable<DoctorCheck> checks)
                    {
                        foreach (var check in checks)
                        {
                            await stdout.WriteLineAsync($"  {(check.Passed ? "通过" : check.Optional ? "未检出" : "未通过")} {check.Name}：{check.Detail}").ConfigureAwait(false);
                        }
                    }
                    else if (pair.Value is IEnumerable<ReToolStatus> tools)
                    {
                        foreach (var tool in tools)
                        {
                            var state = tool.Found ? "已找到" : "未找到";
                            var version = string.IsNullOrWhiteSpace(tool.Version) ? string.Empty : $"（版本 {tool.Version}）";
                            await stdout.WriteLineAsync($"  {state} {tool.DisplayName}{version}：{(tool.Found ? tool.Path : tool.Note)}").ConfigureAwait(false);
                            // 许可证必须跟工具一起显示：用户要能一眼看出哪些是 GPL、哪些不进发行包。
                            if (!string.IsNullOrWhiteSpace(tool.License))
                            {
                                await stdout.WriteLineAsync($"      许可：{tool.License}").ConfigureAwait(false);
                            }
                            if (!tool.Found)
                            {
                                await stdout.WriteLineAsync($"      下载：{tool.DownloadUrl}；放置：{tool.PlacementHint}").ConfigureAwait(false);
                            }
                        }
                    }
                    else if (pair.Value is IEnumerable<string> texts)
                    {
                        await stdout.WriteLineAsync($"  {pair.Key}: {string.Join("；", texts)}").ConfigureAwait(false);
                    }
                    else
                    {
                        await stdout.WriteLineAsync($"  {pair.Key}: {pair.Value}").ConfigureAwait(false);
                    }
                }
            }

            return;
        }

        await stderr.WriteLineAsync($"未完成：{response.Message}").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.Suggestion))
        {
            await stderr.WriteLineAsync($"建议：{response.Suggestion}").ConfigureAwait(false);
        }

        if (response.Details is { Count: > 0 } details)
        {
            foreach (var pair in details)
            {
                await stderr.WriteLineAsync($"详情 {pair.Key}：{pair.Value}").ConfigureAwait(false);
            }
        }

        if (response.Data is Dictionary<string, object?> failureData)
        {
            foreach (var pair in failureData)
            {
                if (pair.Value is IEnumerable<DoctorCheck> checks)
                {
                    foreach (var check in checks)
                    {
                        await stderr.WriteLineAsync($"  {(check.Passed ? "通过" : check.Optional ? "未检出" : "未通过")} {check.Name}：{check.Detail}").ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private static void WriteError(ForgeError error, TextWriter stdout, TextWriter stderr, bool json)
    {
        if (json)
        {
            var response = new CliResponse
            {
                Ok = false,
                Command = string.Empty,
                Code = error.Code,
                Message = error.Message,
                Suggestion = error.Suggestion,
                Details = error.Details,
            };
            stdout.WriteLine(JsonSerializer.Serialize(response, ForgeJson.Compact));
            return;
        }

        stderr.WriteLine($"错误 [{error.Code}]：{error.Message}");
        if (!string.IsNullOrWhiteSpace(error.Suggestion))
        {
            stderr.WriteLine($"建议：{error.Suggestion}");
        }

        if (error.Details is { Count: > 0 } errorDetails)
        {
            foreach (var pair in errorDetails)
            {
                stderr.WriteLine($"详情 {pair.Key}：{pair.Value}");
            }
        }
    }

    private static string CommandText(CliCommand command) => command.ToString().ToLowerInvariant();

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine($"{ForgeInfo.ProductName} {ForgeInfo.Version} — {ForgeInfo.ProductNameChinese}");
        writer.WriteLine();
        writer.WriteLine("用法： forge <命令> [选项]");
        writer.WriteLine();
        writer.WriteLine("命令：");
        writer.WriteLine("  detect   识别游戏引擎（内置指纹库；只读）");
        writer.WriteLine("  analyze  分析文本管线，产出候选与覆盖声明（只读游戏目录）");
        writer.WriteLine("  plan     把分析结果变成补丁方案（含风险与人工待办）");
        writer.WriteLine("  generate 生成补丁工程 Demo（默认写工作区 03-generate/）");
        writer.WriteLine("  build    构建补丁（生成工程 Demo→dotnet build；只写工作区）");
        writer.WriteLine("  install  安装补丁（写游戏目录前逐文件备份，可回滚）");
        writer.WriteLine("  verify   安装后初检与回滚；--rollback 先按清单还原；--speech-log 顺手检查“有没有真的读出来”");
        writer.WriteLine("  report   汇总报告（JSON + Markdown + 人工待办）");
        writer.WriteLine("  doctor   检查运行时、工作区与适配器注册表");
        writer.WriteLine("  tools    检查 RE 工具（Il2CppDumper/pyghidra/Frida/dnSpyEx）是否找得到；只报告，不下载");
        writer.WriteLine();
        writer.WriteLine("选项：");
        writer.WriteLine("  --game <目录>       游戏安装目录（只读）");
        writer.WriteLine("  --out <目录>        输出目录（默认工作区 reports/<slug>）");
        writer.WriteLine("  --workspace <目录>  工作区根目录（默认 %LOCALAPPDATA%\\GalA11yForge）");
        writer.WriteLine("  --bepinex <zip>     BepInEx 离线包（Mono 5.4.23.5 / IL2CPP 6.0.0-be.788；默认在工作区里找）");
        writer.WriteLine("  --speech-runtime <目录>  IL2CPP 安装用：Tolk.dll 等语音运行期 DLL 所在目录（用户自备，不随产品分发）");
        writer.WriteLine("  --speech-log <日志>  verify 用：检查运行日志里有没有真实朗读行（不含台词文本）；相对路径先按当前目录、再按游戏目录解析；不传则自动找默认日志");
        writer.WriteLine("  --option key=value  覆盖 profile.options 的运行时选项（可重复，例如 --option il2cpp.dumpPath=D:\\dump.cs）");
        writer.WriteLine("  --rollback          verify 时先按安装清单回滚");
        writer.WriteLine("  --force             install/verify 时允许覆盖或强制回滚");
        writer.WriteLine("  --json              输出机器可读 JSON");
        writer.WriteLine("  --dry-run           只做检查，不写盘（未实现命令仍返回未实现）");
        writer.WriteLine("  --verbose           输出更多诊断");
        writer.WriteLine("  --advanced          允许调用 Ghidra 等高级后端");
        writer.WriteLine("  --help, --version   帮助 / 版本");
        writer.WriteLine();
        writer.WriteLine("退出码：0 成功，2 参数错误，3 依赖或能力缺失，4 引擎不支持，5 部分成功，1 内部错误。");
        writer.WriteLine("说明：未实现的命令返回 3 与 E_NOT_IMPLEMENTED，绝不假成功。");
    }
}
