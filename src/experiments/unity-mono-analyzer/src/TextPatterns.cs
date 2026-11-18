using System.Text;
using System.Text.RegularExpressions;

namespace UnityMonoAnalyzer;

/// <summary>
/// 全部静态文本匹配规则集中在此，便于单测与审计。
/// 注意：本类只做"是否/计数"判断，从不把字面量内容传出去（版权红线，见 AGENTS.md §5）。
/// </summary>
public static class TextPatterns
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

    // N1 调试/日志/版本类命名
    private static readonly HashSet<string> DebugTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "debug", "log", "logger", "logging", "fps", "version", "ver", "test", "tests", "stats", "stat",
        "benchmark", "diagnostic", "diagnostics", "profiler", "profile", "trace", "cheat", "console",
    };

    // S5 类名 UI/窗口类
    private static readonly string[] UiNameParts = { "Window", "Dialog", "Box", "Panel", "Menu", "Popup", "View" };

    // 启动器/更新器可疑命名（候选排序用，不参与 R4 打分）
    private static readonly HashSet<string> LauncherTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher", "login", "signin", "updater", "update", "download", "news", "patch", "patcher",
        "crash", "bootstrap", "installer", "splash", "credits", "agreement", "eula", "privacy",
    };

    // 已知对话框架（类型命名空间前缀或类型名前缀）
    public static readonly (string Id, string DisplayName, string[] Prefixes)[] KnownFrameworks =
    {
        ("naninovel", "Naninovel", new[] { "Naninovel" }),
        ("nodecanvas.dialoguetrees", "NodeCanvas DialogueTrees", new[] { "NodeCanvas.DialogueTrees", "NodeCanvas.Framework" }),
        ("scenarioforge", "ScenarioForge", new[] { "ScenarioForge" }),
        ("fungus", "Fungus", new[] { "Fungus" }),
        ("yarn", "Yarn Spinner", new[] { "Yarn.Unity", "Yarn" }),
        ("ink", "Ink", new[] { "Ink.Runtime", "Ink" }),
        ("pixelcrushers", "PixelCrushers Dialogue System", new[] { "PixelCrushers.DialogueSystem", "PixelCrushers" }),
        ("adventurecreator", "Adventure Creator", new[] { "AdventureCreator" }),
        ("dialogue.system", "Dialogue System (generic)", new[] { "DialogueSystem" }),
        ("textanimator", "Text Animator", new[] { "Febucci.UI", "Febucci" }),
        ("ugui.extensions", "Unity UI Extensions", new[] { "UnityEngine.UI.Extensions" }),
    };

    // 自定义对话框架痕迹：类型名/命名空间含 dialog|talk|conversation
    private static readonly Regex CustomDialogueRx = new(@"(?i)(dialog|talk|conversation)", RegexOptions.Compiled);
    private static readonly Regex DialogueNamedRx = new(@"(?i)(dialogue|dialog|talk|scenario|script|story|novel|adv|conversation)", RegexOptions.Compiled);

    // N2 非台词样字面量
    private static readonly Regex PathRx = new(@"^([A-Za-z]:[\\/]|\\\\|\.{0,2}[\\/])", RegexOptions.Compiled);
    private static readonly Regex UrlRx = new(@"(?i)^(https?|ftp)://", RegexOptions.Compiled);
    private static readonly Regex FormatRx = new(@"(\{[0-9]+\}|\{\d+:[^}]*\}|%[sdfxX]|\\n)", RegexOptions.Compiled);
    private static readonly Regex NumberRx = new(@"^[\s0-9.,:+\-/TZ]*$", RegexOptions.Compiled);
    private static readonly Regex LowerIdentRx = new(@"^[a-z][a-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex DottedIdentRx = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled);

    public static bool IsCjk(char c) => CjkRx.IsMatch(c.ToString());

    public static int CountCjk(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        return CjkRx.Matches(s).Count;
    }

    public static bool ContainsCjk(string s) => !string.IsNullOrEmpty(s) && CjkRx.IsMatch(s);

    /// <summary>把标识符/完整类型名切成词元：`LoveStoryProject.BacklogItem` → Love|Story|Project|Backlog|Item。</summary>
    public static IReadOnlyList<string> Tokenize(string? name)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(name)) return result;
        foreach (var raw in name.Split(new[] { '.', '/', '+', '<', '>', '_', '$', '`', ' ', '-', '(', ')', '[', ']', ',', ':' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsLetterOrDigit(s[i]))
                {
                    if (i > start) result.Add(s[start..i]);
                    start = i + 1;
                    continue;
                }
                if (i > start && char.IsUpper(s[i]))
                {
                    bool prevLower = char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]);
                    bool acronymEnd = char.IsUpper(s[i - 1]) && i + 1 < s.Length && char.IsLower(s[i + 1]);
                    if (prevLower || acronymEnd)
                    {
                        result.Add(s[start..i]);
                        start = i;
                    }
                }
            }
            if (s.Length > start) result.Add(s[start..]);
        }
        return result;
    }

    /// <summary>S2：方法名语义命中。</summary>
    public static bool HasSemanticName(string? methodName)
    {
        if (string.IsNullOrEmpty(methodName)) return false;
        foreach (var t in Tokenize(methodName))
            if (SemanticTokens.Contains(t)) return true;
        var lower = methodName.ToLowerInvariant();
        foreach (var c in SemanticCompounds)
            if (lower.Contains(c, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>N1：调试/日志/版本类命名。</summary>
    public static bool HasDebugName(string? symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return false;
        foreach (var t in Tokenize(symbol))
            if (DebugTokens.Contains(t)) return true;
        return false;
    }

    /// <summary>S5：类名像 UI/窗口容器。</summary>
    public static bool HasUiName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        foreach (var p in UiNameParts)
            if (typeName.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        // 独立 "UI" 只在大小写完全一致时算（避免 quit/quick 之类误命中）
        return typeName.Contains("UI", StringComparison.Ordinal);
    }

    public static bool HasLauncherSuspicion(string? typeFullName)
    {
        if (string.IsNullOrEmpty(typeFullName)) return false;
        foreach (var t in Tokenize(typeFullName))
            if (LauncherTokens.Contains(t)) return true;
        return false;
    }

    public static bool IsDialogueNamedType(string? typeFullName) =>
        !string.IsNullOrEmpty(typeFullName) && DialogueNamedRx.IsMatch(typeFullName);

    /// <summary>S1：已知框架命中，返回框架 id（未命中返回 null）。</summary>
    public static string? MatchKnownFramework(string? typeFullName)
    {
        if (string.IsNullOrEmpty(typeFullName)) return null;
        foreach (var (id, _, prefixes) in KnownFrameworks)
            foreach (var p in prefixes)
                if (typeFullName.StartsWith(p + ".", StringComparison.Ordinal) ||
                    typeFullName.Equals(p, StringComparison.Ordinal) ||
                    typeFullName.Contains("." + p + ".", StringComparison.Ordinal))
                    return id;
        return null;
    }

    public static string FrameworkDisplayName(string id)
    {
        foreach (var (fid, name, _) in KnownFrameworks)
            if (fid == id) return name;
        return id;
    }

    /// <summary>S1 自定义分支：类型名/命名空间含 dialog|talk|conversation。</summary>
    public static bool HasCustomDialogueTrace(string? typeFullName) =>
        !string.IsNullOrEmpty(typeFullName) && CustomDialogueRx.IsMatch(typeFullName);

    /// <summary>N2 单项判定：这个字面量是否"不像台词"。</summary>
    public static bool IsNonDialogueLiteral(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        if (ContainsCjk(s)) return false;
        if (PathRx.IsMatch(s)) return true;
        if (UrlRx.IsMatch(s)) return true;
        if (FormatRx.IsMatch(s)) return true;
        if (NumberRx.IsMatch(s)) return true;
        if (LowerIdentRx.IsMatch(s)) return true;
        if (DottedIdentRx.IsMatch(s)) return true;
        return false;
    }

    /// <summary>N4 用：CJK 或含字母的"可能是台词"的短串，也算有效内容。</summary>
    public static bool IsTrivialShortLiteral(string? s) => string.IsNullOrEmpty(s) || s.Length < 2;

    /// <summary>Unity 版本串，如 2021.3.31f1 / 6000.0.59f2 / 5.6.7p1。</summary>
    public static readonly Regex UnityVersionRx = new(
        @"\b((?:20\d\d|6\d{3})\.\d+\.\d+[abcfp]\d+|[45]\.\d+\.\d+[abcfp]\d+)\b", RegexOptions.Compiled);

    public static string? FindUnityVersion(ReadOnlySpan<byte> bytes)
    {
        // 逐字节转 Latin1，避免非法 UTF-8 抛异常
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes) sb.Append(b < 0x80 ? (char)b : '.');
        var m = UnityVersionRx.Match(sb.ToString());
        return m.Success ? m.Value : null;
    }

    /// <summary>安全截断标识符用途的字符串（不用于字面量内容）。</summary>
    public static string Shorten(string? s, int max = 160) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
