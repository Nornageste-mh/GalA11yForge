namespace GalA11yForge.Gui.Core;

/// <summary>一个命令按钮的助记符（Alt+字母 直接执行）。</summary>
public sealed record CommandShortcut(ForgeCommandKind Kind, char Mnemonic, string Tooltip);

/// <summary>
/// 键盘快捷键表：按钮上的 (字母) 与 Alt+字母 都来自这里。
/// 集中一处是为了可测——助记符必须唯一，否则 WinForms 会把 Alt+字母 分给第一个匹配的控件，
/// 屏幕上写着的字母就名不副实。
/// </summary>
public static class ForgeShortcuts
{
    /// <summary>「一键」：识别 → 分析 → 生成 → 构建。</summary>
    public const char OneClick = 'O';

    /// <summary>停止当前执行（Esc 同样有效）。</summary>
    public const char Stop = 'X';

    /// <summary>清空结果区（不动执行记录）。</summary>
    public const char ClearResult = 'L';

    /// <summary>重新朗读最近一次结果。</summary>
    public const char ReadResult = 'T';

    /// <summary>按钮顺序与视觉顺序一致：第一行识别→分析→生成→构建→报告→一键，第二行安装→校验→自检→回滚→停止→清空。</summary>
    public static IReadOnlyList<CommandShortcut> Buttons { get; } = new[]
    {
        new CommandShortcut(ForgeCommandKind.Detect, 'D', "识别引擎（Alt+D）"),
        new CommandShortcut(ForgeCommandKind.Analyze, 'A', "分析文本（Alt+A）"),
        new CommandShortcut(ForgeCommandKind.Generate, 'G', "生成工程（Alt+G）"),
        new CommandShortcut(ForgeCommandKind.Build, 'B', "构建补丁（Alt+B）"),
        new CommandShortcut(ForgeCommandKind.Report, 'P', "分析报告（Alt+P，只写工作区）"),
        new CommandShortcut(ForgeCommandKind.Install, 'I', "安装补丁（Alt+I，会写入游戏目录）"),
        new CommandShortcut(ForgeCommandKind.Verify, 'Y', "校验安装（Alt+Y）"),
        new CommandShortcut(ForgeCommandKind.Doctor, 'V', "环境自检（Alt+V，不需要游戏目录）"),
        new CommandShortcut(ForgeCommandKind.Rollback, 'R', "回滚补丁（Alt+R，会写入游戏目录）"),
    };

    /// <summary>输入控件已有 Alt+1..Alt+9（语音日志 Alt+5、演练模式 Alt+6、高级分析 Alt+7、分析程序 Alt+8、允许强制 Alt+9），命令按钮不得与其冲突。</summary>
    public static IReadOnlyList<char> InputMnemonics { get; } = new[] { '1', '2', '3', '4', '5', '6', '7', '8', '9' };

    public static char MnemonicOf(ForgeCommandKind kind)
    {
        foreach (var shortcut in Buttons)
        {
            if (shortcut.Kind == kind)
            {
                return shortcut.Mnemonic;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "没有为该命令配置助记符。");
    }

    /// <summary>界面上出现的全部助记符（命令 + 一键 + 停止 + 清空），用于唯一性断言。</summary>
    public static IReadOnlyList<char> AllMnemonics()
    {
        var all = new List<char>();
        foreach (var shortcut in Buttons)
        {
            all.Add(shortcut.Mnemonic);
        }

        all.Add(OneClick);
        all.Add(Stop);
        all.Add(ClearResult);
        all.Add(ReadResult);
        return all;
    }
}
