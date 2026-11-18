using System.Globalization;

namespace GalA11yForge.Adapters.Il2Cpp.Internal;

/// <summary>某条通道的运行时实测结果（来自 R18 那种探针 CSV）。</summary>
internal sealed class Il2CppRuntimeFact
{
    public string Key { get; init; } = string.Empty;

    public string TypeFullName { get; init; } = string.Empty;

    public string MethodName { get; init; } = string.Empty;

    /// <summary>补丁被调用次数；<c>null</c> 表示没测过（CSV 里留空）。</summary>
    public int? Calls { get; init; }

    /// <summary>非空字符串次数。这是唯一有意义的“真的读到台词”证据。</summary>
    public int NonEmptyHits { get; init; }

    public string Verdict { get; init; } = string.Empty;

    public string Confidence { get; init; } = string.Empty;

    public bool Tested => Calls is not null;
}

/// <summary>
/// 读取运行时探针证据 CSV（列名与 <c>research/20-il2cpp-re/evidence/candidate-hooks.csv</c> 一致）。
///
/// <para>
/// 为什么必须有这一步：IL2CPP interop 的方法体是调用桩，静态分析**无法**判断某个 setter
/// 运行时到底触发不触发。R18 实测 <c>RevealableTMProText.set_Text</c> 静态分 80、运行时不触发，
/// 而 <c>RevealableUIText.set_Text</c> 才是这条游戏的真正分支。没有运行时证据时适配器必须写
/// “未验证”，有了证据才敢把候选标成 A 级。
/// </para>
/// </summary>
internal sealed class Il2CppRuntimeEvidence
{
    private readonly Dictionary<string, Il2CppRuntimeFact> _byTypeMethod = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Il2CppRuntimeFact> _byMethodName = new(StringComparer.Ordinal);

    private Il2CppRuntimeEvidence(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public int RowCount => _byTypeMethod.Count;

    public List<string> Warnings { get; } = [];

    public bool IsEmpty => _byTypeMethod.Count == 0;

    /// <summary>按 <c>类型全名::方法名</c> 查；查不到再按方法名唯一匹配。</summary>
    public Il2CppRuntimeFact? Find(string typeFullName, string methodName)
    {
        if (_byTypeMethod.TryGetValue(typeFullName + "::" + methodName, out var fact))
        {
            return fact;
        }

        if (_byMethodName.TryGetValue(methodName, out var byName) &&
            string.Equals(byName.TypeFullName, typeFullName, StringComparison.Ordinal))
        {
            return byName;
        }

        return _byMethodName.TryGetValue(methodName, out var loose) ? loose : null;
    }

    public static Il2CppRuntimeEvidence? TryLoad(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var evidence = new Il2CppRuntimeEvidence(path);
        try
        {
            var lines = File.ReadLines(path).Take(4096).ToList();
            if (lines.Count < 2)
            {
                evidence.Warnings.Add("运行时证据文件行数不足（<2），忽略。");
                return evidence;
            }

            var header = SplitCsvLine(lines[0]);
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count; i++)
            {
                index[header[i].Trim()] = i;
            }

            if (!index.ContainsKey("target") || !index.ContainsKey("type"))
            {
                evidence.Warnings.Add("运行时证据 CSV 缺少 target/type 列，忽略。");
                return evidence;
            }

            var typeCol = index["type"];
            var targetCol = index["target"];
            var callsCol = index.TryGetValue("runtime_calls", out var c) ? c : -1;
            var nonEmptyCol = index.TryGetValue("runtime_nonempty", out var n) ? n : -1;
            var verdictCol = index.TryGetValue("runtime_verdict", out var v) ? v : -1;
            var confidenceCol = index.TryGetValue("confidence", out var k) ? k : -1;

            for (var line = 1; line < lines.Count; line++)
            {
                if (string.IsNullOrWhiteSpace(lines[line]))
                {
                    continue;
                }

                var cells = SplitCsvLine(lines[line]);
                var type = Cell(cells, typeCol);
                var target = Cell(cells, targetCol);
                if (type.Length == 0 || target.Length == 0)
                {
                    continue;
                }

                var methodName = MethodNameOf(target);
                if (methodName.Length == 0)
                {
                    continue;
                }

                var calls = ParseInt(Cell(cells, callsCol));
                var nonEmpty = ParseInt(Cell(cells, nonEmptyCol)) ?? 0;
                var fact = new Il2CppRuntimeFact
                {
                    Key = type + "::" + methodName,
                    TypeFullName = type,
                    MethodName = methodName,
                    Calls = calls,
                    NonEmptyHits = nonEmpty,
                    Verdict = Cell(cells, verdictCol),
                    Confidence = Cell(cells, confidenceCol),
                };

                evidence._byTypeMethod[fact.Key] = fact;
                if (!evidence._byMethodName.ContainsKey(methodName))
                {
                    evidence._byMethodName[methodName] = fact;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            evidence.Warnings.Add("运行时证据读取失败：" + ex.Message);
        }

        return evidence;
    }

    /// <summary><c>UITextPrinter.set_Text(string)</c> → <c>set_Text</c>；<c>add_OnX(Action&lt;T&gt;)</c> → <c>add_OnX</c>。</summary>
    internal static string MethodNameOf(string target)
    {
        var text = target.Trim();
        var paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = text[..paren];
        }

        var dot = text.LastIndexOf('.');
        return dot >= 0 && dot < text.Length - 1 ? text[(dot + 1)..].Trim() : text.Trim();
    }

    internal static int? ParseInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static string Cell(List<string> cells, int index)
        => index >= 0 && index < cells.Count ? cells[index].Trim() : string.Empty;

    /// <summary>支持双引号包裹与 <c>""</c> 转义的最小 CSV 拆分（证据 CSV 只有这一种情况）。</summary>
    internal static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
