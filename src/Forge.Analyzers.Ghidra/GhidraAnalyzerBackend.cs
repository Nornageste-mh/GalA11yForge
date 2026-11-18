using System.Text.Json;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Analyzers.Ghidra;

public enum GhidraAnalysisStatus
{
    /// <summary>拿到并解析了脚本产物。</summary>
    Completed = 0,

    /// <summary>没装 Ghidra / 路径不可用。</summary>
    NotAvailable = 1,

    /// <summary>配置显式禁用。</summary>
    Disabled = 2,

    /// <summary>游戏目录里找不到可分析的可执行文件。</summary>
    NoTarget = 3,

    /// <summary>headless 超时，进程树已终止。</summary>
    Timeout = 4,

    /// <summary>调用方取消。</summary>
    Canceled = 5,

    /// <summary>进程没能启动。</summary>
    StartFailed = 6,

    /// <summary>进程结束但退出码非 0，且没有可用产物。</summary>
    NonZeroExit = 7,

    /// <summary>退出码 0 但没有产物、产物越界或 JSON 不可解析。</summary>
    InvalidOutput = 8,
}

/// <summary>
/// 后端的完整结果。比 BackendAnalysisResult 多带可用性/状态/耗时/产物路径等，
/// 因为 CLI 的 JSON 信封与人类可读输出需要区分「没装」「超时」「跑完但 0 候选」。
/// </summary>
public sealed class GhidraAnalysisOutcome
{
    public string BackendId { get; init; } = GhidraAnalyzerBackend.BackendIdValue;
    public GhidraAnalysisStatus Status { get; init; }
    public bool Available { get; init; }
    public bool Succeeded => Status == GhidraAnalysisStatus.Completed;

    public GhidraLocation? Location { get; init; }
    public string? ToolVersion { get; init; }
    public string? ExecutablePath { get; init; }
    public List<string> ExecutableCandidates { get; init; } = new();
    public string? ExecutableSha256 { get; init; }
    public long ExecutableSizeBytes { get; init; }

    /// <summary>按可执行文件体积粗估的耗时（秒）；没有目标大小时为 null。只用于提示。</summary>
    public int? EstimatedSeconds { get; init; }

    /// <summary>超时后建议的 timeoutSeconds（秒）；没有目标大小时为 null。只用于给出可复制建议。</summary>
    public int? SuggestedTimeoutSeconds { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? WorkDirectory { get; init; }
    public string? CandidatesJsonPath { get; init; }
    public string? StandardOutputPath { get; init; }
    public string? StandardErrorPath { get; init; }
    public string? CommandLine { get; init; }

    public GhidraCandidatesDocument? Document { get; init; }
    public string? RawScriptJson { get; init; }

    public List<TextCandidate> Candidates { get; init; } = new();
    public CoverageReport Coverage { get; init; } = new();
    public List<string> Unverified { get; init; } = new();
    public List<RiskItem> Risks { get; init; } = new();
    public List<string> Evidence { get; init; } = new();
    public List<string> Notes { get; init; } = new();

    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Suggestion { get; init; }

    /// <summary>CLI 人类可读输出用的一句话结论。</summary>
    public string Summary { get; init; } = "";

    /// <summary>是否属于「硬失败」（CLI 在无适配器兜底时应返回 E_PARTIAL/非 0）。</summary>
    public bool IsHardFailure => Status is
        GhidraAnalysisStatus.Timeout or
        GhidraAnalysisStatus.Canceled or
        GhidraAnalysisStatus.StartFailed or
        GhidraAnalysisStatus.NonZeroExit or
        GhidraAnalysisStatus.InvalidOutput or
        GhidraAnalysisStatus.NoTarget;
}

/// <summary>
/// Ghidra 高级分析后端：定位 → 释放脚本 → headless 分析 → 解析 candidates.json → 结构化候选。
/// 任何一步失败都保留可操作的 unverified/manualTodo，不把「没跑」写成「没有文本」。
/// </summary>
public sealed class GhidraAnalyzerBackend : IAnalyzerBackend
{
    public const string BackendIdValue = "ghidra";
    private const int MaxSampleStrings = 3;
    private const int MaxSampleTextChars = 50;

    private readonly GhidraBackendOptions _options;
    private readonly GhidraLocation? _injectedLocation;
    private readonly Func<GhidraRunRequest, CancellationToken, Task<GhidraRunResult>>? _runnerOverride;

