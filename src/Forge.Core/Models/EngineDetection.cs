namespace GalA11yForge.Core.Models;

/// <summary>
/// 引擎识别结果。Family 使用适配器注册表里的引擎 id（如 unity_mono、renpy、unity_il2cpp、kirikiriz、rpgmaker_mv_mz）。
/// Confidence 为 0..1；Bitness 为 32/64，0 表示未知。
/// </summary>
public sealed class EngineDetection
{
    public int SchemaVersion { get; set; } = 1;

    public string Family { get; set; } = "unknown";

    public string? Version { get; set; }

    public int Bitness { get; set; }

    public double Confidence { get; set; }

    /// <summary>证据条目，建议写成 "file:相对路径"、"measured:命令输出摘要"、"url:https://...（访问日期）"。</summary>
    public List<string> Evidence { get; set; } = new();
}
