using System.Text.RegularExpressions;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>
/// Unity Mono 文本命名/字面量模式。
/// 规则来源：<c>src/experiments/unity-mono-analyzer/src/TextPatterns.cs</c>（实验原型，只读），
/// 适配器内复制并做小幅扩展；从不把字面量内容传出本类（版权红线，AGENTS.md §5）。
/// </summary>
internal static class MonoTextPatterns
{
    // R4 S6：CJK 命中（平假名/片假名 + CJK 统一表意 + 兼容表意）
    private static readonly Regex CjkRx = new(@"[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff]", RegexOptions.Compiled);

    // S2 语义关键词（按标识符 token 匹配，避免 Outline→Line 这类子串误命中）
    private static readonly HashSet<string> SemanticTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "say", "said", "speech", "line", "lines", "dialogue", "dialog", "talk", "message", "messages",
        "choice", "choices", "select", "selection", "subtitle", "subtitles", "caption", "narration", "narrate",
    };

    // S2 复合关键词（token 化之后会被拆开，故用整名子串匹配）
    private static readonly string[] SemanticCompounds =
    {
        "showtext", "settext", "textset", "addchoice", "showchoice", "setmessage", "showmessage",
        "setdialogue", "showdialogue", "setline", "showline", "setcaption", "showsubtitle",
    };

    // 选项类命名（只用于 kind/排序，不单独加分）
    private static readonly HashSet<string> ChoiceTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "choice", "choices", "select", "selection", "option", "options", "answer", "answers",
    };

    private static readonly HashSet<string> MenuTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "menu", "menus", "settings", "setting", "config", "configuration", "title", "save", "load",
        "backlog", "gallery", "pause",
    };

    // N1 调试/日志/版本类命名
    private static readonly HashSet<string> DebugTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "debug", "log", "logger", "logging", "fps", "version", "ver", "test", "tests", "stats", "stat",
        "benchmark", "diagnostic", "diagnostics", "profiler", "profile", "trace", "cheat", "console",
    };

    // S5 类名 UI/窗口类
    private static readonly string[] UiNameParts = { "Window", "Dialog", "Box", "Panel", "Menu", "Popup", "View" };

    // 已知对话框架（类型命名空间前缀或类型名前缀）
    private static readonly (string Id, string DisplayName, string[] Prefixes)[] KnownFrameworks =
    {
        ("naninovel", "Naninovel", new[] { "Naninovel" }),
        ("nodecanvas.dialoguetrees", "NodeCanvas DialogueTrees", new[] { "NodeCanvas.DialogueTrees", "NodeCanvas.Framework" }),
        ("scenarioforge", "ScenarioForge", new[] { "ScenarioForge" }),
        ("fungus", "Fungus", new[] { "Fungus" }),
        ("yarn", "Yarn Spinner", new[] { "Yarn.Unity", "Yarn" }),
        ("ink", "Ink Runtime", new[] { "Ink.Runtime", "Ink" }),
        ("pixelcrushers", "PixelCrushers Dialogue System", new[] { "PixelCrushers.DialogueSystem", "PixelCrushers" }),
        ("adventurecreator", "Adventure Creator", new[] { "AdventureCreator" }),
        ("dialogue.system", "Dialogue System (generic)", new[] { "DialogueSystem" }),
        ("textanimator", "Text Animator", new[] { "Febucci.UI", "Febucci" }),
        ("ugui.extensions", "Unity UI Extensions", new[] { "UnityEngine.UI.Extensions" }),
        ("utage", "Utage（宴）", new[] { "Utage" }),
    };

    // Utage（宴）正文管线：每句翻页时 AdvMessageWindow.PageTextChange(AdvPage) 把 page.TextData
    // 存到实例的 Text 属性，再回调 IAdvMessageWindow.OnTextChanged 写 UnityEngine.UI.Text。
    // 两个入口都没有 string 形参，文本在属性链上（0.2.48 依据真实游戏 Mystery Lover 2 取证）。
    private static readonly HashSet<string> UtageMessageWindowTypes = new(StringComparer.Ordinal)
    {
        "Utage.AdvMessageWindow",
        "Utage.AdvMessageWindowManager",
        "Utage.AdvUguiMessageWindow",
        "Utage.AdvPage",
        "Utage.TextData",
    };

    private static readonly Regex CustomDialogueRx = new(@"(?i)(dialog|talk|conversation)", RegexOptions.Compiled);
    private static readonly Regex DialogueNamedRx = new(@"(?i)(dialogue|dialog|talk|scenario|script|story|novel|adv|conversation)", RegexOptions.Compiled);

    // N2 非台词样字面量
    private static readonly Regex PathRx = new(@"^([A-Za-z]:[\\/]|\\\\|\.{0,2}[\\/])", RegexOptions.Compiled);
    private static readonly Regex UrlRx = new(@"(?i)^(https?|ftp)://", RegexOptions.Compiled);
    private static readonly Regex FormatRx = new(@"(\{[0-9]+\}|\{\d+:[^}]*\}|%[sdfxX]|\\n)", RegexOptions.Compiled);
    private static readonly Regex NumberRx = new(@"^[\s0-9.,:+\-/TZ]*$", RegexOptions.Compiled);
    private static readonly Regex LowerIdentRx = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex DottedIdentRx = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled);

    public static bool ContainsCjk(string s) => !string.IsNullOrEmpty(s) && CjkRx.IsMatch(s);

    public static int CountCjk(string s) => string.IsNullOrEmpty(s) ? 0 : CjkRx.Matches(s).Count;

    /// <summary>把标识符/完整类型名切成词元：<c>LoveStoryProject.BacklogItem</c> → Love|Story|Project|Backlog|Item。</summary>
    public static IReadOnlyList<string> Tokenize(string? name)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(name))
        {
            return result;
        }

        foreach (var raw in name.Split(
                     new[] { '.', '/', '+', '<', '>', '_', '$', '`', ' ', '-', '(', ')', '[', ']', ',', ':' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw;
            var start = 0;
            for (var i = 0; i < s.Length; i++)
            {
                if (!char.IsLetterOrDigit(s[i]))
                {
                    if (i > start)
                    {
                        result.Add(s[start..i]);
                    }

                    start = i + 1;
                    continue;
                }

                if (i > start && char.IsUpper(s[i]))
                {
                    var prevLower = char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]);
                    var acronymEnd = char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                    if (prevLower || acronymEnd)
                    {
                        result.Add(s[start..i]);
                        start = i;
                    }
                }
            }

            if (s.Length > start)
            {
                result.Add(s[start..]);
            }
        }

        return result;
    }

    public static bool HasToken(string? name, IReadOnlySet<string> tokens)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var t in Tokenize(name))
        {
            if (tokens.Contains(t))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>S2：方法名语义命中。</summary>
    public static bool HasSemanticName(string? methodName)
    {
        if (string.IsNullOrEmpty(methodName))
        {
            return false;
        }

        foreach (var t in Tokenize(methodName))
        {
            if (SemanticTokens.Contains(t))
            {
                return true;
            }
        }

        var lower = methodName.ToLowerInvariant();
        foreach (var c in SemanticCompounds)
        {
            if (lower.Contains(c, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasTextToken(string? name)
    {
        foreach (var t in Tokenize(name))
        {
            if (t.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("texts", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsChoiceName(string? name) => HasToken(name, ChoiceTokens);

    public static bool IsMenuName(string? name) => HasToken(name, MenuTokens);

    /// <summary>N1：调试/日志/版本类命名。</summary>
    public static bool HasDebugName(string? symbol) => HasToken(symbol, DebugTokens);

    /// <summary>S5：类名像 UI/窗口容器。为 Unity 补上 Text/TMP/Label 命名。</summary>
    public static bool HasUiName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return false;
        }

        foreach (var p in UiNameParts)
        {
            if (typeName.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (typeName.Contains("UI", StringComparison.Ordinal))
        {
            return true;
        }

        // UIText / MessageText / TMPText 是 Galgame 最常见的文本宿主命名。
        return HasTextToken(typeName) && typeName.Length <= 80;
    }

    public static bool IsDialogueNamedType(string? typeFullName) =>
        !string.IsNullOrEmpty(typeFullName) && DialogueNamedRx.IsMatch(typeFullName);

    public static bool HasCustomDialogueTrace(string? typeFullName) =>
        !string.IsNullOrEmpty(typeFullName) && CustomDialogueRx.IsMatch(typeFullName);

    /// <summary>S1：已知框架命中，返回框架 id（未命中返回 null）。</summary>
    public static string? MatchKnownFramework(string? typeFullName)
    {
        if (string.IsNullOrEmpty(typeFullName))
        {
            return null;
        }

        foreach (var (id, _, prefixes) in KnownFrameworks)
        {
            foreach (var p in prefixes)
            {
                if (typeFullName.StartsWith(p + ".", StringComparison.Ordinal) ||
                    typeFullName.Equals(p, StringComparison.Ordinal) ||
                    typeFullName.Contains("." + p + ".", StringComparison.Ordinal))
                {
                    return id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 0.2.48：Utage（宴）正文相关类型。正文管线里 AdvMessageWindow.PageTextChange(AdvPage)
    /// 没有 string 形参，文本在实例属性链上，扫描器需要单独保留。
    /// </summary>
    public static bool IsUtageDialogueType(string? typeFullName) =>
        !string.IsNullOrEmpty(typeFullName) && UtageMessageWindowTypes.Contains(typeFullName);

    /// <summary>
    /// 0.2.48：Utage 正文入口 → 实例属性链（按顺序反射读取，最后一段必须是 string）。
    /// 目前只认最稳的 AdvMessageWindow.PageTextChange；管理器/视图层只做保留不计入口。
    /// </summary>
    public static bool TryUtagePropertyChain(string? typeFullName, string? methodName, out string[] chain)
    {
        chain = Array.Empty<string>();
        if (string.IsNullOrEmpty(typeFullName) || string.IsNullOrEmpty(methodName))
        {
            return false;
        }

        if (string.Equals(typeFullName, "Utage.AdvMessageWindow", StringComparison.Ordinal) &&
            string.Equals(methodName, "PageTextChange", StringComparison.Ordinal))
        {
            chain = new[] { "Text", "OriginalText" };
            return true;
        }

        return false;
    }

    /// <summary>把属性链渲染成可读文本：Text.OriginalText。</summary>
    public static string DescribePropertyChain(IReadOnlyList<string> chain) => string.Join(".", chain);

    public static string FrameworkDisplayName(string id)
    {
        foreach (var (fid, name, _) in KnownFrameworks)
        {
            if (fid == id)
            {
                return name;
            }
        }

        return id;
    }

    /// <summary>N2 单项判定：这个字面量是否“不像台词”。</summary>
    public static bool IsNonDialogueLiteral(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return true;
        }

        if (ContainsCjk(s))
        {
            return false;
        }

        if (PathRx.IsMatch(s) || UrlRx.IsMatch(s) || FormatRx.IsMatch(s) || NumberRx.IsMatch(s) ||
            LowerIdentRx.IsMatch(s) || DottedIdentRx.IsMatch(s))
        {
            return true;
        }

        return false;
    }

    /// <summary>N4 用：长度小于 2 的常量视为“无信息量”。</summary>
    public static bool IsTrivialShortLiteral(string? s) => string.IsNullOrEmpty(s) || s.Length < 2;
}
