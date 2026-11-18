using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GalA11yForge.Build;
using GalA11yForge.Core;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Logging;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Profiles;
using GalA11yForge.Core.Reports;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Serialization;
using GalA11yForge.Core.Workspaces;
using GalA11yForge.Detection;
using GalA11yForge.Detection.Fingerprints;
using GalA11yForge.Verify;
using GalA11yForge.Verify.Models;
using GalA11yForge.Verify.Reporting;

namespace GalA11yForge.Cli;

/// <summary>
/// analyze / plan / generate 的真实流水线编排。
///
/// <para>
/// 只读游戏目录；只写工作区；未接线的阶段返回明确的 E_NOT_IMPLEMENTED，绝不假成功。
/// 每一步都把结果落到 games/&lt;slug&gt;/0X-stage/*.json，供 GUI 与报告复用。
/// </para>
/// </summary>
internal static class PipelineCommands
{
    private sealed class CliFailure : Exception
    {
        public CliFailure(ForgeError error)
            : base(error.Message)
            => Error = error;

        public ForgeError Error { get; }

        public int ExitCode => ExitCodes.FromError(Error);
    }

    private sealed class Resolved
    {
        public required string GameDirectory { get; init; }

        public required string WorkspaceRoot { get; init; }

        public required string Slug { get; init; }

        public required ForgeWorkspace Workspace { get; init; }

        public required GameContext Context { get; init; }

        public required DetectionResult RawDetection { get; init; }

        public required EngineDetection Detection { get; init; }

        public required BuiltInAdapter Adapter { get; init; }

        /// <summary>没有内置适配器的已知引擎，analyze 自动改走 Ghidra 高级分析。</summary>
        public bool AutoAdvanced { get; init; }
    }