    public GhidraAnalyzerBackend(GhidraBackendOptions options)
        : this(options, location: null, runnerOverride: null)
    {
    }

    internal GhidraAnalyzerBackend(
        GhidraBackendOptions options,
        GhidraLocation? location,
        Func<GhidraRunRequest, CancellationToken, Task<GhidraRunResult>>? runnerOverride)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _injectedLocation = location;
        _runnerOverride = runnerOverride;
    }

    public string BackendId => BackendIdValue;

    public async Task<ForgeResult<BackendAnalysisResult>> AnalyzeAsync(
        AnalyzerBackendRequest request,
        CancellationToken cancellationToken = default)
    {
        var outcome = await AnalyzeDetailedAsync(request, cancellationToken).ConfigureAwait(false);

        if (outcome.Status is GhidraAnalysisStatus.Completed or
            GhidraAnalysisStatus.NotAvailable or
            GhidraAnalysisStatus.Disabled)
        {
            return ForgeResult<BackendAnalysisResult>.Success(ToBackendResult(outcome));
        }

        return ForgeResult<BackendAnalysisResult>.Failure(ForgeError.Create(
            outcome.ErrorCode ?? ForgeErrorCodes.Partial,
            outcome.ErrorMessage ?? outcome.Summary,
            outcome.Suggestion));
    }

    public async Task<GhidraAnalysisOutcome> AnalyzeDetailedAsync(
        AnalyzerBackendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GameDirectory);

        if (!_options.Enabled)
        {
            return BuildUnavailableOutcome(
                GhidraAnalysisStatus.Disabled,
                summary: "已禁用 Ghidra 高级分析（ghidra.enabled=false）。",
                errorCode: null,
                errorMessage: null,
                suggestion: "删除 profile 里的 ghidra.enabled=false 或改为 true 后重跑 --advanced。",
                executablePath: request.ExecutablePath,
                elapsed: TimeSpan.Zero);
        }

        var location = _injectedLocation ?? GhidraLocator.Locate(new GhidraLocationRequest
        {
            ExplicitPath = _options.GhidraPath,
        });

        if (!location.Available)
        {
            return BuildUnavailableOutcome(
                GhidraAnalysisStatus.NotAvailable,
                summary: "未启用高级分析（未找到 Ghidra）。",
                errorCode: ForgeErrorCodes.DependencyMissing,
                errorMessage: location.Error ?? "未找到可用的 Ghidra。",
                suggestion: "安装 Ghidra 12.x（Apache-2.0，需 JDK 21），或用 --option ghidra.path=<Ghidra 安装目录> / 环境变量 GHIDRA_HOME 指定。",
                executablePath: request.ExecutablePath,
                elapsed: TimeSpan.Zero,
                location: location);
        }

        var executable = ResolveExecutable(request);
        if (executable is null)
        {
            return BuildUnavailableOutcome(
                GhidraAnalysisStatus.NoTarget,
                summary: "未找到可分析的可执行文件（游戏目录下没有 .exe）。",
                errorCode: ForgeErrorCodes.ArtifactMissing,
                errorMessage: $"游戏目录 {request.GameDirectory} 下没有找到可分析的可执行文件。",
                suggestion: "确认游戏主程序在安装目录根层；Ghidra 只分析 PE/ELF 可执行文件，不分析脚本或数据包。",
                executablePath: request.ExecutablePath,
                elapsed: TimeSpan.Zero,
                location: location);
        }

        // 多子游戏合集（如 To the Moon）根层往往没有 exe、子目录里一堆候选：
        // 自动选中一个，同时把其它候选写进 unverified，让用户用 ghidra.executablePath 换。
        var executableCandidates = string.IsNullOrWhiteSpace(request.ExecutablePath)
            ? GhidraTargetSelector.FindCandidates(request.GameDirectory).ToList()
            : new List<string>();

        var workId = $"ghidra-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var workDirectory = Path.Combine(_options.WorkspaceRoot, "work", "ghidra", workId);
        var projectDirectory = Path.Combine(workDirectory, "project");
        var scriptDirectory = Path.Combine(workDirectory, "scripts");
        var outputDirectory = Path.Combine(workDirectory, "out");

        Directory.CreateDirectory(workDirectory);
        var scriptPath = GhidraScriptResources.ExtractCandidateHookDump(scriptDirectory);

