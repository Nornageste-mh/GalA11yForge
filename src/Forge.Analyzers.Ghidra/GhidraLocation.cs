namespace GalA11yForge.Analyzers.Ghidra;

/// <summary>
/// 一次 Ghidra 定位的结果。可用性只由 <c>support/analyzeHeadless.bat</c> 是否存在决定，
/// 找不到时 <see cref="Error"/> 与 <see cref="Probes"/> 会保留排查线索，供 CLI 如实写进 unverified。
/// </summary>
public sealed class GhidraLocation
{
    public bool Available { get; init; }

    /// <summary>Ghidra 安装目录（含 support/ 与 Ghidra/ 的那一层）。</summary>
    public string? HomeDirectory { get; init; }

    /// <summary>support/analyzeHeadless.bat 的绝对路径。</summary>
    public string? AnalyzeHeadlessPath { get; init; }

    /// <summary>从 Ghidra/application.properties 读到的版本，例如 12.1.4。</summary>
    public string? Version { get; init; }

    /// <summary>定位来源：option / env:GHIDRA_HOME / env:GHIDRA_INSTALL_DIR / probe。</summary>
    public string? Source { get; init; }

    public string? Error { get; init; }

    /// <summary>尝试过的路径（最多保留 20 条），只写路径与结论，不写环境变量全文。</summary>
    public IReadOnlyList<string> Probes { get; init; } = Array.Empty<string>();
}
