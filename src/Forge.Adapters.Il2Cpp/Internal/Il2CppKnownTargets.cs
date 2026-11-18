namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>已知文本通道的角色。</summary>
internal enum Il2CppChannelRole
{
    /// <summary>台词入口/出口。</summary>
    Dialogue,

    /// <summary>说话人名。</summary>
    Speaker,

    /// <summary>选项。</summary>
    Choice,

    /// <summary>文本事件（需要委托转换订阅）。</summary>
    Event,
}

/// <summary>
/// 一条<b>已实测</b>的文本通道。<c>Probe</c> 对应 R18 探针编号，
/// 分数来自 <c>research/20-il2cpp-re/evidence/candidate-hooks.csv</c> 的 total 列（同样是实测结论）。
/// </summary>
internal sealed class Il2CppKnownTarget
{
    public string TypeFullName { get; init; } = string.Empty;

    public string MethodName { get; init; } = string.Empty;

    public int Score { get; init; }

    public int Rank { get; init; }

    public Il2CppChannelRole Role { get; init; }

    public string Probe { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;

    public string Display => TypeFullName + "::" + MethodName;
}

/// <summary>
/// 已知框架通道表。**只收录 R18 真机探针实测过的 Naninovel 通道**，
/// 其他框架仍走通用规则（<see cref="Il2CppScoring"/> 的 S1–S10 / N1–N7）。
///
/// <para>
/// 为什么需要这张表：通用规则给所有 <c>set_Text(string)</c> 同样的分数（都是 25+20+20+10+10+5=90），
/// 但实测差别巨大——<c>UITextPrinter.set_Text</c> 收到了非空台词，<c>RevealableTMProText.set_Text</c>
/// 运行期 0 次调用，<c>AuthorNamePanel.set_Text</c> 只是说话人名。没有这张表，生成器会把
/// 说话人名面板当成台词出口（2026-11-14 冒烟验证里确实发生了）。
/// </para>
/// <para>
/// 证据：<c>research/20-il2cpp-re/REPORT.md</c> 与 <c>evidence/candidate-hooks.csv</c>（10 行）。
/// 未收录的通道一律视为“未实测”，由通用规则给分并标注未验证。
/// </para>
/// </summary>
internal static class Il2CppKnownTargets
{
    private static readonly Il2CppKnownTarget[] Naninovel =
    [
        new()
        {
            TypeFullName = "Naninovel.TextPrinterManager",
            MethodName = "PrintTextAsync",
            Score = 100,
            Rank = 0,
            Role = Il2CppChannelRole.Dialogue,
            Probe = "G",
            Note = "管理器入口，每行台词必经此处；AsyncToken 形参不是简单类型，补丁只读 string 形参",
        },
        new()
        {
            TypeFullName = "Naninovel.UITextPrinter",
            MethodName = "set_Text",
            Score = 100,
            Rank = 1,
            Role = Il2CppChannelRole.Dialogue,
            Probe = "A",
            Note = "R18 探针 A：6 次调用 / 1 次非空，真正把整行交给打印机的出口",
        },
        new()
        {
            TypeFullName = "Naninovel.UI.RevealableTextPrinterPanel",
            MethodName = "set_PrintedText",
            Score = 95,
            Rank = 2,
            Role = Il2CppChannelRole.Dialogue,
            Probe = "B",
            Note = "R18 探针 B：14 次调用 / 1 次非空，面板层聚合出口",
        },
        new()
        {
            TypeFullName = "Naninovel.UI.RevealableUIText",
            MethodName = "set_Text",
            Score = 90,
            Rank = 3,
            Role = Il2CppChannelRole.Dialogue,
            Probe = "I",
            Note = "R18 探针 I：14 次调用 / 1 次非空；本作实际走 uGUI 分支",
        },
        new()
        {
            TypeFullName = "Naninovel.UI.RevealableTMProText",
            MethodName = "set_Text",
            Score = 60,
            Rank = 4,
            Role = Il2CppChannelRole.Dialogue,
            Probe = "D",
            Note = "R18 探针 D：0 次调用；本作走 uGUI 分支，TMPro 分支是死路，60 分低于自动打补丁门槛",
        },
        new()
        {
            TypeFullName = "Naninovel.UI.RevealableTextPrinterPanel",
            MethodName = "set_AuthorNameText",
            Score = 75,
            Rank = 5,
            Role = Il2CppChannelRole.Speaker,
            Probe = "C",
            Note = "R18 探针 C：9 次调用，说话人名",
        },
        new()
        {
            TypeFullName = "Naninovel.UI.UITextPrinterPanel",
            MethodName = "set_AuthorNameText",
            Score = 75,
            Rank = 6,
            Role = Il2CppChannelRole.Speaker,
            Probe = "C'",
            Note = "R18 探针 C 的基类声明（推断），子类覆写时仍需打在子类上",
        },
        new()
        {
            TypeFullName = "Naninovel.TextPrinterManager",
            MethodName = "add_OnPrintTextStarted",
            Score = 90,
            Rank = 7,
            Role = Il2CppChannelRole.Event,
            Probe = "H",
            Note = "R18 探针 H：1 次调用 / 非空；IL2CPP 下必须 ConvertDelegate 后订阅，不能自动生成",
        },
        new()
        {
            TypeFullName = "Naninovel.UI.ChoiceHandlerPanel",
            MethodName = "AddChoiceButton",
            Score = 75,
            Rank = 8,
            Role = Il2CppChannelRole.Choice,
            Probe = "?",
            Note = "R18 未实测；ChoiceState 不是简单类型，只能人工接入",
        },
    ];

    /// <summary>按 <c>类型全名 + 方法名</c> 精确匹配。</summary>
    public static bool TryMatch(Il2CppMethodFact method, out Il2CppKnownTarget target)
    {
        foreach (var item in Naninovel)
        {
            if (string.Equals(item.TypeFullName, method.TypeFullName, StringComparison.Ordinal) &&
                string.Equals(item.MethodName, method.Name, StringComparison.Ordinal))
            {
                target = item;
                return true;
            }
        }

        target = null!;
        return false;
    }

    public static int Count => Naninovel.Length;
}