        var timeoutSeconds = _options.TimeoutSeconds;
        var runRequest = new GhidraRunRequest
        {
            AnalyzeHeadlessPath = location.AnalyzeHeadlessPath!,
            ProjectDirectory = projectDirectory,
            ProjectName = "gala11y",
            ImportPath = executable,
            ScriptDirectory = Path.GetDirectoryName(scriptPath)!,
            OutputDirectory = outputDirectory,
            PostScript = GhidraScriptResources.CandidateHookDumpFileName,
            TimeoutSeconds = timeoutSeconds,
            AnalysisTimeoutSeconds = _options.AnalysisTimeoutSeconds,
            MaxCpu = _options.MaxCpu,
            MaxMemory = _options.MaxMemory,
            DeleteProject = !_options.KeepProject,
            // 日志放在工程目录外：-deleteProject 会删掉工程目录，失败清理时也只删工程、保留日志。
            StandardOutputPath = Path.Combine(workDirectory, "stdout.log"),
            StandardErrorPath = Path.Combine(workDirectory, "stderr.log"),
        };

        GhidraRunResult run;
        try
        {
            run = _runnerOverride is null
                ? await new GhidraRunner(_options.WorkspaceRoot, request.GameDirectory)
                    .RunAsync(runRequest, cancellationToken).ConfigureAwait(false)
                : await _runnerOverride(runRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return BuildFailedOutcome(
                GhidraAnalysisStatus.Canceled,
                "Ghidra 分析已取消。",
                ForgeErrorCodes.Partial,
                suggestion: "重新运行 analyze --advanced。",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return BuildFailedOutcome(
                GhidraAnalysisStatus.StartFailed,
                $"Ghidra 调用准备失败：{ex.Message}",
                ForgeErrorCodes.Io,
                suggestion: "检查工作区可写、--workspace 不在游戏目录内，以及 ghidra.path 指向真实安装目录。",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: TimeSpan.Zero);
        }

        var candidatesJson = Path.Combine(outputDirectory, "candidates.json");
        var scriptWrote = GhidraResultParser.TryParseWroteLine(
            run.StandardOutput,
            out var wrotePath,
            out var wroteCandidates,
            out var wroteCjk,
            out var wrotePacked);

        if (scriptWrote && wrotePath is not null && !PathSafety.IsUnder(workDirectory, wrotePath))
        {
            return BuildFailedOutcome(
                GhidraAnalysisStatus.InvalidOutput,
                $"Ghidra 脚本报告的产物路径越界：{wrotePath}",
                ForgeErrorCodes.PathUnsafe,
                suggestion: "脚本只允许写工作区内的 out 目录；检查脚本是否被替换。",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: run.Elapsed,
                run: run);
        }

        GhidraCandidatesDocument document;
        string rawJson;
        if (File.Exists(candidatesJson))
        {
            try
            {
                rawJson = await File.ReadAllTextAsync(candidatesJson, cancellationToken).ConfigureAwait(false);
                document = GhidraResultParser.Parse(rawJson);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return BuildFailedOutcome(
                    GhidraAnalysisStatus.InvalidOutput,
                    $"Ghidra 产物 candidates.json 不可用：{ex.Message}",
                    ForgeErrorCodes.ArtifactInvalid,
                    suggestion: "查看 work 目录下的 stdout.log/stderr.log；脚本可能因版本不兼容中断。",
                    location: location,
                    executable: executable,
                    workDirectory: workDirectory,
                    elapsed: run.Elapsed,
                    run: run);
            }
        }
        else if (run.Status == GhidraRunStatus.Succeeded)
        {
            return BuildFailedOutcome(
                GhidraAnalysisStatus.InvalidOutput,
                "Ghidra 正常退出但没有产出 candidates.json。",
                ForgeErrorCodes.ArtifactMissing,
                suggestion: "确认脚本已释放到 work 目录、脚本输出目录可写；stderr 里通常有脚本异常。",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: run.Elapsed,
                run: run);
        }
        else
        {
            return WithCandidates(BuildRunFailure(run, location, executable, workDirectory, timeoutSeconds), executableCandidates, executable);
        }

        // 产物存在但退出码非 0：仍然用产物，但必须把「可能不完整」写出来。
        if (run.Status != GhidraRunStatus.Succeeded)
        {
            CleanupProjectDirectory(projectDirectory);
        }

        var extraUnverified = new List<string>();
        if (run.ExitCode != 0)
        {
            extraUnverified.Add($"Ghidra 退出码为 {run.ExitCode}，但脚本产物存在；结果可能不完整。");
        }

        if (scriptWrote && wroteCandidates != document.Candidates.Count)
        {
            extraUnverified.Add(
                $"脚本自报候选数 {wroteCandidates} 与产物中的 {document.Candidates.Count} 不一致；以产物为准。");
        }

        _ = wroteCjk;
        _ = wrotePacked;

        var outcome = BuildCompletedOutcome(
            document,
            rawJson,
            run,
            location,
            executable,
            workDirectory,
            candidatesJson,
            extraUnverified);
        return WithCandidates(outcome, executableCandidates, executable);
    }

    private GhidraAnalysisOutcome BuildCompletedOutcome(
        GhidraCandidatesDocument document,
        string rawJson,
        GhidraRunResult run,
        GhidraLocation location,
        string executable,
        string workDirectory,
        string candidatesJson,
        List<string> extraUnverified)
    {
        var candidates = document.Candidates
            .Select((candidate, index) => ToTextCandidate(candidate, index))
            .ToList();

        var coverage = new CoverageReport();
        coverage.Covered.Add(
            $"Ghidra 静态分析：{document.Stats.Functions} 个函数、{document.Candidates.Count} 个文本 API 候选");
        coverage.Covered.Add(
            $"导入表文本 API 命中 {document.TextApiSurface.Present.Count} 个：" +
            $"{string.Join("、", document.TextApiSurface.Present.Take(6))}");
        coverage.Covered.Add(
            $"中日文字符串引用 {document.Stats.CjkStrings} 条（其中严格 UTF-16 扫描 {document.Stats.Utf16ScanCjkStrings} 条）");

        coverage.NotCovered.Add("未执行代码：只做静态反汇编，不覆盖运行时分支与加密后段。");
        coverage.NotCovered.Add("运行时动态解析的 API（GetProcAddress/LoadLibrary）不在静态导入表里。");
        coverage.NotCovered.Add("非 GDI/DirectWrite 的自绘文本（SDL_ttf、FreeType、引擎自带字形缓存）无法命中。");

        var unverified = new List<string>
        {
            "Ghidra 候选是静态推断，未在游戏中运行验证（未确认文本确实流经这些函数）。",
        };

        if (document.Binary.PackedSuspected)
        {
            coverage.NotCovered.Add("加壳/自保护后的真实代码段（静态熵启发式判定）。");
            unverified.Add("目标疑似加壳（packed_suspected=true）：静态候选通常为 0，不能据此判断「没有文本」。");
        }

        if (document.Binary.DynamicApiResolutionSuspected)
        {
            unverified.Add("检测到 GetProcAddress/LoadLibrary：完整文本路径可能在运行时解析，静态结果只覆盖已导入部分。");
        }

        if (document.Binary.IsDotnet)
        {
            unverified.Add("目标是 .NET 程序集：Ghidra 原生分析参考价值低，应优先使用托管层适配器（如 Cecil）。");
        }

        if (document.Candidates.Count == 0)
        {
            unverified.Add("Ghidra 未找到文本 API 候选；不等于没有文本（自绘/运行时解析/加壳/非 GDI 栈都会是 0）。");
        }

        unverified.AddRange(extraUnverified);

        var risks = new List<RiskItem>();
        if (document.Binary.PackedSuspected)
        {
            risks.Add(new RiskItem
            {
                Id = "ghidra-packed",
                Severity = RiskSeverity.High,
                Description = "Ghidra 判定疑似加壳/自保护（可执行段熵启发式），静态分析结论不可信。",
                Mitigation = "先用 DIE/PEiD 确认壳类型；只有 UPX 可安全自动脱壳，其余转人工或运行时方案。",
            });
        }

        if (document.Binary.DynamicApiResolutionSuspected)
        {
            risks.Add(new RiskItem
            {
                Id = "ghidra-dynamic-api",
                Severity = RiskSeverity.Medium,
                Description = "文本 API 可能被运行时解析，静态导入表覆盖不全。",
                Mitigation = "结合运行时断点（x64dbg/Textractor）确认实际调用的文本函数。",
            });
        }

        if (document.Candidates.Count == 0)
        {
            coverage.ManualTodo.Add("在 x64dbg/Textractor 中实际跑一遍文本，定位绘制函数后再用 Ghidra 建钩子点。");
        }
        else
        {
            var top = document.Candidates[0];
            coverage.ManualTodo.Add(
                $"用 x64dbg 核对候选 {top.Function} @ {top.Address} 的调用链与文本绘制时机。");
        }

        coverage.ManualTodo.Add("静态候选不构成成品补丁：需人工确认参数（文本指针/长度/编码）后才能生成钩子。");

        var evidence = new List<string>
        {
            $"Ghidra {document.Tool.Version}（脚本 {document.Tool.Script}，schema {document.Schema}）",
            $"目标：{document.Binary.Name}（{document.Binary.SizeBytes} 字节，SHA-256 {ShortHash(document.Binary.Sha256)}）",
            $"格式：{document.Binary.ExecutableFormat} / {document.Binary.Language} / {document.Binary.Bits} 位；入口 {document.Binary.EntryPoint}",
            $"耗时 {run.Elapsed.TotalSeconds:F1}s；命令行：{run.Command.Display}",
        };

        var notes = new List<string>();
        foreach (var hint in document.EngineHints.Take(3))
        {
            notes.Add($"Ghidra 提示 {hint.Id}：{hint.Text}");
        }

        var summary = document.Candidates.Count == 0
            ? $"Ghidra 分析完成：0 个候选（{document.Stats.Functions} 个函数），耗时 {run.Elapsed.TotalSeconds:F0}s。"
            : $"Ghidra 分析完成：{document.Candidates.Count} 个候选（最高 {document.Candidates[0].Confidence:F0}/100），耗时 {run.Elapsed.TotalSeconds:F0}s。";

        return new GhidraAnalysisOutcome
        {
            Status = GhidraAnalysisStatus.Completed,
            Available = true,
            Location = location,
            ToolVersion = document.Tool.Version,
            ExecutablePath = executable,
            ExecutableSha256 = document.Binary.Sha256,
            ExecutableSizeBytes = document.Binary.SizeBytes,
            Elapsed = run.Elapsed,
            WorkDirectory = workDirectory,
            CandidatesJsonPath = candidatesJson,
            StandardOutputPath = run.StandardOutputPath,
            StandardErrorPath = run.StandardErrorPath,
            CommandLine = run.Command.Display,
            Document = document,
            RawScriptJson = rawJson,
            Candidates = candidates,
            Coverage = coverage,
            Unverified = unverified,
            Risks = risks,
            Evidence = evidence,
            Notes = notes,
            Summary = summary,
        };
    }

    private GhidraAnalysisOutcome BuildRunFailure(
        GhidraRunResult run,
        GhidraLocation location,
        string executable,
        string workDirectory,
        int timeoutSeconds)
    {
        var executableSizeBytes = TryGetFileSize(executable);
        var suggestedTimeoutSeconds = executableSizeBytes > 0
            ? GhidraDurationEstimator.SuggestTimeoutSeconds(executableSizeBytes, timeoutSeconds)
            : (int?)null;
        return run.Status switch
        {
            GhidraRunStatus.TimedOut => BuildFailedOutcome(
                GhidraAnalysisStatus.Timeout,
                $"Ghidra 分析超时（{timeoutSeconds}s），已终止进程树。",
                ForgeErrorCodes.Partial,
                suggestion: $"提高 ghidra.timeoutSeconds（当前 {timeoutSeconds}" +
                            (executableSizeBytes > 0
                                ? $"；按文件大小粗估需要{GhidraDurationEstimator.Describe(executableSizeBytes)}，建议调到 {suggestedTimeoutSeconds}"
                                : "") +
                            $"）后重跑，或换更小的可执行文件；日志：{run.StandardErrorPath}",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: run.Elapsed,
                executableSizeBytes: executableSizeBytes,
                suggestedTimeoutSeconds: suggestedTimeoutSeconds,
                run: run),
            GhidraRunStatus.Canceled => BuildFailedOutcome(
                GhidraAnalysisStatus.Canceled,
                "Ghidra 分析已取消，进程树已终止。",
                ForgeErrorCodes.Partial,
                suggestion: "重新运行 analyze --advanced。",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: run.Elapsed,
                executableSizeBytes: executableSizeBytes,
                run: run),
            GhidraRunStatus.StartFailed => BuildFailedOutcome(
                GhidraAnalysisStatus.StartFailed,
                $"Ghidra 启动失败：{run.StartError}",
                ForgeErrorCodes.DependencyMissing,
                suggestion: "检查 ghidra.path 是否指向含 support\\analyzeHeadless.bat 的目录，以及 JDK 21 是否可用。",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: run.Elapsed,
                executableSizeBytes: executableSizeBytes,
                run: run),
            _ => BuildFailedOutcome(
                GhidraAnalysisStatus.NonZeroExit,
                $"Ghidra headless 退出码 {run.ExitCode}：{run.Tail()}",
                ForgeErrorCodes.Partial,
                suggestion: $"查看 {run.StandardErrorPath} 与 {run.StandardOutputPath}；常见原因是脚本不兼容或目标格式不受支持。",
                location: location,
                executable: executable,
                workDirectory: workDirectory,
                elapsed: run.Elapsed,
                run: run),
        };
    }

    private GhidraAnalysisOutcome BuildUnavailableOutcome(
        GhidraAnalysisStatus status,
        string summary,
        string? errorCode,
        string? errorMessage,
        string? suggestion,
        string? executablePath,
        TimeSpan elapsed,
        GhidraLocation? location = null)
    {
        var coverage = new CoverageReport();
        coverage.NotCovered.Add("原生代码静态分析（本次未运行）。");
        coverage.ManualTodo.Add("安装 Ghidra 12.x 后用 --option ghidra.path=<目录> 重跑 analyze --advanced。");
        coverage.ManualTodo.Add("没有 Ghidra 时，原生钩子只能靠人工在 x64dbg/Textractor 里定位。");

        var unverified = new List<string>
        {
            status == GhidraAnalysisStatus.Disabled
                ? "已按配置禁用 Ghidra 高级分析，本次没有任何原生代码证据。"
                : "未启用高级分析（未找到 Ghidra），本次没有任何原生代码证据。",
        };

        if (status == GhidraAnalysisStatus.NotAvailable)
        {
            unverified.Add("不能据本次结果判断「游戏没有文本钩子」——只代表没有跑原生分析。");
        }

        return new GhidraAnalysisOutcome
        {
            Status = status,
            Available = status == GhidraAnalysisStatus.NoTarget,
            Location = location,
            ExecutablePath = executablePath,
            Elapsed = elapsed,
            Coverage = coverage,
            Unverified = unverified,
            Evidence = location?.Probes.ToList() ?? new List<string>(),
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            Suggestion = suggestion,
            Summary = summary,
        };
    }

    private GhidraAnalysisOutcome BuildFailedOutcome(
        GhidraAnalysisStatus status,
        string summary,
        string errorCode,
        string? suggestion,
        GhidraLocation location,
        string executable,
        string workDirectory,
        TimeSpan elapsed,
        long executableSizeBytes = 0,
        int? suggestedTimeoutSeconds = null,
        GhidraRunResult? run = null)
    {
        // 失败时清理 Ghidra 工程库（可能残留锁文件与大体积数据）；日志已写到工程目录外。
        CleanupProjectDirectory(Path.Combine(workDirectory, "project"));

        var coverage = new CoverageReport();
        coverage.NotCovered.Add("Ghidra 分析未成功完成，没有可用的原生候选。");
        coverage.ManualTodo.Add("查看 Ghidra 日志（work 目录下 stdout.log/stderr.log）后重跑。");
        coverage.ManualTodo.Add("若反复失败，改用人工 x64dbg/Textractor 路线，并在报告中标注「高级分析不可用」。");

        var unverified = new List<string>
        {
            $"Ghidra 高级分析失败：{summary}",
            "本次没有原生代码证据，不能据此判断游戏没有文本钩子。",
        };

        return new GhidraAnalysisOutcome
        {
            Status = status,
            Available = true,
            Location = location,
            ToolVersion = location.Version,
            ExecutablePath = executable,
            ExecutableSizeBytes = executableSizeBytes,
            EstimatedSeconds = executableSizeBytes > 0
                ? GhidraDurationEstimator.EstimateRoundedSeconds(executableSizeBytes)
                : null,
            SuggestedTimeoutSeconds = suggestedTimeoutSeconds,
            Elapsed = elapsed,
            WorkDirectory = workDirectory,
            CandidatesJsonPath = run is null ? null : Path.Combine(workDirectory, "out", "candidates.json"),
            StandardOutputPath = run?.StandardOutputPath,
            StandardErrorPath = run?.StandardErrorPath,
            CommandLine = run?.Command.Display,
            Coverage = coverage,
            Unverified = unverified,
            Evidence = run is null
                ? new List<string>()
                : new List<string> { $"退出码 {run.ExitCode}；耗时 {run.Elapsed.TotalSeconds:F1}s；{run.Command.Display}" },
            ErrorCode = errorCode,
            ErrorMessage = summary,
            Suggestion = suggestion,
            Summary = summary,
        };
    }

    /// <summary>把“还有哪些候选可执行文件”附加到结果里；没有候选或只有一个时不打扰用户。</summary>
    private static GhidraAnalysisOutcome WithCandidates(
        GhidraAnalysisOutcome outcome,
        IReadOnlyList<string> candidates,
        string selectedExecutable)
    {
        if (candidates.Count == 0)
        {
            return outcome;
        }

        outcome.ExecutableCandidates.AddRange(candidates);
        if (candidates.Count > 1)
        {
            var names = string.Join("、", candidates.Take(4).Select(Path.GetFileName));
            var more = candidates.Count > 4 ? "…" : "";
            outcome.Unverified.Add(
                $"候选可执行文件共 {candidates.Count} 个：{names}{more}；当前自动选择的是 {Path.GetFileName(selectedExecutable)}；" +
                "要分析另一个，可用 --option ghidra.executablePath=<完整路径> 指定。");
        }

        return outcome;
    }

    private static long TryGetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    private static void CleanupProjectDirectory(string projectDirectory)
    {
        try
        {
            if (Directory.Exists(projectDirectory))
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响结论：日志已经写到工程目录外，产物目录也不删。
        }
    }

    private static BackendAnalysisResult ToBackendResult(GhidraAnalysisOutcome outcome) => new()
    {
        BackendId = BackendIdValue,
        ToolVersion = outcome.ToolVersion,
        Candidates = outcome.Candidates,
        Evidence = outcome.Evidence,
        Risks = outcome.Risks,
        Coverage = outcome.Coverage,
        Unverified = outcome.Unverified,
        Notes = outcome.Notes,
    };

    public static TextCandidate ToTextCandidate(GhidraCandidate candidate, int index)
    {
        var evidence = new List<string>
        {
            $"符号：{candidate.Function} @ {candidate.Address}（{candidate.Level}，{candidate.Confidence:F0}/100）",
            $"引用：调用者 {candidate.Evidence.CallerCount} 个；CJK 字符串引用 {candidate.Evidence.CjkStringRefs} 条",
        };

        if (candidate.Apis.Count > 0)
        {
            evidence.Add($"文本 API：{string.Join("、", candidate.Apis.Take(4))}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.Hook.Strategy))
        {
            evidence.Add($"挂钩建议：{Truncate(candidate.Hook.Strategy, 80)}");
        }

        foreach (var sample in candidate.Evidence.Strings.Take(MaxSampleStrings))
        {
            evidence.Add($"样例：{Truncate(sample.Text, MaxSampleTextChars)}");
        }

        return new TextCandidate
        {
            Id = $"ghidra-{index + 1:D2}-{SanitizeAddress(candidate.Address)}",
            // 原生文本 API 调用点：与托管 UI 文本方法区分，JSON 中写 native_api。
            Kind = TextCandidateKind.NativeApi,
            Source = $"native-api:ghidra!{candidate.Address} {candidate.Function}",
            Score = Math.Round(candidate.Confidence, 1, MidpointRounding.AwayFromZero),
            Evidence = evidence,
        };
    }

    private static string SanitizeAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "unknown";
        }

        var builder = new System.Text.StringBuilder(address.Length);
        foreach (var ch in address)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return builder.ToString().Trim('_');
    }

    internal static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text ?? "";
        }