    /// <summary>
    /// 0.2.58：--json 模式下 stdout 必须是纯 JSON。
    /// 先把整段输出收进缓冲，再把人类可读行转到 stderr、只把最后一行 JSON 信封写回 stdout。
    /// </summary>
    public static async Task<int> RunAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!options.Json)
        {
            return await RunCoreAsync(options, stdout, stderr, cancellationToken).ConfigureAwait(false);
        }

        var buffer = new StringWriter();
        var code = await RunCoreAsync(options, buffer, stderr, cancellationToken).ConfigureAwait(false);
        var text = buffer.ToString();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var jsonIndex = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                jsonIndex = i;
                break;
            }
        }

        if (jsonIndex < 0)
        {
            await stdout.WriteAsync(text).ConfigureAwait(false);
            return code;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (i == jsonIndex || lines[i].Length == 0)
            {
                continue;
            }

            await stderr.WriteLineAsync(lines[i]).ConfigureAwait(false);
        }

        await stdout.WriteLineAsync(lines[jsonIndex]).ConfigureAwait(false);
        return code;
    }

    private static async Task<int> RunCoreAsync(
        CliOptions options,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var commandText = CommandText(options.Command);
        try
        {
            var resolved = Resolve(options);
            return options.Command switch
            {
                CliCommand.Analyze => await RunAnalyzeAsync(resolved, options, commandText, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Plan => await RunPlanAsync(resolved, options, commandText, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Generate => await RunGenerateAsync(resolved, options, commandText, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Build => await RunBuildAsync(resolved, options, commandText, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Install => await RunInstallAsync(resolved, options, commandText, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Verify => await RunVerifyAsync(resolved, options, commandText, stdout, stderr, cancellationToken).ConfigureAwait(false),
                CliCommand.Report => await RunReportAsync(resolved, options, commandText, stdout, stderr, cancellationToken).ConfigureAwait(false),
                _ => await WriteFailureAsync(
                    ForgeError.Create(
                        ForgeErrorCodes.NotImplemented,
                        $"命令 {commandText} 尚未接线：当前已接通 detect / analyze / plan / generate / build / install / verify / report。",
                        "GUI 在后续里程碑接线。"),
                    commandText,
                    stdout,
                    stderr,
                    options.Json).ConfigureAwait(false),
            };
        }
        catch (CliFailure failure)
        {
            return await WriteFailureAsync(failure.Error, commandText, stdout, stderr, options.Json).ConfigureAwait(false);
        }
    }

    private static Resolved Resolve(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.GamePath))
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"命令 {CommandText(options.Command)} 需要 --game <游戏目录>。",
                "传入已安装游戏的目录；工具只读该目录，不会写入。"));
        }

        string gameDirectory;
        try
        {
            gameDirectory = Path.GetFullPath(options.GamePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不合法：{options.GamePath}。",
                "传入一个有效的目录路径。"));
        }

        if (!Directory.Exists(gameDirectory))
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"游戏目录不存在：{gameDirectory}。",
                "检查 --game 指向的安装目录，注意空格与引号。"));
        }

        var workspaceRoot = string.IsNullOrWhiteSpace(options.WorkspacePath)
            ? ForgeWorkspace.DefaultRootPath
            : Path.GetFullPath(options.WorkspacePath);

        var slug = SlugFrom(gameDirectory);
        var workspace = new ForgeWorkspace(workspaceRoot, gameDirectory);

        var ensure = workspace.EnsureCreated();
        if (!ensure.Ok)
        {
            throw new CliFailure(ensure.Error!);
        }

        var layout = workspace.EnsureGameLayout(slug);
        if (!layout.Ok)
        {
            throw new CliFailure(layout.Error!);
        }

        var loadProfile = ProfileStore.LoadPair(workspace.ProfilePath(slug), workspace.OverridesPath(slug));
        if (!loadProfile.Ok)
        {
            throw new CliFailure(loadProfile.Error!);
        }

        var profile = loadProfile.Value ?? new ProfileDocument();
        profile.Slug ??= slug;
        profile.GamePath ??= gameDirectory;
        if (!options.DryRun && !File.Exists(workspace.ProfilePath(slug)))
        {
            ProfileStore.Save(workspace.ProfilePath(slug), profile);
        }

        if (options.OptionOverrides.Count > 0)
        {
            foreach (var pair in options.OptionOverrides)
            {
                profile.Options[pair.Key] = JsonSerializer.SerializeToElement(pair.Value);
            }
        }

        var context = new GameContext
        {
            GameDirectory = gameDirectory,
            Slug = slug,
            WorkspaceRoot = workspaceRoot,
            Profile = profile,
            Advanced = options.Advanced,
            DryRun = options.DryRun,
        };

        DetectionResult raw;
        try
        {
            raw = new EngineDetector().Detect(gameDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.ArgumentInvalid,
                $"检测时目录已不存在：{gameDirectory}。",
                "确认 --game 路径；本命令不会创建目录。"));
        }
        catch (FingerprintFormatException ex)
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                $"内置指纹库损坏或版本不兼容：{ex.Message}",
                "重新安装工具或报告缺陷；这是工具自身问题。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"读取游戏目录失败：{ex.Message}",
                "检查目录权限；游戏目录必须是可读的本地目录。"));
        }

        var engineId = EngineAliasMap.ToContractId(raw.Family);
        var detection = new EngineDetection
        {
            Family = engineId,
            Version = raw.Primary.Version,
            Bitness = raw.Primary.Bitness,
            Confidence = raw.Confidence,
            Evidence = BuildDetectionEvidence(raw, engineId),
        };

        var autoAdvanced = false;
        var adapter = BuiltInAdapters.Find(engineId);
        if (adapter is null)
        {
            // 没有内置适配器时，analyze --advanced 允许 Ghidra 原生兜底：
            //   - 已识别但没有适配器的引擎（如 kirikiriz）；
            //   - 未识别但根目录确实有 exe 的目录（空目录仍按 E_ENGINE_UNSUPPORTED 处理）。
            var hasRootExecutable = Directory.EnumerateFiles(gameDirectory, "*.exe", SearchOption.TopDirectoryOnly).Any();
            // report 允许无适配器解析：只要有之前 analyze --advanced 留下的产物，就能导出报告。
            var allowAdapterless = (options.Command == CliCommand.Analyze && options.Advanced)
                || options.Command == CliCommand.Report;
            // 已识别的引擎但没有适配器：analyze 不必让用户先知道 --advanced，
            // 直接自动改走 Ghidra 高级分析（只读、诚实产出报告），并在 stderr 说明改道。
            if (!options.Advanced
                && options.Command is CliCommand.Analyze or CliCommand.Report
                && hasRootExecutable)
            {
                autoAdvanced = true;
            }

            if ((allowAdapterless && (raw.IsKnown || hasRootExecutable)) || autoAdvanced)
            {
                adapter = new BuiltInAdapter
                {
                    EngineId = engineId,
                    Name = "Ghidra 原生兜底",
                    Version = "ghidra",
                };
            }
            else
            {
                throw new CliFailure(ForgeError.Create(
                    raw.IsKnown ? ForgeErrorCodes.NotImplemented : ForgeErrorCodes.EngineUnsupported,
                    raw.IsKnown
                        ? $"识别为 {engineId}，但该引擎的适配器尚未接线（当前已内置：{string.Join("、", BuiltInAdapters.EngineIds)}）。"
                        : "未能识别引擎（unknown）；没有可用的分析适配器。",
                    raw.IsKnown
                        ? "该引擎暂无内置适配器：可先用 `analyze --advanced` 跑 Ghidra 静态分析，再用 `report` 导出报告；真正可玩仍需运行时方案或人工分析。也可用 detect --json 查看证据。"
                        : "确认 --game 指向游戏根目录；可运行 detect --json 查看扫描统计与候选。"));
            }
        }

        return new Resolved
        {
            GameDirectory = gameDirectory,
            WorkspaceRoot = workspaceRoot,
            Slug = slug,
            Workspace = workspace,
            Context = context,
            RawDetection = raw,
            Detection = detection,
            Adapter = adapter,
            AutoAdvanced = autoAdvanced,
        };
    }

    private static async Task<int> RunAnalyzeAsync(
        Resolved resolved,
        CliOptions options,
        string commandText,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var hasAdapterAnalyzer = resolved.Adapter.Analyzer is not null;
        if (resolved.AutoAdvanced && !options.Advanced)
        {
            await stderr.WriteLineAsync(
                "该引擎没有内置适配器，已自动改用 Ghidra 高级分析（只读静态分析；加 --advanced 可显式指定，用 --option ghidra.timeoutSeconds 调整超时）。").ConfigureAwait(false);
        }

        var (analysis, ghidra) = await AnalyzeWithOptionalGhidraAsync(
            resolved,
            options,
            cancellationToken).ConfigureAwait(false);

        var data = new Dictionary<string, object?>
        {
            ["game"] = resolved.GameDirectory,
            ["workspace"] = resolved.Workspace.RootPath,
            ["slug"] = resolved.Slug,
            ["engine"] = resolved.Detection.Family,
            ["engineVersion"] = resolved.Detection.Version,
            ["bitness"] = resolved.Detection.Bitness,
            ["confidence"] = resolved.Detection.Confidence,
            ["candidates"] = DescribeCandidates(analysis),
            ["coverage"] = DescribeCoverage(analysis.Coverage),
            ["risks"] = analysis.Risks,
            ["unverified"] = analysis.Unverified,
            ["autoAdvanced"] = resolved.AutoAdvanced,
            ["dryRun"] = options.DryRun,
        };

        if (ghidra is not null)
        {
            data["ghidra"] = ghidra.Section;
        }

        var high = analysis.Candidates.Count(c => c.Score >= 70);
        var medium = analysis.Candidates.Count(c => c.Score is >= 50 and < 70);
        var low = analysis.Candidates.Count(c => c.Score < 50);
        var analyzeSummary = ghidra is { HardFailure: true } && !hasAdapterAnalyzer
            ? $"[部分完成] 分析未完成：{analysis.Candidates.Count} 个文本候选（Ghidra 高级分析失败，结果不完整）。"
            : $"[完成] 分析完成：{analysis.Candidates.Count} 个文本候选（高 {high} / 中 {medium} / 低 {low}）。";
        await stdout.WriteLineAsync(analyzeSummary).ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"  引擎：{resolved.Detection.Family} {resolved.Detection.Version ?? "版本未识别"}（{DescribeBitness(resolved.Detection.Bitness)}，置信度 {resolved.Detection.Confidence:P0}）").ConfigureAwait(false);
        if (ghidra is not null)
        {
            await stdout.WriteLineAsync($"  [高级分析] {ghidra.HumanLine}").ConfigureAwait(false);
        }

        await stdout.WriteLineAsync(
            $"  覆盖：{analysis.Coverage.Covered.Count} 条 / 未覆盖 {analysis.Coverage.NotCovered.Count} 条 / 人工待办 {analysis.Coverage.ManualTodo.Count} 条 / 未验证 {analysis.Unverified.Count} 条").ConfigureAwait(false);
        foreach (var candidate in analysis.Candidates.OrderByDescending(c => c.Score).Take(8))
        {
            await stdout.WriteLineAsync($"    {candidate.Id} {candidate.Kind.ToString().ToLowerInvariant()} {candidate.Score:F0} 分  {candidate.Source}").ConfigureAwait(false);
        }

        if (!options.DryRun)
        {
            await stdout.WriteLineAsync($"  输出：{Path.Combine(resolved.Workspace.StageDirectory(resolved.Slug, PipelineStage.Analyze), "analysis.json")}").ConfigureAwait(false);
        }

        if (ghidra is { HardFailure: true })
        {
            if (!hasAdapterAnalyzer)
            {
                var error = ForgeError.Create(
                    ghidra.ErrorCode ?? ForgeErrorCodes.Partial,
                    ghidra.ErrorMessage ?? "Ghidra 高级分析失败。",
                    ghidra.Suggestion);
                return await WriteFailureAsync(error, commandText, stdout, stderr, options.Json, data).ConfigureAwait(false);
            }

            await stderr.WriteLineAsync(
                $"[警告] 高级分析未完成：{ghidra.ErrorMessage} 适配器结果仍然有效，但原生候选缺失。").ConfigureAwait(false);
        }

        return await WriteSuccessAsync(commandText, $"分析完成：{analysis.Candidates.Count} 个候选。", data, stdout, stderr, options.Json).ConfigureAwait(false);
    }

    private static async Task<int> RunPlanAsync(
        Resolved resolved,
        CliOptions options,
        string commandText,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(resolved, options, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(resolved, options, analysis, cancellationToken).ConfigureAwait(false);
        await WarnIfNoTextHooksAsync(plan, stderr).ConfigureAwait(false);
        await WarnIfNoDialogueEntryAsync(plan, stderr).ConfigureAwait(false);

        var voiceWarning = JapaneseVoiceNotice.ShouldWarn(
            GalA11yForge.Core.Tools.VoiceProbe.Probe().HasJapanese,
            analysis.Candidates.SelectMany(candidate => candidate.Evidence));
        if (voiceWarning)
        {
            await stderr.WriteLineAsync("[注意] " + JapaneseVoiceNotice.Text).ConfigureAwait(false);
        }

        var data = new Dictionary<string, object?>
        {
            ["japaneseVoiceMissing"] = voiceWarning,
            ["engine"] = resolved.Detection.Family,
            ["slug"] = resolved.Slug,
            ["route"] = plan.Route.ToString(),
            ["confidence"] = plan.Confidence,
            ["patchPoints"] = plan.PatchPoints,
            ["artifacts"] = plan.Artifacts,
            ["risks"] = plan.Risks,
            ["manualTodo"] = plan.ManualTodo,
            ["noDialogueEntry"] = PlanQualityInspector.HasNoDialogueEntryRisk(plan),
            ["dryRun"] = options.DryRun,
        };

        await stdout.WriteLineAsync(
            $"[完成] 补丁方案：route={plan.Route.ToString().ToLowerInvariant()}，{plan.PatchPoints.Count} 个补丁点，{plan.Artifacts.Count} 个生成物。").ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"  目标：{plan.Target.Engine} {plan.Target.EngineVersion ?? "版本未识别"}（{DescribeBitness(plan.Target.Bitness)}），规划置信度 {plan.Confidence:P0}").ConfigureAwait(false);
        foreach (var point in plan.PatchPoints.Take(8))
        {
            await stdout.WriteLineAsync($"    {point.Id} {point.Kind.ToString().ToLowerInvariant()} {point.Confidence:P0}  {point.Target}").ConfigureAwait(false);
        }

        if (plan.ManualTodo.Count > 0)
        {
            await stdout.WriteLineAsync($"  人工待办：{plan.ManualTodo.Count} 条（阻断 {plan.ManualTodo.Count(t => t.Blocking)} 条）").ConfigureAwait(false);
        }

        if (plan.Risks.Count > 0)
        {
            await stdout.WriteLineAsync($"  风险：{plan.Risks.Count} 条（高风险 {plan.Risks.Count(r => r.Severity == RiskSeverity.High)} 条）").ConfigureAwait(false);
            foreach (var risk in plan.Risks.Where(r => r.Severity == RiskSeverity.High).Take(3))
            {
                await stdout.WriteLineAsync($"    [{risk.Id}] {risk.Description}").ConfigureAwait(false);
            }
        }

        if (!options.DryRun)
        {
            await stdout.WriteLineAsync($"  输出：{Path.Combine(resolved.Workspace.StageDirectory(resolved.Slug, PipelineStage.Plan), "plan.json")}").ConfigureAwait(false);
        }

        return await WriteSuccessAsync(commandText, $"方案完成：{plan.PatchPoints.Count} 个补丁点。", data, stdout, stderr, options.Json).ConfigureAwait(false);
    }

    private static async Task<int> RunGenerateAsync(
        Resolved resolved,
        CliOptions options,
        string commandText,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(resolved, options, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(resolved, options, analysis, cancellationToken).ConfigureAwait(false);
        await WarnIfNoTextHooksAsync(plan, stderr).ConfigureAwait(false);
        await WarnIfNoDialogueEntryAsync(plan, stderr).ConfigureAwait(false);

        var outputDirectory = ResolveGenerationDirectory(resolved, options);

        if (options.DryRun)
        {
            var dryData = new Dictionary<string, object?>
            {
                ["engine"] = resolved.Detection.Family,
                ["route"] = plan.Route.ToString(),
                ["outputDirectory"] = outputDirectory,
                ["artifacts"] = plan.Artifacts,
                ["dryRun"] = true,
            };
            await stdout.WriteLineAsync($"[完成] dry-run：已得到方案，未写任何文件（计划 {plan.Artifacts.Count} 个生成物）。").ConfigureAwait(false);
            return await WriteSuccessAsync(commandText, "dry-run 完成，未写文件。", dryData, stdout, stderr, options.Json).ConfigureAwait(false);
        }

        var generation = await GenerateProjectAsync(resolved, options, plan, outputDirectory, cancellationToken).ConfigureAwait(false);
        WriteStage(resolved, PipelineStage.Generate, "generation.json", generation, options.DryRun);

        var files = CountGeneratedFiles(generation);
        var data = new Dictionary<string, object?>
        {
            ["engine"] = resolved.Detection.Family,
            ["route"] = plan.Route.ToString(),
            ["projectDirectory"] = generation.ProjectDirectory,
            ["artifacts"] = generation.Artifacts,
            ["files"] = files,
            ["metadata"] = generation.Metadata,
        };

        await stdout.WriteLineAsync($"[完成] 已生成补丁工程：{generation.ProjectDirectory}（{files} 个文件）。").ConfigureAwait(false);
        foreach (var artifact in generation.Artifacts.Take(10))
        {
            await stdout.WriteLineAsync($"    {artifact.RelativePath}  {artifact.Description ?? string.Empty}").ConfigureAwait(false);
        }

        await stdout.WriteLineAsync("  下一步：forge build（Unity Mono 需要 BepInEx 离线包）→ forge install → forge verify。").ConfigureAwait(false);
        return await WriteSuccessAsync(commandText, $"生成完成：{files} 个文件。", data, stdout, stderr, options.Json).ConfigureAwait(false);
    }

    private static string ResolveGenerationDirectory(Resolved resolved, CliOptions options)
        => string.IsNullOrWhiteSpace(options.OutputPath)
            ? resolved.Workspace.StageDirectory(resolved.Slug, PipelineStage.Generate)
            : Path.GetFullPath(options.OutputPath);

    private static async Task<GenerationResult> GenerateProjectAsync(
        Resolved resolved,
        CliOptions options,
        PatchPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var generator = resolved.Adapter.Generator ?? throw new CliFailure(ForgeError.Create(
            ForgeErrorCodes.NotImplemented,
            $"引擎 {resolved.Detection.Family} 的生成器尚未接线。",
            "当前可用：Ren'Py。"));

        var generated = await generator.GenerateAsync(resolved.Context, plan, outputDirectory, cancellationToken).ConfigureAwait(false);
        if (!generated.Ok || generated.Value is null)
        {
            throw new CliFailure(generated.Error ?? ForgeError.Create(
                ForgeErrorCodes.Internal,
                "生成失败但没有错误信息。",
                null));
        }

        return generated.Value;
    }

    private static int CountGeneratedFiles(GenerationResult generation)
        => generation.Artifacts.Count > 0
            ? generation.Artifacts.Count
            : (Directory.Exists(generation.ProjectDirectory)
                ? Directory.GetFiles(generation.ProjectDirectory, "*", SearchOption.AllDirectories).Length
                : 0);

    private static async Task<int> RunBuildAsync(
        Resolved resolved,
        CliOptions options,
        string commandText,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(resolved, options, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(resolved, options, analysis, cancellationToken).ConfigureAwait(false);
        await WarnIfNoTextHooksAsync(plan, stderr).ConfigureAwait(false);
        await WarnIfNoDialogueEntryAsync(plan, stderr).ConfigureAwait(false);
        EnsureLoaderCompat(resolved, stderr);

        if (options.DryRun)
        {
            var dryData = new Dictionary<string, object?>
            {
                ["engine"] = resolved.Detection.Family,
                ["route"] = plan.Route.ToString(),
                ["artifacts"] = plan.Artifacts,
                ["dryRun"] = true,
            };
            await stdout.WriteLineAsync("[完成] dry-run：分析与方案已完成；未生成、未构建。").ConfigureAwait(false);
            return await WriteSuccessAsync(commandText, "dry-run 完成，未构建。", dryData, stdout, stderr, options.Json).ConfigureAwait(false);
        }

        var isIl2Cpp = IsUnityIl2Cpp(resolved);
        if (resolved.Adapter.Builder is null && !IsUnityMono(resolved) && !isIl2Cpp)
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.NotImplemented,
                $"引擎 {resolved.Detection.Family} 没有 dotnet 构建器（Ren'Py 走覆盖层部署，不需要编译）。",
                "Ren'Py 路线请用 install（覆盖层安装已接）；Unity Mono 用 build。"));
        }

        if (isIl2Cpp && resolved.Adapter.RequiresIl2CppInterop)
        {
            // 前置闸门：没有 BepInEx/interop 就没有 Il2CppInterop 引用，构建必失败；
            // 与其让 dotnet 报一堆 CS0246，不如在这里给可执行指引（E_DEPENDENCY_MISSING）。
            var interop = Il2CppInteropProbe.RequireForBuild(resolved.GameDirectory);
            if (!interop.Ok || interop.Value is null)
            {
                throw new CliFailure(interop.Error!);
            }

            await stdout.WriteLineAsync(
                $"[准备] IL2CPP interop 就绪：{interop.Value.InteropAssemblyCount} 个 DLL / {interop.Value.InteropTotalBytes:N0} B（{interop.Value.InteropDirectory}）。").ConfigureAwait(false);
        }

        var generation = await GenerateProjectAsync(
            resolved,
            options,
            plan,
            ResolveGenerationDirectory(resolved, options),
            cancellationToken).ConfigureAwait(false);
        WriteStage(resolved, PipelineStage.Generate, "generation.json", generation, options.DryRun);

        var build = await BuildOnceAsync(resolved, options, generation, cancellationToken).ConfigureAwait(false);
        WriteStage(resolved, PipelineStage.Build, "build.json", build, options.DryRun);

        var data = new Dictionary<string, object?>
        {
            ["engine"] = resolved.Detection.Family,
            ["outputDirectory"] = build.OutputDirectory,
            ["artifacts"] = build.Artifacts,
            ["logPath"] = build.LogPath,
            ["metadata"] = build.Metadata,
        };

        await stdout.WriteLineAsync($"[完成] 构建完成：{build.OutputDirectory}（{build.Artifacts.Count} 个产物）。").ConfigureAwait(false);
        foreach (var artifact in build.Artifacts.Take(10))
        {
            await stdout.WriteLineAsync($"    {artifact}").ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(build.LogPath))
        {
            await stdout.WriteLineAsync($"  日志：{build.LogPath}").ConfigureAwait(false);
        }

        await stdout.WriteLineAsync("  下一步：forge install（需要 BepInEx 离线包）→ forge verify。").ConfigureAwait(false);
        return await WriteSuccessAsync(commandText, $"构建完成：{build.Artifacts.Count} 个产物。", data, stdout, stderr, options.Json).ConfigureAwait(false);
    }

    private static async Task<int> RunInstallAsync(
        Resolved resolved,
        CliOptions options,
        string commandText,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(resolved, options, cancellationToken).ConfigureAwait(false);
        var plan = await PlanAsync(resolved, options, analysis, cancellationToken).ConfigureAwait(false);
        await WarnIfNoTextHooksAsync(plan, stderr).ConfigureAwait(false);
        await WarnIfNoDialogueEntryAsync(plan, stderr).ConfigureAwait(false);
        EnsureLoaderCompat(resolved, stderr);

        if (options.DryRun)
        {
            var dryData = new Dictionary<string, object?>
            {
                ["engine"] = resolved.Detection.Family,
                ["route"] = plan.Route.ToString(),
                ["dryRun"] = true,
                ["note"] = "dry-run 不生成、不构建、不写游戏目录。",
            };
            await stdout.WriteLineAsync("[完成] dry-run：只做到方案；未生成、未构建、未写游戏目录。").ConfigureAwait(false);
            return await WriteSuccessAsync(commandText, "dry-run 完成，未部署。", dryData, stdout, stderr, options.Json).ConfigureAwait(false);
        }

        // 0.2.49 闸门：计划里没有任何正文入口时不装（否则就是“安装成功但一句台词都不读”，
        // 用户已经踩过一次）。确认要冒险时加 --force。
        if (PlanQualityInspector.HasNoDialogueEntryRisk(plan) && !options.Force)
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.Partial,
                "这个游戏的计划里没有识别出正文入口（台词管线）：现在安装大概率读不到剧情，为免误导先不装。",
                "看 plan 里的 R-106 与人工待办；确认要冒险安装时加 --force，并在装完后核对 BepInEx\\LogOutput.log 里的 [Speech] 行。"));
        }

        var isRenpy = IsRenpy(resolved);
        var isIl2Cpp = IsUnityIl2Cpp(resolved);
        var hasDeployer = resolved.Adapter.Deployer is not null || resolved.Adapter.DeployerFactory is not null;
        if (!isRenpy
            && (resolved.Adapter.Builder is null || !hasDeployer)
            && !IsUnityMono(resolved)
            && !isIl2Cpp)
        {
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.NotImplemented,
                $"引擎 {resolved.Detection.Family} 的构建/部署链路尚未接线（Ren'Py 走文件覆盖层，当前生成物在 03-generate，可按 README 手工复制并保留基线备份）。",
                "先检查生成的 README-A11Y.txt；Unity Mono / IL2CPP 的 build/install 已接 CLI。"));
        }

        var generation = await GenerateProjectAsync(
            resolved,
            options,
            plan,
            ResolveGenerationDirectory(resolved, options),
            cancellationToken).ConfigureAwait(false);
        WriteStage(resolved, PipelineStage.Generate, "generation.json", generation, options.DryRun);

        // Ren'Py 覆盖层不需要编译：跳过 build，直接把生成物当部署来源。
        var build = isRenpy
            ? new BuildResult
            {
                OutputDirectory = generation.ProjectDirectory,
                Metadata = generation.Metadata,
            }
            : await BuildOnceAsync(resolved, options, generation, cancellationToken).ConfigureAwait(false);
        WriteStage(resolved, PipelineStage.Build, "build.json", build, options.DryRun);

        var deploymentOptions = new DeploymentOptions
        {
            AllowRedeploy = options.Force,
            PluginVersion = build.Metadata.TryGetValue("version", out var version) ? version : null,
            SpeechRuntimeDirectory = options.SpeechRuntimePath,
        };
        if (isIl2Cpp)
        {
            // IL2CPP 用 BepInEx 6：显式 --bepinex 最优先；没给就按“工作区 + forge.exe 旁
            // bepinex6\”自动发现。发布包自带 app\bepinex6\ 时用户不必再指定路径。
            deploymentOptions.BepInExPackagePath = ResolveBepInEx6Package(resolved, options);
        }
        else if (!isRenpy)
        {
            // 与 build 阶段共用同一套“工作区 + exe 旁 bepinex/”查找逻辑，
            // 这样发布包里自带 app\bepinex\ 时 install 不必再传 --bepinex。
            var package = ResolveBepInExPackage(resolved, options);
            if (!package.Ok || string.IsNullOrWhiteSpace(package.Value))
            {
                throw new CliFailure(package.Error ?? ForgeError.Create(
                    ForgeErrorCodes.DependencyMissing,
                    "找不到 BepInEx 离线包，无法部署。",
                    "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\ 目录，或用 --bepinex <zip> 指定路径。"));
            }

            deploymentOptions.BepInExPackagePath = package.Value;
        }

        if (isIl2Cpp)
        {
            // IL2CPP 固定 BepInEx 6.0.0-be.788（含 Il2CppInterop 与 dotnet/ 运行时），
            // 与 Mono 的 BepInEx 5 包是两条独立链路，哈希也各自 pin。
            deploymentOptions.BepInExVersion = BepInEx6Package.Version;
            deploymentOptions.BepInExSha256 = BepInEx6Package.PinnedSha256;
        }
        IDeployer deployer;
        if (isRenpy)
        {
            deployer = new OverlayDeployer(
                resolved.Workspace,
                new OverlayDeployOptions
                {
                    AllowRedeploy = options.Force,
                    GenerationDirectory = generation.ProjectDirectory,
                });
        }
        else
        {
            var adapterDeployer = resolved.Adapter.Deployer;
            if (adapterDeployer is null)
            {
                if (resolved.Adapter.DeployerFactory is not null)
                {
                    adapterDeployer = resolved.Adapter.DeployerFactory(resolved.Workspace, deploymentOptions);
                }
                else if (IsUnityMono(resolved))
                {
                    adapterDeployer = new PluginDeployer(resolved.Workspace, deploymentOptions);
                }
                else
                {
                    throw new CliFailure(ForgeError.Create(
                        ForgeErrorCodes.NotImplemented,
                        $"引擎 {resolved.Detection.Family} 没有部署器。",
                        "目前只有 Unity Mono / IL2CPP / Ren'Py 有内置部署器。"));
                }
            }

            deployer = adapterDeployer;
        }

        var deployed = await deployer.DeployAsync(resolved.Context, build, cancellationToken).ConfigureAwait(false);
        if (!deployed.Ok || deployed.Value is null)
        {
            throw new CliFailure(deployed.Error ?? ForgeError.Create(ForgeErrorCodes.Internal, "部署失败但没有错误信息。", null));
        }

        WriteStage(resolved, PipelineStage.Deploy, "deploy.json", deployed.Value, options.DryRun);

        var deployedManifest = deployed.Value.Manifest;
        var manifestPath = InstallManifestStore.DefaultPath(resolved.Workspace, resolved.Slug);
        var data = new Dictionary<string, object?>
        {
            ["engine"] = resolved.Detection.Family,
            ["deploymentId"] = deployedManifest.DeploymentId,
            ["manifestPath"] = manifestPath,
            ["backupDirectory"] = deployed.Value.BackupDirectory,
            ["installedFiles"] = deployedManifest.Files.Count,
            ["dryRun"] = false,
        };

        await stdout.WriteLineAsync($"[完成] 已安装：{deployedManifest.Files.Count} 个文件操作，备份在 {deployed.Value.BackupDirectory}。").ConfigureAwait(false);
        await stdout.WriteLineAsync($"  安装清单：{manifestPath}").ConfigureAwait(false);
        await stdout.WriteLineAsync("  下一步：启动游戏人工确认能读；然后 forge verify；出问题用 forge verify --rollback 回滚。").ConfigureAwait(false);
        var cachedPlan = TryReadStage<PatchPlan>(resolved, PipelineStage.Plan, "plan.json");
        var hookCount = cachedPlan?.PatchPoints.Count(point => point.Kind == PatchPointKind.Hook) ?? 0;
        var hasWatcherFallback = string.Equals(resolved.Detection.Family, "unity_mono", StringComparison.Ordinal);
        var message = InstallNotice.Compose(isIl2Cpp, options.SpeechRuntimePath, data, hookCount, hasWatcherFallback, resolved.GameDirectory, resolved.Detection.Family);
        var cachedAnalysis = TryReadStage<AnalysisReport>(resolved, PipelineStage.Analyze, "analysis.json");
        if (cachedAnalysis is not null)
        {
            var evidence = cachedAnalysis.Candidates.SelectMany(candidate => candidate.Evidence);
            if (JapaneseVoiceNotice.ShouldWarn(GalA11yForge.Core.Tools.VoiceProbe.Probe().HasJapanese, evidence))
            {
                data["japaneseVoiceMissing"] = true;
                await stderr.WriteLineAsync("[注意] " + JapaneseVoiceNotice.Text).ConfigureAwait(false);
            }
        }

        return await WriteSuccessAsync(commandText, message, data, stdout, stderr, options.Json).ConfigureAwait(false);
    }

    /// <summary>账本存在不等于补丁还在：核对清单里“本工具新增”的文件是否还存在且哈希一致。</summary>
    private static bool IsPatchInstalled(Resolved resolved, string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        var loaded = InstallManifestStore.Load(resolved.Workspace, manifestPath);
        if (!loaded.Ok || loaded.Value is null || loaded.Value.Files.Count == 0)
        {
            return false;
        }

        foreach (var entry in loaded.Value.Files)
        {
            if (entry.Action != DeployFileAction.Add)
            {
                continue;
            }

            var path = Path.Combine(resolved.GameDirectory, entry.RelativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Sha256After)
                || FileHashes.MatchesFile(path, entry.Sha256After))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<int> RunVerifyAsync(
        Resolved resolved,
        CliOptions options,
        string commandText,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var manifestPath = InstallManifestStore.DefaultPath(resolved.Workspace, resolved.Slug);
        var mode = options.Rollback ? VerifyMode.Rollback : VerifyMode.Install;
        var isRenpy = IsRenpy(resolved);

        if (mode == VerifyMode.Install && !IsPatchInstalled(resolved, manifestPath))
        {
            // 回滚后账本文件仍保留（用于追溯），所以不能只看清单存在与否；
            // 这里核对“清单中本工具新增的文件是否还在、且哈希一致”，再决定是否跑安装后自检。
            var notInstalled = new Dictionary<string, object?>
            {
                ["engine"] = resolved.Detection.Family,
                ["mode"] = "install",
                ["overall"] = "not_installed",
                ["checks"] = 0,
                ["failed"] = 0,
                ["unverified"] = 1,
                ["note"] = "没有检测到已安装的补丁（未安装或已回滚）；跳过安装后自检。",
            };
            await stdout.WriteLineAsync("[提示] 当前没有检测到已安装的补丁（可能未安装或已回滚），跳过安装后自检；这不等于通过。").ConfigureAwait(false);
            await stdout.WriteLineAsync($"  清单：{manifestPath}").ConfigureAwait(false);
            return await WriteSuccessAsync(commandText, "未安装补丁：跳过安装后自检。", notInstalled, stdout, stderr, options.Json).ConfigureAwait(false);
        }

        if (options.Rollback)
        {
            if (!File.Exists(manifestPath))
            {
                throw new CliFailure(ForgeError.Create(
                    ForgeErrorCodes.ArgumentInvalid,
                    $"找不到安装清单，无法回滚：{manifestPath}",
                    "先运行一次 forge install；或确认工作区指向了正确的 --workspace。"));
            }

            var rollbackOptions = new RollbackOptions { Force = options.Force };
            var isIl2Cpp = IsUnityIl2Cpp(resolved);
            DeploymentOptions RollbackDeploymentOptions()
            {
                var options2 = new DeploymentOptions
                {
                    BepInExPackagePath = options.BepInExPath,
                    SpeechRuntimeDirectory = options.SpeechRuntimePath,
                };
                if (isIl2Cpp)
                {
                    options2.BepInExVersion = BepInEx6Package.Version;
                    options2.BepInExSha256 = BepInEx6Package.PinnedSha256;
                }

                return options2;
            }

            var rolledBack = isRenpy
                ? await new OverlayDeployer(resolved.Workspace)
                    .RollbackFromFileAsync(resolved.Context, manifestPath, rollbackOptions, cancellationToken)
                    .ConfigureAwait(false)
                : isIl2Cpp
                    ? await new Il2CppPluginDeployer(resolved.Workspace, RollbackDeploymentOptions())
                        .RollbackFromFileAsync(resolved.Context, manifestPath, rollbackOptions, cancellationToken)
                        .ConfigureAwait(false)
                    : await new PluginDeployer(resolved.Workspace, RollbackDeploymentOptions())
                        .RollbackFromFileAsync(resolved.Context, manifestPath, rollbackOptions, cancellationToken)
                        .ConfigureAwait(false);
            if (!rolledBack.Ok || rolledBack.Value is null)
            {
                throw new CliFailure(rolledBack.Error ?? ForgeError.Create(ForgeErrorCodes.Internal, "回滚失败但没有错误信息。", null));
            }

            var report = rolledBack.Value;
            WriteStage(resolved, PipelineStage.Verify, "rollback.json", report, options.DryRun);
            await stdout.WriteLineAsync(
                $"[完成] 回滚：还原 {report.RestoredFiles.Count} / 删除 {report.DeletedFiles.Count} / 清理目录 {report.CleanedDirectories.Count}，哈希不一致 {report.HashMismatches.Count}。").ConfigureAwait(false);
        }

        var service = new VerificationService();
        var verify = await service.VerifyAsync(
            new VerifyOptions
            {
                GameDirectory = resolved.GameDirectory,
                WorkspaceRoot = resolved.WorkspaceRoot,
                Slug = resolved.Slug,
                ManifestPath = manifestPath,
                Mode = mode,
                InstallerKind = isRenpy ? VerifyInstallerKind.Overlay : VerifyInstallerKind.Auto,
                ProbeEnvironment = true,
                SpeechLogPath = options.SpeechLogPath,
            },
            cancellationToken).ConfigureAwait(false);
        if (!verify.Ok || verify.Value is null)
        {
            throw new CliFailure(verify.Error ?? ForgeError.Create(ForgeErrorCodes.Internal, "初检失败但没有错误信息。", null));
        }

        var verifyReport = verify.Value;
        var writer = new VerifyReportWriter(resolved.GameDirectory);
        var write = await writer.WriteAsync(
            verifyReport,
            resolved.Workspace.StageDirectory(resolved.Slug, PipelineStage.Verify),
            cancellationToken).ConfigureAwait(false);
        if (!write.Ok || write.Value is null)
        {
            throw new CliFailure(write.Error ?? ForgeError.Create(ForgeErrorCodes.Io, "初检报告写入失败。", null));
        }

        string? archivedMarkdown = null;
        try
        {
            var directory = Path.GetDirectoryName(write.Value.MarkdownPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                archivedMarkdown = Path.Combine(directory, $"verify-{stamp}.md");
                File.Copy(write.Value.MarkdownPath, archivedMarkdown, overwrite: false);
            }
        }
        catch (IOException)
        {
            // 存档失败不影响主报告；数据里如实留 null。
            archivedMarkdown = null;
        }

        var failed = verifyReport.FailCount;
        var data = new Dictionary<string, object?>
        {
            ["engine"] = resolved.Detection.Family,
            ["mode"] = mode.ToString().ToLowerInvariant(),
            ["installer"] = isRenpy ? "overlay" : "bepinex",
            ["overall"] = verifyReport.Overall,
            ["checks"] = verifyReport.Checks.Count,
            ["failed"] = failed,
            ["unverified"] = verifyReport.Unverified,
            ["jsonPath"] = write.Value.JsonPath,
            ["markdownPath"] = write.Value.MarkdownPath,
            ["archivedMarkdownPath"] = archivedMarkdown,
        };

        // 语音证据：把 s-01..s-04 全部抬出来（0.2.70 起；以前只回显 s-02），GUI/脚本能看到完整语音块。
        var speechCheck = verifyReport.Checks.FirstOrDefault(
            static check => string.Equals(check.Id, "s-02", StringComparison.OrdinalIgnoreCase));
        var speechChecks = verifyReport.Checks
            .Where(static check => check.Id is not null && check.Id.StartsWith("s-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (speechCheck is not null || speechChecks.Count > 0)
        {
            var speechMetrics = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var check in speechChecks)
            {
                if (check.Metrics is null)
                {
                    continue;
                }

                foreach (var metric in check.Metrics)
                {
                    speechMetrics[metric.Key] = metric.Value;
                }
            }

            // 指标实际挂在报告层（VerificationRun.Metric 写 Report.Metrics），这里把 speech* 的指标并进来。
            foreach (var metric in verifyReport.Metrics)
            {
                if (metric.Key.StartsWith("speech", StringComparison.OrdinalIgnoreCase))
                {
                    speechMetrics[metric.Key] = metric.Value;
                }
            }

            data["speech"] = new Dictionary<string, object?>
            {
                ["check"] = speechCheck?.Id,
                ["name"] = speechCheck?.Name,
                ["status"] = speechCheck?.Status.ToString().ToLowerInvariant(),
                ["detail"] = speechCheck?.Detail,
                ["metrics"] = speechMetrics,
                ["allChecks"] = speechChecks.Select(static check => new Dictionary<string, object?>
                {
                    ["check"] = check.Id,
                    ["name"] = check.Name,
                    ["status"] = check.Status.ToString().ToLowerInvariant(),
                    ["detail"] = check.Detail,
                }).ToList(),
            };
        }

        await stdout.WriteLineAsync(
            $"[完成] 初检（{mode}）：overall={verifyReport.Overall}，{verifyReport.Checks.Count} 项检查，失败 {failed} 项，未验证 {verifyReport.Unverified.Count} 项。").ConfigureAwait(false);
        await stdout.WriteLineAsync($"  报告：{write.Value.MarkdownPath}").ConfigureAwait(false);
        if (speechCheck is not null)
        {
            await stdout.WriteLineAsync($"  语音证据：{speechCheck.Detail}").ConfigureAwait(false);
        }

        // 用词纪律：pass_with_skips 不等于“通过”；只有全部检查通过才说“通过”。
        var message = verifyReport.Overall switch
        {
            VerifyOverall.Pass => "初检通过（全部检查通过）。",
            VerifyOverall.PassWithSkips => $"初检完成但有 {verifyReport.Unverified.Count} 项未验证，不等于通过；见报告。",
            VerifyOverall.Partial => "初检部分通过：存在非阻断失败项，见报告。",
            _ => "初检未通过：存在阻断失败项，见报告与未验证项。",
        };
        var ok = verifyReport.Overall is VerifyOverall.Pass or VerifyOverall.PassWithSkips;
        var exit = ok ? ExitCodes.Success : ExitCodes.PartialSuccess;
        if (options.Json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(
                new CliResponse
                {
                    Ok = ok,
                    Command = commandText,
                    Code = ok ? null : ForgeErrorCodes.Partial,
                    Message = message,
                    Data = data,
                },
                ForgeJson.Compact)).ConfigureAwait(false);
        }

        return exit;
    }

    private static async Task<BuildResult> BuildOnceAsync(
        Resolved resolved,
        CliOptions options,
        GenerationResult generation,
        CancellationToken cancellationToken)
    {
        var isIl2Cpp = IsUnityIl2Cpp(resolved);
        var buildOptions = new BuildOptions
        {
            Configuration = "Release",
            OutputDirectory = resolved.Workspace.WorkDirectory(resolved.Workspace.NewWorkId("build")),
            LogDirectory = resolved.Workspace.LogsRoot,
            Version = "0.1.0.0",
        };

        if (isIl2Cpp)
        {
            // IL2CPP 的 BepInEx 6 核心与 interop 都在游戏目录里（首次启动生成），
            // 不走 BepInEx 5 的核心暂存；把 GameDir 显式传给生成工程（csproj 里的默认值只是生成时提示）。
            buildOptions.Properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["GameDir"] = resolved.GameDirectory,
            };
        }
        else
        {
            var staging = StageBepInExCore(resolved, options);
            if (staging.Ok && staging.Value is not null)
            {
                buildOptions.Properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BepInExDir"] = staging.Value,
                    ["GameDir"] = resolved.GameDirectory,
                };
            }
            else
            {
                // 暂存失败时唯一还能编译的前提：游戏目录里本来就有 BepInEx core（生成工程默认指向 $(GameDir)\BepInEx\core）。
                // 否则必须诚实失败——硬着头皮编译只会得到一堆 CS0246，让用户误以为是补丁代码坏了。
                var coreInGame = Path.Combine(resolved.GameDirectory, "BepInEx", "core", "BepInEx.dll");
                if (!File.Exists(coreInGame))
                {
                    throw new CliFailure(staging.Error ?? ForgeError.Create(
                        ForgeErrorCodes.DependencyMissing,
                        "找不到 BepInEx 5.4.23.5 离线包，而且游戏目录里也没有可用的 BepInEx core，无法构建补丁。",
                        "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\ 目录，或用 --bepinex <zip> 指定路径。"));
                }

                buildOptions.Properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GameDir"] = resolved.GameDirectory,
                };
            }
        }

        var builder = resolved.Adapter.Builder;
        if (builder is null)
        {
            if (!IsUnityMono(resolved) && !isIl2Cpp)
            {
                throw new CliFailure(ForgeError.Create(
                    ForgeErrorCodes.NotImplemented,
                    $"引擎 {resolved.Detection.Family} 没有构建器。",
                    "当前只有 Unity Mono / IL2CPP 需要构建；Ren'Py 走覆盖层安装。"));
            }

            builder = new PluginBuilder(resolved.Workspace, buildOptions);
        }

        var built = await builder.BuildAsync(resolved.Context, generation, cancellationToken).ConfigureAwait(false);
        if (!built.Ok || built.Value is null)
        {
            throw new CliFailure(built.Error ?? ForgeError.Create(ForgeErrorCodes.Internal, "构建失败但没有错误信息。", null));
        }

        return built.Value;
    }

    private static bool IsUnityMono(Resolved resolved)
        => string.Equals(resolved.Detection.Family, "unity_mono", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnityIl2Cpp(Resolved resolved)
        => string.Equals(resolved.Detection.Family, "unity_il2cpp", StringComparison.OrdinalIgnoreCase)
           || string.Equals(resolved.Adapter.EngineId, "unity_il2cpp", StringComparison.OrdinalIgnoreCase);

    private static bool IsRenpy(Resolved resolved)
        => string.Equals(resolved.Adapter.EngineId, "renpy", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 生成/构建/安装前的加载器兼容性闸门（只对 Unity Mono 生效）。
    /// 背景：R20 实测，厂商裁剪 mscorlib 会缺 Module.GetPEKind，BepInEx 5/6 预加载器必崩，
    /// 换 MelonLoader 也不可用；这种游戏不能假装能自动注入，必须明确拒绝并给人工建议。
    /// </summary>
    private static void EnsureLoaderCompat(Resolved resolved, TextWriter stderr)
    {
        if (!IsUnityMono(resolved))
        {
            return;
        }

        var compat = LoaderCompatProbe.Probe(resolved.GameDirectory);
        if (compat.IsUnsupported)
        {
            var detail = string.Join("；", compat.Evidence.Take(3));
            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.EngineUnsupported,
                $"该游戏裁剪了 .NET 基础库（mscorlib 缺 Module.GetPEKind），BepInEx 5/6 预加载器都会崩溃，无法自动注入。{detail}",
                "这不是工具缺陷，是游戏方裁剪运行库导致；换 BepInEx 6 或 MelonLoader 也已实测不可行。"
                + "请把游戏名/版本反馈给我们；当前版本会拒绝对它生成可安装补丁。详见 research/26-loader-compat/REPORT.md。"));
        }

        if (compat.IsDegraded)
        {
            stderr.WriteLine("[警告] 该游戏裁剪了 Unity API Input.anyKeyDown，生成的键位读取代码可能编译失败（CS0117）；若失败请按生成物 README 的人工待办处理。");
            foreach (var todo in compat.ManualTodos)
            {
                stderr.WriteLine($"[警告] {todo}");
            }
        }
    }

    /// <summary>
    /// 在工作区、exe 所在目录（含单文件发布的真实 exe 目录）和 exe 旁的 bepinex6\ 里
    /// 查找 BepInEx 6 离线包。找不到返回 null，由部署器给出带官方下载地址的诚实错误。
    /// </summary>
    private static string? ResolveBepInEx6Package(Resolved resolved, CliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.BepInExPath))
        {
            return options.BepInExPath;
        }

        var searchRoots = new List<string> { resolved.Workspace.RootPath };
        void AddRoot(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var existing in searchRoots)
            {
                if (string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            searchRoots.Add(directory);
        }

        AddRoot(AppContext.BaseDirectory);
        AddRoot(Path.GetDirectoryName(Environment.ProcessPath));

        var found = BepInEx6Locator.FindCandidates(searchRoots);
        return found.Ok && found.Value is { Count: > 0 } ? found.Value[0] : null;
    }

    /// <summary>
    /// 在工作区、exe 所在目录（含单文件发布的真实 exe 目录）和 exe 旁的 bepinex\ 里查找 BepInEx 离线包。
    /// build 与 install 共用，避免“构建能找到、安装找不到”的不一致。
    /// </summary>
    private static ForgeResult<string> ResolveBepInExPackage(Resolved resolved, CliOptions options)
    {
        var searchRoots = new List<string> { resolved.Workspace.RootPath };
        void AddRoot(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (var existing in searchRoots)
            {
                if (string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            searchRoots.Add(directory);
        }

        AddRoot(AppContext.BaseDirectory);
        AddRoot(Path.GetDirectoryName(Environment.ProcessPath));

        var found = BepInExLocator.FindCandidates(searchRoots, options.BepInExPath);
        if (!found.Ok || found.Value is null || found.Value.Count == 0)
        {
            return ForgeResult<string>.Failure(found.Error ?? ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "找不到 BepInEx 5.4.23.5 离线包。",
                "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\ 目录，或用 --bepinex <zip> 指定路径。"));
        }

        return ForgeResult<string>.Success(found.Value[0]);
    }

    /// <summary>把 BepInEx 离线包解到工作区暂存目录，返回 BepInEx\core 路径；找不到包时返回失败（调用方可决定是否继续）。</summary>
    private static ForgeResult<string> StageBepInExCore(Resolved resolved, CliOptions options)
    {
        var packagePath = ResolveBepInExPackage(resolved, options);
        if (!packagePath.Ok || string.IsNullOrWhiteSpace(packagePath.Value))
        {
            return ForgeResult<string>.Failure(packagePath.Error ?? ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "找不到 BepInEx 5.4.23.5 离线包，无法准备构建依赖。",
                "把 BepInEx_win_x64_5.4.23.5.zip 放进工作区缓存或 forge.exe 旁边的 bepinex\\ 目录，或用 --bepinex <zip> 指定路径。"));
        }

        var package = BepInExPackage.Open(packagePath.Value);
        if (!package.Ok || package.Value is null)
        {
            return ForgeResult<string>.Failure(package.Error ?? ForgeError.Create(
                ForgeErrorCodes.DependencyMissing,
                "BepInEx 离线包无法读取。",
                "重新下载官方 5.4.23.5 win x64 包并校验 SHA-256。"));
        }

        var stagingRoot = resolved.Workspace.WorkDirectory(resolved.Workspace.NewWorkId("bepinex-core"));
        var coreDirectory = Path.Combine(stagingRoot, "BepInEx", "core");
        if (File.Exists(Path.Combine(coreDirectory, "BepInEx.dll")))
        {
            return ForgeResult<string>.Success(coreDirectory);
        }

        try
        {
            Directory.CreateDirectory(stagingRoot);
            foreach (var entry in package.Value.Entries)
            {
                if (!entry.RelativePath.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase)
                    && !entry.RelativePath.StartsWith("BepInEx\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(stagingRoot, entry.RelativePath.Replace('\\', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return ForgeResult<string>.Failure(ForgeError.Create(
                        ForgeErrorCodes.PathUnsafe,
                        $"BepInEx 包里的路径越界：{entry.RelativePath}",
                        "离线包可能损坏或被篡改，重新下载官方包。"));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, package.Value.ReadBytes(entry.RelativePath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.Io,
                $"解压 BepInEx 失败：{ex.Message}",
                "检查工作区磁盘空间与杀软拦截；也可手工解压后把 --bepinex 指向 zip。"));
        }

        if (!File.Exists(Path.Combine(coreDirectory, "BepInEx.dll")))
        {
            return ForgeResult<string>.Failure(ForgeError.Create(
                ForgeErrorCodes.ArtifactMissing,
                "BepInEx 包里没有 BepInEx/core/BepInEx.dll。",
                "确认下载的是 win x64 发行包，不是源码包。"));
        }

        return ForgeResult<string>.Success(coreDirectory);
    }

    private static async Task<int> RunReportAsync(
        Resolved resolved,
        CliOptions options,
        string commandText,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        // 优先复用工作区里的分析/方案产物：既能给无适配器引擎导出报告（Ghidra 兜底），
        // 也能让 report 不必每次重跑 Ghidra。
        var analysis = TryReadStage<AnalysisReport>(resolved, PipelineStage.Analyze, "analysis.json");
        var plan = TryReadStage<PatchPlan>(resolved, PipelineStage.Plan, "plan.json");

        if (analysis is null)
        {
            if (resolved.AutoAdvanced)
            {
                await stderr.WriteLineAsync(
                    "报告需要先分析：该引擎没有内置适配器，已自动改用 Ghidra 高级分析（只读，可能较慢）。").ConfigureAwait(false);
            }

            (analysis, _) = await AnalyzeWithOptionalGhidraAsync(
                resolved,
                options,
                cancellationToken).ConfigureAwait(false);
        }

        if (plan is null)
        {
            plan = resolved.Adapter.Planner is null
                ? BuildAdapterlessPlan(resolved, analysis)
                : await PlanAsync(resolved, options, analysis, cancellationToken).ConfigureAwait(false);
        }

        await WarnIfNoTextHooksAsync(plan, stderr).ConfigureAwait(false);
        await WarnIfNoDialogueEntryAsync(plan, stderr).ConfigureAwait(false);

        var report = ReportDocument.FromAnalysis(analysis, plan);
        report.GameSlug = resolved.Slug;
        report.GamePath = resolved.GameDirectory;
        report.GeneratedAt = DateTimeOffset.Now.ToString("O");

        var outputDirectory = string.IsNullOrWhiteSpace(options.OutputPath)
            ? resolved.Workspace.ReportDirectory(resolved.Slug)
            : Path.GetFullPath(options.OutputPath);

        if (options.DryRun)
        {
            var dryData = new Dictionary<string, object?>
            {
                ["engine"] = resolved.Detection.Family,
                ["route"] = plan.Route.ToString(),
                ["outputDirectory"] = outputDirectory,
                ["dryRun"] = true,
            };
            await stdout.WriteLineAsync($"[完成] dry-run：报告将写到 {outputDirectory}，未写文件。").ConfigureAwait(false);
            return await WriteSuccessAsync(commandText, "dry-run 完成，未写报告。", dryData, stdout, stderr, options.Json).ConfigureAwait(false);
        }

        Directory.CreateDirectory(outputDirectory);
        var writer = new JsonMarkdownReportWriter(gameDirectory: resolved.GameDirectory);
        var written = await writer.WriteAsync(report, outputDirectory, cancellationToken).ConfigureAwait(false);
        if (!written.Ok || written.Value is null)
        {
            throw new CliFailure(written.Error ?? ForgeError.Create(ForgeErrorCodes.Internal, "报告写出失败但没有错误信息。", null));
        }

        var humanTodosPath = Path.Combine(outputDirectory, "human-todos.md");
        var todos = new StringBuilder();
        todos.AppendLine("# 人工待办（human todos）").AppendLine();
        if (plan.ManualTodo.Count == 0)
        {
            todos.AppendLine("（无显式人工待办；这不等于全部已验证）");
        }
        else
        {
            foreach (var todo in plan.ManualTodo.OrderByDescending(todo => todo.Blocking).ThenBy(todo => todo.Id, StringComparer.Ordinal))
            {
                var detail = string.IsNullOrWhiteSpace(todo.Detail) ? string.Empty : $"：{todo.Detail}";
                todos.AppendLine($"- [{(todo.Blocking ? "阻断" : "建议")}] {todo.Id} {todo.Title}{detail}");
            }
        }

        await File.WriteAllTextAsync(humanTodosPath, todos.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var data = new Dictionary<string, object?>
        {
            ["engine"] = resolved.Detection.Family,
            ["route"] = plan.Route.ToString(),
            ["jsonPath"] = written.Value.JsonPath,
            ["markdownPath"] = written.Value.MarkdownPath,
            ["humanTodosPath"] = humanTodosPath,
            ["humanTodoCount"] = plan.ManualTodo.Count,
            ["blockingCount"] = plan.ManualTodo.Count(todo => todo.Blocking),
            ["blockingTodos"] = plan.ManualTodo
                .Where(todo => todo.Blocking)
                .Take(5)
                .Select(todo => todo.Id + " " + todo.Title)
                .ToList(),
            ["suggestedTodos"] = plan.ManualTodo
                .Where(todo => !todo.Blocking)
                .Take(3)
                .Select(todo => todo.Id + " " + todo.Title)
                .ToList(),
            ["dryRun"] = false,
        };

        await stdout.WriteLineAsync($"[完成] 报告已写出：{written.Value.MarkdownPath}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"  人工待办：{humanTodosPath}（{plan.ManualTodo.Count} 条，其中阻断 {data["blockingCount"]} 条）").ConfigureAwait(false);
        return await WriteSuccessAsync(commandText, "报告已写出。", data, stdout, stderr, options.Json).ConfigureAwait(false);
    }

    private static async Task<(AnalysisReport Analysis, GhidraStepResult? Ghidra)> AnalyzeWithOptionalGhidraAsync(
        Resolved resolved,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeAsync(resolved, options, cancellationToken).ConfigureAwait(false);
        GhidraStepResult? ghidra = null;

        var advancedEnabled = options.Advanced || resolved.AutoAdvanced;
        if (advancedEnabled)
        {
            ghidra = await GhidraAdvanced.RunAsync(
                resolved.GameDirectory,
                resolved.Workspace.RootPath,
                resolved.Workspace.StageDirectory(resolved.Slug, PipelineStage.Analyze),
                resolved.Detection.Family,
                analysis,
                resolved.Context.Profile?.Options,
                options.DryRun,
                cancellationToken).ConfigureAwait(false);
            analysis = ghidra.Analysis;
            WriteStage(resolved, PipelineStage.Analyze, "analysis.json", analysis, options.DryRun);
        }

        return (analysis, ghidra);
    }

    private static async Task<AnalysisReport> AnalyzeAsync(Resolved resolved, CliOptions options, CancellationToken cancellationToken)
    {
        var analyzer = resolved.Adapter.Analyzer;

        if (analyzer is null)
        {
            if (options.Command is CliCommand.Analyze or CliCommand.Report
                && (options.Advanced || resolved.AutoAdvanced))
            {
                // 无适配器、--advanced：先给一个诚实的空结果；合并 Ghidra 结果后再统一写 analysis.json。
                WriteStage(resolved, PipelineStage.Detect, "detect.json", resolved.Detection, options.DryRun);
                return BuildAdapterlessAnalysis(resolved);
            }

            throw new CliFailure(ForgeError.Create(
                ForgeErrorCodes.NotImplemented,
                $"引擎 {resolved.Detection.Family} 的分析器尚未接线。",
                "当前可用：Ren'Py；没有适配器时请加 --advanced 用 Ghidra 做原生兜底。"));
        }

        var result = await analyzer.AnalyzeAsync(resolved.Context, resolved.Detection, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
        {
            throw new CliFailure(result.Error ?? ForgeError.Create(
                ForgeErrorCodes.Internal,
                "分析失败但没有错误信息。",
                null));
        }

        var analysis = result.Value;
        WriteStage(resolved, PipelineStage.Detect, "detect.json", resolved.Detection, options.DryRun);
        WriteStage(resolved, PipelineStage.Analyze, "analysis.json", analysis, options.DryRun);
        return analysis;
    }

    private static AnalysisReport BuildAdapterlessAnalysis(Resolved resolved)
    {
        var report = new AnalysisReport
        {
            GamePath = resolved.GameDirectory,
            Detection = resolved.Detection,
        };
        report.Coverage.NotCovered.Add($"引擎 {resolved.Detection.Family} 没有内置分析适配器，未执行适配器分析。");
        report.Coverage.ManualTodo.Add("安装 Ghidra 后运行 analyze --advanced，走原生代码兜底（静态文本 API 候选）。");
        report.Unverified.Add("未执行适配器分析：该引擎没有内置分析器，本次结果只可能来自 Ghidra 高级分析。");
        return report;
    }

    private static async Task<PatchPlan> PlanAsync(
        Resolved resolved,
        CliOptions options,
        AnalysisReport analysis,
        CancellationToken cancellationToken)
    {
        var planner = resolved.Adapter.Planner ?? throw new CliFailure(ForgeError.Create(
            ForgeErrorCodes.NotImplemented,
            $"引擎 {resolved.Detection.Family} 的规划器尚未接线。",
            "当前可用：Ren'Py。"));

        var result = await planner.PlanAsync(resolved.Context, analysis, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Value is null)
        {
            throw new CliFailure(result.Error ?? ForgeError.Create(
                ForgeErrorCodes.Internal,
                "规划失败但没有错误信息。",
                null));
        }

        var plan = result.Value;
        WriteStage(resolved, PipelineStage.Plan, "plan.json", plan, options.DryRun);
        return plan;
    }

    private static T? TryReadStage<T>(Resolved resolved, PipelineStage stage, string fileName)
        where T : class
    {
        var path = Path.Combine(resolved.Workspace.StageDirectory(resolved.Slug, stage), fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return ForgeJson.Deserialize<T>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>无适配器引擎只导出分析报告时的诚实骨架：不支持自动生成，人工待办阻塞。</summary>
    private static PatchPlan BuildAdapterlessPlan(Resolved resolved, AnalysisReport analysis)
    {
        var plan = new PatchPlan
        {
            GameSlug = resolved.Slug,
            Route = PlanRoute.Unsupported,
            Confidence = 0,
            Target = new PlanTarget
            {
                GamePath = resolved.GameDirectory,
                Engine = resolved.Detection.Family,
                EngineVersion = resolved.Detection.Version,
                Bitness = resolved.Detection.Bitness,
            },
        };

        plan.ManualTodo.Add(new ManualTodoItem
        {
            Id = "M-A01",
            Title = "该引擎没有内置适配器，不能自动生成可安装补丁",
            Detail = "本报告只汇总分析产物（含 Ghidra 静态结果）与覆盖声明；真正可玩需在 x64dbg/Textractor 中定位文本函数后人工接钩子。",
            Blocking = true,
        });

        foreach (var todo in analysis.Coverage.ManualTodo)
        {
            plan.ManualTodo.Add(new ManualTodoItem
            {
                Id = "M-A" + (plan.ManualTodo.Count + 1).ToString("D2", CultureInfo.InvariantCulture),
                Title = todo,
                Blocking = false,
            });
        }

        return plan;
    }

    private static void WriteStage<T>(Resolved resolved, PipelineStage stage, string fileName, T value, bool dryRun)
    {
        if (dryRun)
        {
            return;
        }

        var directory = resolved.Workspace.StageDirectory(resolved.Slug, stage);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, ForgeJson.Serialize(value), new UTF8Encoding(false));
    }

    private static List<object> DescribeCandidates(AnalysisReport analysis)
        => analysis.Candidates
            .Select(candidate => (object)new
            {
                candidate.Id,
                kind = candidate.Kind.ToString().ToLowerInvariant(),
                candidate.Source,
                candidate.Score,
                level = candidate.Score >= 70 ? "high" : candidate.Score >= 50 ? "medium" : "low",
                candidate.Evidence,
            })
            .ToList();

    private static object DescribeCoverage(CoverageReport coverage)
        => new
        {
            covered = coverage.Covered,
            notCovered = coverage.NotCovered,
            manualTodo = coverage.ManualTodo,
        };

    private static List<string> BuildDetectionEvidence(DetectionResult detection, string contractFamily)
    {
        var evidence = new List<string>(detection.Primary.Evidence)
        {
            $"research:family={detection.Family}",
            $"contract:family={contractFamily}",
        };

        if (detection.Candidates.Count > 0)
        {
            evidence.Add("candidates=" + string.Join(",", detection.Candidates.Take(3).Select(c => $"{c.Engine}:{c.Score}")));
        }

        return evidence;
    }

    private static string DescribeBitness(int bitness) => bitness switch
    {
        32 => "32 位",
        64 => "64 位",
        _ => "位数未识别",
    };

    private static string SlugFrom(string gameDirectory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(gameDirectory));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "game";
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            // 只保留工作区允许的 ASCII 字符；中文/日文目录名走下面的稳定哈希兜底。
            builder.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? ch : '-');
        }

        var slug = builder.ToString().Trim('-', '.', '_');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length == 0)
        {
            // 中文/日文等非 ASCII 目录名：用完整路径的 SHA-256 前 8 位做稳定后缀，
            // 保证同一款游戏每次落到同一个工作区，且不同游戏不会撞车。
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gameDirectory))));
            slug = "game-" + hash[..8].ToLowerInvariant();
        }

        if (slug.Length > 64)
        {
            slug = slug[..64].TrimEnd('-', '.', '_');
        }

        return slug.Length == 0 ? "game" : slug;
    }

    private static async Task<int> WriteSuccessAsync(
        string command,
        string message,
        Dictionary<string, object?> data,
        TextWriter stdout,
        TextWriter stderr,
        bool json)
    {
        if (json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(
                new CliResponse
                {
                    Ok = true,
                    Command = command,
                    Message = message,
                    Data = data,
                },
                ForgeJson.Compact)).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        return ExitCodes.Success;
    }

    private static async Task<int> WriteFailureAsync(
        ForgeError error,
        string command,
        TextWriter stdout,
        TextWriter stderr,
        bool json,
        Dictionary<string, object?>? data = null)
    {
        if (json)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(
                new CliResponse
                {
                    Ok = false,
                    Command = command,
                    Code = error.Code,
                    Message = error.Message,
                    Suggestion = error.Suggestion,
                    Details = error.Details,
                    Data = data,
                },
                ForgeJson.Compact)).ConfigureAwait(false);
            return ExitCodes.FromError(error);
        }

        await stderr.WriteLineAsync($"错误 [{error.Code}]：{error.Message}").ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error.Suggestion))
        {
            await stderr.WriteLineAsync($"建议：{error.Suggestion}").ConfigureAwait(false);
        }

        if (error.Details is { Count: > 0 } details)
        {
            foreach (var pair in details)
            {
                await stderr.WriteLineAsync($"详情 {pair.Key}：{pair.Value}").ConfigureAwait(false);
            }
        }

        return ExitCodes.FromError(error);
    }

    private static string CommandText(CliCommand command) => command.ToString().ToLowerInvariant();

    /// <summary>
    /// 选点诚实性闸门：如果方案里没有任何带字符串参数的补丁点，生成物只能记录调用次数，
    /// 不会朗读。NEEDY 真机 E2E（task-27）证明这类方案不能当成“自动成功”，必须提前告诉用户。
    /// 判断逻辑放在 Core 的 <see cref="PatchPlanTextInspector"/>，同时覆盖 Mono 的 Description
    /// 与 IL2CPP 的 Target（task-37 P2：此前只查 Description 导致 IL2CPP 正常方案误报）。
    /// 运行时取文本的引擎（Ren'Py，task-53）不适用该警告——静态签名缺失不等于不能读。
    /// </summary>
    private static async Task WarnIfNoTextHooksAsync(PatchPlan plan, TextWriter stderr)
    {
        if (!PatchPlanTextInspector.WarnsNoTextSource(plan))
        {
            return;
        }

        await stderr.WriteLineAsync(
            "[警告] 本次计划的补丁点既没有字符串参数，也没有可读的实例文本字段：生成物只能记录调用次数，不会朗读。"
            + " 请按报告的人工待办人工指定文本来源，或换一款文本写法可静态识别的游戏再试。").ConfigureAwait(false);
    }

    /// <summary>
    /// “计划里没有正文入口”的提醒（0.2.49）。0.2.48 复盘：Utage 游戏的计划只挑到系统对话框，
    /// CLI 仍报“安装完成”，用户装完发现一句台词都不读。这里在 plan/generate/install 三个节点都提醒。
    /// </summary>
    private static async Task WarnIfNoDialogueEntryAsync(PatchPlan plan, TextWriter stderr)
    {
        if (!PlanQualityInspector.HasNoDialogueEntryRisk(plan))
        {
            return;
        }

        await stderr.WriteLineAsync("[注意] " + PlanQualityInspector.DescribeNoDialogueEntry()).ConfigureAwait(false);
        await stderr.WriteLineAsync(
            "       先看 plan 里 R-106 的说明与人工待办；装完后以 BepInEx\\LogOutput.log 里的 [Speech] 行为准。")
            .ConfigureAwait(false);
    }
}
