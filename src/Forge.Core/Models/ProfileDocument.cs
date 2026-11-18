using System.Text.Json;

namespace GalA11yForge.Core.Models;

/// <summary>
/// 用户可编辑的游戏配置（games/&lt;slug&gt;/profile.json）。
/// overrides.json 是同一目录下的覆盖文件，优先级更高；合并规则见 ProfileMerger。
/// </summary>
public sealed class ProfileDocument
{
    public int SchemaVersion { get; set; } = 1;

    public string? Slug { get; set; }

    public string? GamePath { get; set; }

    /// <summary>强制指定引擎 id（覆盖自动识别）。</summary>
    public string? Engine { get; set; }

    /// <summary>自动识别的最低置信度，低于此值要求人工确认。0..1。</summary>
    public double? MinConfidence { get; set; }

    public bool? Advanced { get; set; }

    /// <summary>万能层（通用文本捕获）开关；v1 默认实验性。</summary>
    public bool? UniversalCapture { get; set; }

    /// <summary>语音输出通道，例如 sapi / nvda / auto。</summary>
    public string? Voice { get; set; }

    public Dictionary<string, string> Keymap { get; set; } = new();

    /// <summary>适配器私有配置；按对象递归合并，数组整体替换。</summary>
    public Dictionary<string, JsonElement> Options { get; set; } = new();

    public string? LastResult { get; set; }
}