        return text[..maxChars] + "…";
    }

    private static string ShortHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return "(未知)";
        }

        return hash.Length <= 16 ? hash : hash[..16] + "…";
    }

    private string? ResolveExecutable(AnalyzerBackendRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            var explicitPath = Path.GetFullPath(request.ExecutablePath);
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        return GhidraTargetSelector.FindPrimaryExecutable(request.GameDirectory);
    }
}

/// <summary>在游戏目录根层挑一个最像主程序的可执行文件（最大、排除安装器/卸载器/崩溃报告器）；
/// 根层没有 .exe 时，再在两层子目录里找（常见于 bin\、game\、引擎子目录布局）。</summary>
public static class GhidraTargetSelector
{
    /// <summary>根层找不到时，向下搜索的最大子目录层数（1 = 只看一级子目录）。</summary>
    internal const int MaxSearchDepth = 2;

    private static readonly string[] ExcludedNameFragments =
    {
        "unins", "setup", "install", "vcredist", "dxsetup", "directx",
        "crash", "report", "helper", "update", "launcher", "config",
    };

    private static readonly string[] ExcludedDirectoryFragments =
    {
        "_commonredist", "redist", "dotnet", "directx", "crash", "install", "tools",
    };

    public static string? FindPrimaryExecutable(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        if (!Directory.Exists(gameDirectory))
        {
            return null;
        }

        var rootCandidate = Directory.EnumerateFiles(gameDirectory, "*.exe", SearchOption.TopDirectoryOnly)
            .Where(path => !IsExcluded(Path.GetFileName(path)))
            .Select(path => new FileInfo(path))
            .Where(info => info.Length > 0)
            .OrderByDescending(info => info.Length)
            .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => info.FullName)
            .FirstOrDefault();
        if (rootCandidate is not null)
        {
            return rootCandidate;
        }

