using System.Text.RegularExpressions;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>
/// IL2CPP 静态分析的判定词表。集中放一处，便于测试逐条钉住。
/// 注意：这里只做**名字/类型**判定，从不读取、也不保存任何台词内容。
/// </summary>
internal static class Il2CppPatterns
{
    /// <summary>参数名像台词本体（而不是 printerId / authorId 这类标识）。</summary>
    internal static readonly Regex TextParameterNameRx =
        new(@"^(text|line|body|content|message|msg|sentence|dialog|dialogue|str|value|label)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>参数名明显是标识/路径/回调，优先级降到最低。</summary>
    internal static readonly Regex NonTextParameterNameRx =
        new(@"(id|guid|path|key|tag|name|type|index|token|callback|handler|asset)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>方法名语义：打印/显示/逐字/设置文本。</summary>
    internal static readonly Regex SemanticMethodNameRx =
        new(@"((print|show|display|reveal|typewrite|typeout|write|say|emit|publish|output|render|set|update)[_a-z0-9]*text)|(text[_a-z0-9]*(print|show|display|reveal|set|update|render))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>逐字机（typewriter/reveal）相关名字。</summary>
    internal static readonly Regex TypewriterNameRx =
        new(@"(reveal|typewrit|typeout|charper|speed|delay|progress)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static readonly Regex ChoiceNameRx =
        new(@"(choice|select|option|answer|branch)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>展示型方法名：字段兜底只看“把文本显示出来”的入口，避开 UI 框架噪声。</summary>
    internal static readonly Regex FieldFallbackNameRx =
        new(@"^(show|display|set|update|refresh|print|render|reveal|handle|apply|bind|populate|fill|change|write|type)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 字段兜底要跳过的“不是台词”的字段名：调试/状态/路径/键值这类文本读出来只会吵。
    /// （0.2.12 真机：LayeredWindowPresenter._diag 会把 “not started” 当台词读。）
    /// 只匹配“开头或结尾”的整词，避免把 _dialogueText 里的 “logue” 误判成 log。
    /// </summary>
    internal static readonly Regex FieldNameExcludeRx =
        new(@"(^|_)(diag|debug|log|status|path|key|hash|url|version|prefab)|(diag|debug|log|status|path|key|hash|url|version|prefab)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 主动“展示/设置文本”的方法名：字段兜底候选里优先选这类入口，
    /// 让 ShowNode 排在 Apply*/Handle* 前面（0.2.12 真机选点顺序修正）。
    /// </summary>
    internal static readonly Regex FieldFallbackPrimaryNameRx =
        new(@"^(show|display|print|render|reveal|set|refresh|populate|fill|type)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// “像对话宿主”的类型名：字段兜底候选里再排一次优先级，让 DialogueBubbleUI 排在
    /// LocalizedText / MemoFullView 这类通用或界面工具类前面（0.2.15 真机选点顺序）。
    /// </summary>
    internal static readonly Regex FieldHostPriorityRx =
        new(@"(dialogue|dialog|chat|bubble|subtitle|caption|speech|story|say)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>像承载文本的界面类型名（字段兜底再加一层门，避免把调试/工具类当对话入口）。</summary>
    internal static readonly Regex TextHostTypeNameRx =
        new(@"(ui|view|panel|text|bubble|dialogue|dialog|label|window|hud|story|caption|speech)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// “装饰性容器”类型名：单元格、格子、列表项、文件夹、图标这类地方的文本通常是道具名/按钮字，
    /// 不是当前台词。0.2.12 真机里 <c>GiftCellView._text</c> 会把饰品名当台词读、<c>LilithFolderView</c> 会读界面字。
    /// </summary>
    internal static readonly Regex FieldHostExcludeRx =
        new(@"(cell|slot|item|folder|grid|list|tab|scroll|button|toggle|badge|icon|avatar|thumbnail|photo|sprite)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static readonly Regex DebugNameRx =        new(@"^(log|debug|trace|warn|error|assert|print$|printf|draw|gizmo)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static readonly Regex LifecycleNameRx =
        new(@"^(update|lateupdate|fixedupdate|start|awake|onenable|ondisable|ondestroy|ongui|onvalidate|reset)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>文本类类型名（属性/字段的类型名比对这些）。</summary>
    internal static readonly HashSet<string> TextLikeTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TMP_Text", "TextMeshProUGUI", "TextMeshPro", "String", "Il2CppSystem.String",
        "IRevealableText", "RevealableText", "RevealableUIText", "RevealableTMProText",
        "UITextPrinter", "UILabel", "NGUIText", "RichText",
    };

    /// <summary>名字像“文本成员”的属性/字段名。</summary>
    internal static readonly Regex TextMemberNameRx =
        new(@"(text|line|body|caption|message|label|title|author|speaker|sentence|utterance|content|dialogue)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>真正的文本出口属性名（R18 实测 <c>set_Text</c> 是命中最多的通道）。</summary>
    internal static readonly HashSet<string> TextSinkPropertyNames = new(StringComparer.Ordinal)
    {
        "Text", "PrintedText", "AuthorNameText", "LabelText", "FullText", "Message",
        "Content", "Caption", "DisplayText", "TextContent", "RawText",
    };

    /// <summary>
    /// 事件名像“文本/台词相关”（配合 <c>add_</c> 前缀使用）。
    /// IL2CPP interop 不生成 <c>EventDefinition</c>，只能从 <c>add_X</c> 方法名反推事件语义。
    /// </summary>
    internal static readonly Regex TextEventNameRx =
        new(@"(text|print|dialogue|line|say|reveal)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Unity 生命周期 / 纯 UI 装配方法，作为负分信号。</summary>
    internal static readonly Regex ObfuscatedNameRx =
        new(@"^[^\x20-\x7e]{1,3}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 已知对话框架特征。IL2CPP 下这些框架的文本出口几乎固定在少量类型上，
    /// 命中即高优先级（列表来自 research/04-engines 的 playbook，不针对单一游戏）。
    /// </summary>
    internal static readonly (string Prefix, string FrameworkId)[] KnownFrameworks =
    [
        ("Elringus.Naninovel", "naninovel"),
        ("Naninovel.", "naninovel"),
        ("Fungus.", "fungus"),
        ("PixelCrushers.DialogueSystem", "dialogue_system"),
        ("Yarn.Unity", "yarn"),
        ("Ink.Runtime", "ink"),
        ("DG.Tweening", "dotween_ui"),
    ];

    public static string? DetectFramework(string assemblyName, string typeFullName)
    {
        foreach (var (prefix, id) in KnownFrameworks)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                typeFullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    public static bool IsTextLikeType(string? typeName)
        => !string.IsNullOrEmpty(typeName) && TextLikeTypeNames.Contains(typeName);

    public static bool LooksLikeTextMember(string name) => TextMemberNameRx.IsMatch(name);

    /// <summary>从参数表里挑出“最像台词”的 string 参数名。</summary>
    public static string? PickTextParameter(IReadOnlyList<Il2CppParameterFact> parameters)
    {
        string? fallback = null;
        foreach (var p in parameters)
        {
            if (!p.IsString)
            {
                continue;
            }

            fallback ??= p.Name;
            if (TextParameterNameRx.IsMatch(p.Name) && !NonTextParameterNameRx.IsMatch(p.Name))
            {
                return p.Name;
            }
        }

        return fallback;
    }
}
