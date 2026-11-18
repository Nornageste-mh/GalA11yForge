namespace GalA11yForge.Core.Models;

/// <summary>03-generate 的产物描述：一个可编辑的补丁工程目录。</summary>
public sealed class GenerationResult
{
    public string ProjectDirectory { get; set; } = string.Empty;

    public List<GeneratedArtifact> Artifacts { get; set; } = new();

    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>04-build 的产物：二进制/压缩包、日志位置。</summary>
public sealed class BuildResult
{
    public string OutputDirectory { get; set; } = string.Empty;

    public List<string> Artifacts { get; set; } = new();

    public string? LogPath { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>05-deploy 的结果账本。</summary>
public sealed class DeployResult
{
    public DeployManifest Manifest { get; set; } = new();

    public string BackupDirectory { get; set; } = string.Empty;
}

/// <summary>回滚结果。</summary>
public sealed class DeployRollbackResult
{
    public List<string> RestoredFiles { get; set; } = new();

    public List<string> DeletedFiles { get; set; } = new();

    public bool AlreadyClean { get; set; }
}

/// <summary>单条验证检查。</summary>
public sealed class VerificationCheck
{
    public string Name { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public string? Detail { get; set; }
}

/// <summary>06-verify 的初检结果：哪些通过、哪些未验证、还需人工做什么。</summary>
public sealed class VerificationReport
{
    public bool Passed { get; set; }

    public List<VerificationCheck> Checks { get; set; } = new();

    public List<string> Unverified { get; set; } = new();

    public List<ManualTodoItem> ManualTodo { get; set; } = new();
}

/// <summary>高级分析后端（Ghidra 等）的请求。</summary>
public sealed class AnalyzerBackendRequest
{
    public string GameDirectory { get; set; } = string.Empty;

    public string? ExecutablePath { get; set; }

    public string? ExportDirectory { get; set; }

    public int TimeoutSeconds { get; set; } = 600;

    public bool Advanced { get; set; } = true;
}

/// <summary>高级分析后端的输出；Coverage/Unverified 必须填写，报告侧原样透传。</summary>
public sealed class BackendAnalysisResult
{
    public string BackendId { get; set; } = string.Empty;

    public string? ToolVersion { get; set; }

    public List<TextCandidate> Candidates { get; set; } = new();

    public List<string> Evidence { get; set; } = new();

    public List<RiskItem> Risks { get; set; } = new();

    public CoverageReport Coverage { get; set; } = new();

    public List<string> Unverified { get; set; } = new();

    public List<string> Notes { get; set; } = new();
}

/// <summary>万能层（通用运行时捕获）的请求；v1 默认实验性、默认关闭。</summary>
public sealed class UniversalCaptureRequest
{
    public string GameDirectory { get; set; } = string.Empty;

    public int DurationSeconds { get; set; } = 30;

    public bool IncludeClipboard { get; set; } = true;

    public bool IncludeWindowText { get; set; } = true;

    public bool Experimental { get; set; } = true;
}

/// <summary>万能层抓到的文本与证据；它不是“已支持引擎”，报告里必须标注实验性。</summary>
public sealed class UniversalCaptureResult
{
    public List<string> Texts { get; set; } = new();

    public List<string> Evidence { get; set; } = new();

    public CoverageReport Coverage { get; set; } = new();

    public List<string> Unverified { get; set; } = new();
}