        return FindInSubdirectories(gameDirectory);
    }

    /// <summary>
    /// 列出所有候选可执行文件（根层 + 两层子目录），按与主程序相同的优先级排序。
    /// 用于多子游戏合集：报告里列出候选，让用户用 ghidra.executablePath 指定。
    /// </summary>
    public static IReadOnlyList<string> FindCandidates(string gameDirectory, int max = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        if (max <= 0 || !Directory.Exists(gameDirectory))
        {
            return Array.Empty<string>();
        }

        var candidates = new List<FileInfo>();
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(gameDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !IsExcluded(Path.GetFileName(path)))
                .Select(path => new FileInfo(path))
                .Where(info => info.Length > 0));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((gameDirectory, 0));
        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            if (depth >= MaxSearchDepth)
            {
                continue;
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                if (IsExcludedDirectory(Path.GetFileName(subdirectory)))
                {
                    continue;
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(subdirectory, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        if (IsExcluded(Path.GetFileName(file)))
                        {
                            continue;
                        }

                        var info = new FileInfo(file);
                        if (info.Length > 0)
                        {
                            candidates.Add(info);
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                queue.Enqueue((subdirectory, depth + 1));
            }
        }

        return candidates
            .GroupBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(info => new { Info = info, Matches = MatchesAncestorName(info, gameDirectory) })
            .OrderByDescending(item => item.Matches)
            .ThenBy(item => DepthOf(item.Info.FullName, gameDirectory))
            .ThenByDescending(item => item.Info.Length)
            .ThenBy(item => item.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(item => item.Info.FullName)
            .ToList();
    }

    private static string? FindInSubdirectories(string gameDirectory)
    {
        var candidates = new List<FileInfo>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((gameDirectory, 0));
        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();
            if (depth >= MaxSearchDepth)
            {
                continue;
            }

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                if (IsExcludedDirectory(Path.GetFileName(subdirectory)))
                {
                    continue;
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(subdirectory, "*.exe", SearchOption.TopDirectoryOnly))
                    {
                        if (IsExcluded(Path.GetFileName(file)))
                        {
                            continue;
                        }

                        var info = new FileInfo(file);
                        if (info.Length > 0)
                        {
                            candidates.Add(info);
                        }
                    }
                }
                catch (IOException)
                {
                    // 单个子目录读不了就跳过，不影响其它候选。
                }
                catch (UnauthorizedAccessException)
                {
                }

                queue.Enqueue((subdirectory, depth + 1));
            }
        }

        return candidates
            .Select(info => new { Info = info, Matches = MatchesAncestorName(info, gameDirectory) })
            .OrderByDescending(item => item.Matches)
            .ThenBy(item => DepthOf(item.Info.FullName, gameDirectory))
            .ThenByDescending(item => item.Info.Length)
            .ThenBy(item => item.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Info.FullName)
            .FirstOrDefault();
    }

    private static int DepthOf(string filePath, string gameDirectory)
    {
        var relative = Path.GetRelativePath(gameDirectory, filePath);
        return relative.Count(ch => ch == Path.DirectorySeparatorChar);
    }

    private static bool MatchesAncestorName(FileInfo executable, string gameDirectory)
    {
        var executableName = Normalize(Path.GetFileNameWithoutExtension(executable.Name));
        if (executableName.Length < 3)
        {
            return false;
        }

        var current = executable.Directory;
        var rootFull = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null)
        {
            if (string.Equals(
                    Normalize(current.Name),
                    executableName,
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    rootFull,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent;
        }

        return false;
    }

    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static bool IsExcluded(string fileName)
    {
        foreach (var fragment in ExcludedNameFragments)
        {
            if (fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExcludedDirectory(string directoryName)
    {
        foreach (var fragment in ExcludedDirectoryFragments)
        {
            if (directoryName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
