using System.Text.Json.Serialization;

namespace UnityMonoAnalyzer;

/// <summary>工具身份与契约版本（JSON schema 版本变化时才改 SchemaVersion）。</summary>
public static class ToolInfo
{
    public const string Name = "unity-mono-analyzer";
    public const string Version = "0.1.0.0";
    public const string SchemaVersion = "1.0";
    public const string CecilVersion = "0.11.6";
}

/// <summary>一次分析的完整结果 —— 这就是写入 analysis.json 的固定 schema（schemaVersion=1.0）。</summary>
public sealed class AnalysisResult
{
    public string SchemaVersion { get; set; } = ToolInfo.SchemaVersion;
    public RunInfo Run { get; set; } = new();
    public GameInfo Game { get; set; } = new();
    public DiscoveryInfo Discovery { get; set; } = new();
    public UnityInfo Unity { get; set; } = new();
    public AssemblyInfo Assembly { get; set; } = new();
    public ScanStats Stats { get; set; } = new();
    public List<TextSinkApi> TextSinks { get; set; } = new();
    public List<TextSinkHotspot> TextSinkHotspots { get; set; } = new();
    public List<FrameworkTrace> FrameworkTraces { get; set; } = new();
    public List<TextCandidate> Candidates { get; set; } = new();
    public CoverageInfo Coverage { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public TimingInfo Timing { get; set; } = new();
    public int ExitCode { get; set; }
}

public sealed class RunInfo
{
    public string Tool { get; set; } = ToolInfo.Name;
    public string ToolVersion { get; set; } = ToolInfo.Version;
    public string CecilVersion { get; set; } = ToolInfo.CecilVersion;
    public string StartedAtUtc { get; set; } = "";
    public string CommandLine { get; set; } = "";
}

public sealed class GameInfo
{
    /// <summary>游戏名（用户传入目录的名字，比数据目录名更接近玩家认知）。</summary>
    public string Name { get; set; } = "";
    /// <summary>--game 传入的原样路径。</summary>
    public string InputPath { get; set; } = "";
    /// <summary>真正被分析的游戏根目录（= dataDir 的父目录）。</summary>
    public string GameDir { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string DataDirName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ExeReason { get; set; } = "";
    /// <summary>是否已部署过补丁（BepInEx/winhttp.dll/doorstop）。不影响静态分析，只作为环境事实记录。</summary>
    public bool Deployed { get; set; }
    public List<string> DeployedEvidence { get; set; } = new();
}

public sealed class DiscoveryInfo
{
    public string InputKind { get; set; } = "directory"; // directory|exe|assembly
    public string SelectionMode { get; set; } = "";       // explicit-game|explicit-exe|single|ranked|forced
    public List<DiscoveryCandidate> Candidates { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public sealed class DiscoveryCandidate
{
    public string DataDir { get; set; } = "";
    public string DataDirName { get; set; } = "";
    public string GameDir { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public long AssemblySize { get; set; }
    public string ExePath { get; set; } = "";
    public string ExeReason { get; set; } = "";
    public int Depth { get; set; }
    public bool Selected { get; set; }
    public double RankScore { get; set; }
    public string RankReason { get; set; } = "";
    public QuickMetrics QuickMetrics { get; set; } = new();
    public string ScanError { get; set; } = "";
}

/// <summary>候选排序用的轻量指标（不建调用图、不打分）。</summary>
public sealed class QuickMetrics
{
    public int Types { get; set; }
    public int Methods { get; set; }
    public int TextSinkCalls { get; set; }
    public int TextSinkCallerMethods { get; set; }
    public int CjkLiteralMethods { get; set; }
    public int KnownFrameworkTypes { get; set; }
    public int DialogueNamedTypes { get; set; }
    public int LauncherSuspicionTypes { get; set; }
    public bool Scanned { get; set; }
}

public sealed class UnityInfo
{
    public string Version { get; set; } = "";
    /// <summary>globalgamemanagers | UnityPlayer.dll | IL2CPP metadata | unknown</summary>
    public string VersionSource { get; set; } = "unknown";
    public bool UnityPlayerDllPresent { get; set; }
    public bool Il2CppMarkersPresent { get; set; }
    public string Il2CppEvidence { get; set; } = "";
    public string GlobalGameManagersPath { get; set; } = "";
}

public sealed class AssemblyInfo
{
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public string FirstPassAssemblyPath { get; set; } = "";
}

public sealed class ScanStats
{
    public int Types { get; set; }
    public int Methods { get; set; }
    public int MethodsWithBody { get; set; }
    public int LdstrTotal { get; set; }
    public int CjkLiteralMethods { get; set; }
    /// <summary>整包是否存在 CJK 字面量（决定 S6 维度是否适用）。</summary>
    public bool CjkGame { get; set; }
    public int TextFieldTypes { get; set; }
    public int TextSinkCallSites { get; set; }
    public int TextSinkCallerMethods { get; set; }
    public int UpstreamCandidateMethods { get; set; }
    public int CandidatesScored { get; set; }
    public int CandidatesIncluded { get; set; }
    public int TierA { get; set; }
    public int TierB { get; set; }
    public int TierC { get; set; }
    public int BelowThreshold { get; set; }
    public List<string> ResolveFailures { get; set; } = new();
}

public sealed class TextSinkApi
{
    /// <summary>形如 TMPro.TMP_Text::set_text 的写入 API。</summary>
    public string Api { get; set; } = "";
    public int Calls { get; set; }
    public int CallerMethods { get; set; }
    /// <summary>primary = 计入 S3；secondary/ignored = 只统计不参与打分。</summary>
    public string Role { get; set; } = "primary";
}

public sealed class TextSinkHotspot
{
    /// <summary>写入点最集中的类型（全部候选之外也统计，帮助人判断文本到底从哪个类流出）。</summary>
    public string TypeFullName { get; set; } = "";
    public int Calls { get; set; }
    public int Methods { get; set; }
    public string SampleMethod { get; set; } = "";
}

public sealed class FrameworkTrace
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>known = 已知对话框架；custom = 自定义 *Dialog*/*Talk* 命名。</summary>
    public string Category { get; set; } = "custom";
    public int TypeCount { get; set; }
    public int MethodCount { get; set; }
    public List<string> SampleTypes { get; set; } = new();
    public bool AffectsScoring { get; set; } = true;
}

public sealed class TextCandidate
{
    public int Rank { get; set; }
    public string Kind { get; set; } = "unknown";          // dialogue|choice|menu|debug|unknown
    public double Score { get; set; }
    public string Tier { get; set; } = "excluded";          // A|B|C|excluded
    public string Confidence { get; set; } = "low";         // high|medium|low
    public string MethodFullName { get; set; } = "";
    public string TypeFullName { get; set; } = "";
    public string MethodName { get; set; } = "";
    public string Signature { get; set; } = "";
    public string DiscoveredBy { get; set; } = "";          // direct-text-setter|upstream-of-text-setter
    public Dictionary<string, double> RuleScores { get; set; } = new();
    public List<string> Evidence { get; set; } = new();
    public int TextSinkCalls { get; set; }
    public List<string> TextSinkApis { get; set; } = new();
    public string ValueSource { get; set; } = "unknown";    // constant|parameter|field|local|call|mixed|unknown|none
    public int CjkLiteralCount { get; set; }
    public int LiteralCount { get; set; }
    public int CallerCount { get; set; }
    public List<string> CallerSamples { get; set; } = new();
    public bool TypeHoldsTextFields { get; set; }
    public int TextFieldCount { get; set; }
    public string Framework { get; set; } = "";
    /// <summary>候选方法是否声明在已知框架/自定义对话类型的调用链上层（S1 来源）。</summary>
    public string FrameworkCategory { get; set; } = "none"; // known|custom|none
    public bool OnKnownFrameworkType { get; set; }
    /// <summary>人工复核标记（system-ui/i18n-refresh/sample-code/framework-internal/multi-write/hardcoded-text/upstream-only）。
    /// 只是证据标注，不参与 R4 打分。</summary>
    public List<string> Flags { get; set; } = new();
}

public sealed class CoverageInfo
{
    public List<string> Can { get; set; } = new();
    public List<string> Cannot { get; set; } = new();
    /// <summary>R4 §11.4 的 8 项运行期确认（静态拿不到的语义）。</summary>
    public List<string> RuntimeConfirmations { get; set; } = new();
    public Thresholds Thresholds { get; set; } = new();
    public List<DimensionInfo> Dimensions { get; set; } = new();
}

public sealed class Thresholds
{
    public double TierA { get; set; } = 70;
    public double TierB { get; set; } = 50;
    public double TierC { get; set; } = 30;
    public string TierARule { get; set; } = "total>=70 且 S1>0";
}

public sealed class DimensionInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double Points { get; set; }
    public bool Applicable { get; set; } = true;
    public string Reason { get; set; } = "";
}

public sealed class TimingInfo
{
    public long TotalMs { get; set; }
    public long DiscoveryMs { get; set; }
    public long CandidateScanMs { get; set; }
    public long FullScanMs { get; set; }
    public long ScoringMs { get; set; }
    public long ReportMs { get; set; }
    public List<CandidateTiming> Candidates { get; set; } = new();
}

public sealed class CandidateTiming
{
    public string DataDirName { get; set; } = "";
    public long Ms { get; set; }
    public bool Selected { get; set; }
}
