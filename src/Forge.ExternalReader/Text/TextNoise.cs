namespace GalA11yForge.ExternalReader;

/// <summary>
/// UI 噪声识别：窗口外壳文本（标题栏/菜单栏/滚动条/最小化按钮…）不是台词。
///
/// 为什么需要它：外部读屏器读的是“整个窗口”，而游戏窗口里同时存在
/// 标题栏、系统菜单、输入法候选框阴影等系统控件；实测 Forge.Gui/LoveNest 的 UIA 树里，
/// 一半以上的文本节点是这些外壳（见 research/33-external-reader/evidence/）。
///
/// 策略上保持保守：只过滤“确定的窗口外壳”控件类型与固定词表，
/// Button 一律不过滤（游戏主菜单的“开始游戏/继续”就是 Button）；
/// 台词里出现“关闭”这类词也不会被误杀，因为词表只对
/// ControlType ∈ {Button, MenuItem} 且整段文本完全相等时才生效。
/// </summary>
public static class TextNoise
{
    private static readonly HashSet<string> ChromeControlTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TitleBar", "MenuBar", "ScrollBar", "ToolBar", "StatusBar", "Menu",
    };

    private static readonly HashSet<string> ChromeVerbs = new(StringComparer.Ordinal)
    {
        "最小化", "最大化", "还原", "关闭", "系统", "系统菜单",
        "Minimize", "Maximize", "Restore", "Close", "System",
        "上一行", "下一行", "上一页", "下一页", "Scroll up", "Scroll down",
    };

    public static bool IsChrome(TextObservation obs, string? windowTitle)
    {
        if (ChromeControlTypes.Contains(obs.ControlType ?? string.Empty)) return true;

        var text = obs.Text;
        if (text.Length == 0) return false;

        if (!string.IsNullOrEmpty(windowTitle) && string.Equals(text, windowTitle, StringComparison.Ordinal))
            return true;

        // 只对“按钮/菜单项”这类本身就表示动作的控件做整段相等过滤，避免误杀台词
        var isActionControl = string.Equals(obs.ControlType, "Button", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(obs.ControlType, "MenuItem", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(obs.ControlType, "Text", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(obs.ControlType, "Pane", StringComparison.OrdinalIgnoreCase);
        if (!isActionControl) return false;

        return ChromeVerbs.Contains(text);
    }
}
