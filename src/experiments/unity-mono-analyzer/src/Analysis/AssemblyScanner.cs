using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace UnityMonoAnalyzer.Analysis;

public sealed class ScanOptions
{
    /// <summary>快速模式：只算排序用聚合指标，不建调用图、不产出 MethodRecord。</summary>
    public bool Quick { get; init; }
    /// <summary>候选上限（防止异常程序集把报告撑爆）。</summary>
    public int MaxCandidates { get; init; } = 400;
}

/// <summary>文本写入点（一条 call/callvirt 指令）。</summary>
public sealed class SinkSite
{
    public string Api { get; init; } = "";
    /// <summary>constant|parameter|field|local|call|other|unknown</summary>
    public string Source { get; init; } = "unknown";
    /// <summary>当 Source=constant 时的字面量长度（不保留内容）。</summary>
    public int ConstantLength { get; init; }
}

public sealed class MethodRecord
{
    public string TypeFullName { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string Namespace { get; init; } = "";
    public string MethodName { get; init; } = "";
    public string MethodFullName { get; init; } = "";
    public string Signature { get; init; } = "";
    public int ParameterCount { get; init; }
    public bool HasStringParameter { get; init; }
    public int TextFieldCount { get; init; }
    public List<string> TextFieldTypeNames { get; init; } = new();
    public List<SinkSite> SinkSites { get; init; } = new();
    public int LdstrCount { get; init; }
    public int CjkLiteralCount { get; init; }
    public int NonDialogueLiteralCount { get; init; }
    public int MaxLiteralLength { get; init; }
    public int CallerCount { get; init; }
    public List<string> CallerSamples { get; init; } = new();
    public bool AllCallersInitLike { get; init; }
    public bool AnyCallerChoiceLike { get; init; }
    public string FrameworkId { get; init; } = "";
    public bool IsDialogueNamed { get; init; }
    public bool IsSemanticNamed { get; init; }
    public bool DiscoveredBySink { get; init; }
}

public sealed class ScanResult
{
    public AssemblyInfo Assembly { get; set; } = new();
    public int Types { get; set; }
    public int MethodsTotal { get; set; }
    public int MethodsWithBody { get; set; }
    public int LdstrTotal { get; set; }
    public int CjkLiteralMethods { get; set; }
    public bool CjkGame { get; set; }
    public int TextFieldTypes { get; set; }
    public int SinkCallSiteCount { get; set; }
    public int SinkCallerMethods { get; set; }
    public List<TextSinkApi> Sinks { get; set; } = new();
    public List<TextSinkHotspot> Hotspots { get; set; } = new();
    public List<FrameworkTrace> Frameworks { get; set; } = new();
    public List<MethodRecord> Candidates { get; set; } = new();
    public int UpstreamCandidateMethods { get; set; }
    public List<string> ResolveFailures { get; set; } = new();
    public QuickMetrics Quick { get; set; } = new();
}

/// <summary>
/// Assembly-CSharp.dll 静态扫描器。
/// 设计约束：
///  * 只输出"计数/标识符"，绝不把字面量内容带出（AGENTS.md §5 版权红线）。
///  * Cecil 对象全部在 using 块内消化，对外只返回 POCO，避免 deferred-read 悬空。
///  * 调用图按 "类型全名::方法名" 聚合（重载合并），这是启发式打分，报告里明确标注。
/// </summary>
public static class AssemblyScanner
{
    private static readonly string[] InitLikeNames =
        { "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy", ".ctor", ".cctor" };

    public static ScanResult Scan(string assemblyPath, ScanOptions options)
    {
        var result = new ScanResult();
        var resolver = new RecordingResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);

        var readerParams = new ReaderParameters
        {
            AssemblyResolver = resolver,
            ReadSymbols = false,
            ReadingMode = ReadingMode.Deferred,
            InMemory = true,
        };

        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);
        var module = assembly.MainModule;

        result.Assembly = new AssemblyInfo
        {
            Path = Path.GetFullPath(assemblyPath),
            SizeBytes = SafeLength(assemblyPath),
            Sha256 = SafeSha256(assemblyPath),
            ModuleName = module.Name ?? "",
            RuntimeVersion = module.Runtime.ToString(),
        };

        var types = FlattenTypes(module.Types);
        result.Types = types.Count;

        // ---------- 元数据层：字段持有 / 框架痕迹 ----------
        var typeMeta = new Dictionary<string, TypeMeta>(StringComparer.Ordinal);
        var frameworkAgg = new Dictionary<string, (int types, int methods, List<string> samples)>(StringComparer.Ordinal);
        int textFieldTypes = 0, knownFrameworkTypes = 0, dialogueNamedTypes = 0, launcherTypes = 0;

        foreach (var t in types)
        {
            var meta = new TypeMeta { FullName = t.FullName, Name = t.Name, Namespace = t.Namespace ?? "" };
            foreach (var f in t.Fields)
                if (IsTextComponentType(f.FieldType)) meta.TextFieldNames.Add(f.FieldType.Name);

            if (meta.TextFieldNames.Count > 0) textFieldTypes++;
            meta.FrameworkId = TextPatterns.MatchKnownFramework(t.FullName) ?? "";
            meta.IsDialogueNamed = TextPatterns.IsDialogueNamedType(t.FullName);
            meta.IsCustomDialogue = meta.FrameworkId.Length == 0 && TextPatterns.HasCustomDialogueTrace(t.FullName);
            meta.IsLauncherLike = TextPatterns.HasLauncherSuspicion(t.FullName);

            if (meta.FrameworkId.Length > 0) knownFrameworkTypes++;
            if (meta.IsDialogueNamed) dialogueNamedTypes++;
            if (meta.IsLauncherLike) launcherTypes++;

            var aggId = meta.FrameworkId.Length > 0 ? meta.FrameworkId
                : meta.IsCustomDialogue ? "custom.dialogue"
                : null;
            if (aggId != null)
            {
                var cur = frameworkAgg.TryGetValue(aggId, out var v) ? v : (0, 0, new List<string>());
                var samples = cur.Item3;
                if (samples.Count < 5) samples.Add(t.FullName);
                frameworkAgg[aggId] = (cur.Item1 + 1, cur.Item2 + t.Methods.Count, samples);
            }

            typeMeta[t.FullName] = meta;
        }
        result.TextFieldTypes = textFieldTypes;

        // ---------- IL 第一遍：字面量统计 + 文本写入点 ----------
        var sinkCallerKeys = new HashSet<string>(StringComparer.Ordinal);      // "Type::Name"
        var sinkApiAgg = new Dictionary<string, (int calls, HashSet<string> callers)>(StringComparer.Ordinal);
        var otherSetterAgg = new Dictionary<string, int>(StringComparer.Ordinal);
        var byEdgeKey = new Dictionary<string, List<MethodAgg>>(StringComparer.Ordinal);
        var hotspotAgg = new Dictionary<string, (int calls, HashSet<string> methods, string sample)>(StringComparer.Ordinal);
        int methodsTotal = 0, methodsWithBody = 0, ldstrTotal = 0, cjkMethods = 0, sinkSites = 0;

        foreach (var t in types)
        {
            var meta = typeMeta[t.FullName];
            foreach (var m in t.Methods)
            {
                methodsTotal++;
                if (!m.HasBody) continue;
                methodsWithBody++;

                var agg = new MethodAgg
                {
                    Meta = meta,
                    Method = m,
                    FullName = m.FullName,
                    Key = EdgeKey(m),
                    IsSemanticNamed = TextPatterns.HasSemanticName(m.Name),
                };

                foreach (var p in m.Parameters)
                    if (p.ParameterType.MetadataType == MetadataType.String) agg.HasStringParameter = true;

                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s)
                    {
                        agg.LdstrCount++;
                        ldstrTotal++;
                        if (s.Length > agg.MaxLiteralLength) agg.MaxLiteralLength = s.Length;
                        if (TextPatterns.ContainsCjk(s)) agg.CjkLiteralCount++;
                        else if (TextPatterns.IsNonDialogueLiteral(s)) agg.NonDialogueLiteralCount++;
                    }
                    else if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is MethodReference mr)
                    {
                        if (!IsSinkMethod(mr))
                        {
                            if (mr.Name is "set_text" or "SetText") Increment(otherSetterAgg, ApiName(mr));
                            continue;
                        }

                        agg.SinkSites.Add(ClassifySite(ins, mr));
                        sinkSites++;
                        sinkCallerKeys.Add(agg.Key);
                        if (!hotspotAgg.TryGetValue(agg.Meta.FullName, out var h))
                            h = (0, new HashSet<string>(StringComparer.Ordinal), agg.Method.Name);
                        h.methods.Add(agg.Method.Name);
                        hotspotAgg[agg.Meta.FullName] = (h.calls + 1, h.methods, h.sample);
                        var api = ApiName(mr);
                        if (!sinkApiAgg.TryGetValue(api, out var a)) a = (0, new HashSet<string>(StringComparer.Ordinal));
                        a.Item2.Add(agg.Key);
                        sinkApiAgg[api] = (a.Item1 + 1, a.Item2);
                    }
                }

                if (agg.CjkLiteralCount > 0) cjkMethods++;
                if (!byEdgeKey.TryGetValue(agg.Key, out var list)) byEdgeKey[agg.Key] = list = new List<MethodAgg>();
                list.Add(agg);
            }
        }

        result.MethodsTotal = methodsTotal;
        result.MethodsWithBody = methodsWithBody;
        result.LdstrTotal = ldstrTotal;
        result.CjkLiteralMethods = cjkMethods;
        result.CjkGame = cjkMethods > 0;
        result.SinkCallSiteCount = sinkSites;
        result.SinkCallerMethods = sinkCallerKeys.Select(k => byEdgeKey.TryGetValue(k, out var l) ? l.Count(a => a.SinkSites.Count > 0) : 0).Sum();

        result.Sinks = sinkApiAgg
            .Select(kv => new TextSinkApi { Api = kv.Key, Calls = kv.Value.calls, CallerMethods = kv.Value.callers.Count, Role = "primary" })
            .OrderByDescending(x => x.Calls).ThenBy(x => x.Api, StringComparer.Ordinal)
            .ToList();
        foreach (var kv in otherSetterAgg.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
            result.Sinks.Add(new TextSinkApi { Api = kv.Key, Calls = kv.Value, CallerMethods = 0, Role = "ignored" });

        result.Hotspots = hotspotAgg
            .Select(kv => new TextSinkHotspot
            {
                TypeFullName = kv.Key,
                Calls = kv.Value.calls,
                Methods = kv.Value.methods.Count,
                SampleMethod = kv.Value.sample,
            })
            .OrderByDescending(x => x.Calls).ThenBy(x => x.TypeFullName, StringComparer.Ordinal)
            .Take(40).ToList();

        result.Frameworks = BuildFrameworkTraces(frameworkAgg);
        result.Quick = new QuickMetrics
        {
            Types = types.Count,
            Methods = methodsTotal,
            TextSinkCalls = sinkSites,
            TextSinkCallerMethods = sinkCallerKeys.Count,
            CjkLiteralMethods = cjkMethods,
            KnownFrameworkTypes = knownFrameworkTypes,
            DialogueNamedTypes = dialogueNamedTypes,
            LauncherSuspicionTypes = launcherTypes,
            Scanned = true,
        };

        if (options.Quick)
        {
            result.ResolveFailures = SortedFailures(resolver);
            return result;
        }

        // ---------- IL 第二遍：只收集流入候选集合的调用边 ----------
        var universe = new HashSet<string>(sinkCallerKeys, StringComparer.Ordinal);
        foreach (var kv in byEdgeKey)
            if (kv.Value.Any(a => a.IsSemanticNamed)) universe.Add(kv.Key);

        var edges = new List<(string Caller, string Callee)>();
        foreach (var t in types)
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var callerKey = EdgeKey(m);
                foreach (var ins in m.Body.Instructions)
                {
                    if ((ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt) || ins.Operand is not MethodReference mr) continue;
                    var calleeKey = EdgeKey(mr);
                    if (universe.Contains(calleeKey)) edges.Add((callerKey, calleeKey));
                }
            }

        var callersOf = edges.GroupBy(e => e.Callee, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Caller).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var calleesOf = edges.GroupBy(e => e.Caller, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Callee).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

        // ---------- 候选集合：直接写入者 + 语义上游一层 ----------
        var selected = new List<MethodRecord>();
        foreach (var key in sinkCallerKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!byEdgeKey.TryGetValue(key, out var aggs)) continue;
            foreach (var agg in aggs.Where(a => a.SinkSites.Count > 0))
                selected.Add(BuildRecord(agg, true, callersOf));
        }

        int upstreamCount = 0;
        foreach (var key in byEdgeKey.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (sinkCallerKeys.Contains(key)) continue;
            if (!calleesOf.TryGetValue(key, out var callees)) continue;
            if (!callees.Overlaps(sinkCallerKeys)) continue;
            foreach (var agg in byEdgeKey[key].Where(a => a.IsSemanticNamed))
            {
                upstreamCount++;
                selected.Add(BuildRecord(agg, false, callersOf));
            }
        }
        result.UpstreamCandidateMethods = upstreamCount;

        if (selected.Count > options.MaxCandidates)
            selected = selected.Where(x => x.DiscoveredBySink)
                .Concat(selected.Where(x => !x.DiscoveredBySink))
                .Take(options.MaxCandidates)
                .ToList();

        result.Candidates = selected;
        result.ResolveFailures = SortedFailures(resolver);
        return result;
    }

    // ---------------- helpers ----------------

    private sealed class TypeMeta
    {
        public string FullName = "";
        public string Name = "";
        public string Namespace = "";
        public List<string> TextFieldNames = new();
        public string FrameworkId = "";
        public bool IsDialogueNamed;
        public bool IsCustomDialogue;
        public bool IsLauncherLike;
    }

    private sealed class MethodAgg
    {
        public TypeMeta Meta = null!;
        public MethodDefinition Method = null!;
        public string FullName = "";
        public string Key = "";
        public bool IsSemanticNamed;
        public bool HasStringParameter;
        public int LdstrCount;
        public int CjkLiteralCount;
        public int NonDialogueLiteralCount;
        public int MaxLiteralLength;
        public List<SinkSite> SinkSites = new();
    }

    private static MethodRecord BuildRecord(MethodAgg agg, bool discoveredBySink, Dictionary<string, List<string>> callersOf)
    {
        callersOf.TryGetValue(agg.Key, out var callers);
        callers ??= new List<string>();

        return new MethodRecord
        {
            TypeFullName = agg.Meta.FullName,
            TypeName = agg.Meta.Name,
            Namespace = agg.Meta.Namespace,
            MethodName = agg.Method.Name,
            MethodFullName = agg.FullName,
            Signature = Signature(agg.Method),
            ParameterCount = agg.Method.Parameters.Count,
            HasStringParameter = agg.HasStringParameter,
            TextFieldCount = agg.Meta.TextFieldNames.Count,
            TextFieldTypeNames = agg.Meta.TextFieldNames.Distinct(StringComparer.Ordinal).Take(8).ToList(),
            SinkSites = agg.SinkSites,
            LdstrCount = agg.LdstrCount,
            CjkLiteralCount = agg.CjkLiteralCount,
            NonDialogueLiteralCount = agg.NonDialogueLiteralCount,
            MaxLiteralLength = agg.MaxLiteralLength,
            CallerCount = callers.Count,
            CallerSamples = callers.Take(8).ToList(),
            AllCallersInitLike = callers.Count > 0 && callers.All(IsInitLike),
            AnyCallerChoiceLike = callers.Any(IsChoiceLike),
            FrameworkId = agg.Meta.FrameworkId,
            IsDialogueNamed = agg.Meta.IsDialogueNamed,
            IsSemanticNamed = agg.IsSemanticNamed,
            DiscoveredBySink = discoveredBySink,
        };
    }

    private static bool IsInitLike(string callerKey)
    {
        var idx = callerKey.LastIndexOf("::", StringComparison.Ordinal);
        var name = idx >= 0 ? callerKey[(idx + 2)..] : callerKey;
        return InitLikeNames.Any(n => string.Equals(n, name, StringComparison.Ordinal));
    }

    private static bool IsChoiceLike(string callerKey)
    {
        var idx = callerKey.LastIndexOf("::", StringComparison.Ordinal);
        var typePart = idx > 0 ? callerKey[..idx] : "";
        var namePart = idx > 0 ? callerKey[(idx + 2)..] : callerKey;
        return HasWord(typePart, "Choice") || HasWord(typePart, "Select") || HasWord(typePart, "Option")
            || HasWord(namePart, "Choice") || HasWord(namePart, "Select") || HasWord(namePart, "Option");
    }

    private static bool HasWord(string s, string w) => s.Contains(w, StringComparison.OrdinalIgnoreCase);

    private static string EdgeKey(MethodReference m) => m.DeclaringType?.FullName + "::" + m.Name;

    public static string ApiName(MethodReference mr) => $"{mr.DeclaringType?.FullName}::{mr.Name}";

    public static string Signature(MethodDefinition m) =>
        $"{m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))})";

    private static void Increment(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

    private static List<string> SortedFailures(RecordingResolver r) =>
        r.Failures.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public static List<TypeDefinition> FlattenTypes(IEnumerable<TypeDefinition> roots)
    {
        var list = new List<TypeDefinition>();
        var stack = new Stack<TypeDefinition>(roots.Reverse());
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            list.Add(t);
            for (int i = t.NestedTypes.Count - 1; i >= 0; i--) stack.Push(t.NestedTypes[i]);
        }
        list.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        return list;
    }

    /// <summary>S4：字段类型像文本显示组件（不含 TextAsset，不把输入框/下拉框算作显示文本）。</summary>
    public static bool IsTextComponentType(TypeReference tr)
    {
        var n = tr.Name;
        if (tr.FullName is "UnityEngine.UI.Text" or "TMPro.TMP_Text") return true;
        if (n == "TextAsset") return false;
        if (n == "Text") return true;
        if (n.Contains("TextMeshPro", StringComparison.Ordinal)) return true;
        if (n.StartsWith("TMP_", StringComparison.Ordinal) &&
            n is not ("TMP_InputField" or "TMP_Dropdown" or "TMP_FontAsset" or "TMP_SpriteAsset" or "TMP_Settings"))
            return true;
        return false;
    }

    /// <summary>S3：文本写入 API 判定（含基类链：游戏自定义的 TMP 子类也算）。</summary>
    public static bool IsSinkMethod(MethodReference mr)
    {
        var name = mr.Name;
        if (name is not ("set_text" or "SetText")) return false;
        if (name == "SetText" && mr.Parameters.Count == 0) return false;
        return IsTextSinkType(mr.DeclaringType);
    }

    private static readonly Dictionary<string, bool> SinkTypeCache = new(StringComparer.Ordinal);
    private static readonly object SinkCacheLock = new();

    public static bool IsTextSinkType(TypeReference? t)
    {
        if (t == null) return false;
        lock (SinkCacheLock)
            if (SinkTypeCache.TryGetValue(t.FullName, out var cached)) return cached;

        bool ok = IsTextSinkTypeCore(t);
        lock (SinkCacheLock) SinkTypeCache[t.FullName] = ok;
        return ok;
    }

    private static bool IsTextSinkTypeCore(TypeReference t)
    {
        if (t.FullName is "TMPro.TMP_Text" or "UnityEngine.UI.Text") return true;
        if (t.Name is "TMP_Text" or "TextMeshProUGUI" or "TextMeshPro") return true;

        var cur = t;
        for (int i = 0; i < 16; i++)
        {
            TypeDefinition? def;
            try { def = cur.Resolve(); }
            catch { return false; }
            if (def?.BaseType == null) return false;
            var bt = def.BaseType;
            if (bt.FullName is "TMPro.TMP_Text" or "UnityEngine.UI.Text") return true;
            if (bt.Name is "TMP_Text" or "TextMeshProUGUI" or "TextMeshPro") return true;
            if (bt.FullName is "System.Object" or "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject" or "UnityEngine.UI.Graphic")
                return false;
            cur = bt;
        }
        return false;
    }

    /// <summary>用 IL 反向栈分析判定 set_text 实参来源（constant/parameter/field/local/call）。</summary>
    private static SinkSite ClassifySite(Instruction call, MethodReference mr)
    {
        var api = ApiName(mr);
        try
        {
            var (source, constLen) = IlStackAnalyzer.ClassifyArgument(call, 1);
            return new SinkSite { Api = api, Source = source, ConstantLength = constLen };
        }
        catch
        {
            return new SinkSite { Api = api, Source = "unknown", ConstantLength = 0 };
        }
    }

    private static List<FrameworkTrace> BuildFrameworkTraces(
        Dictionary<string, (int types, int methods, List<string> samples)> agg)
    {
        var list = new List<FrameworkTrace>();
        foreach (var kv in agg.OrderByDescending(k => k.Value.types).ThenBy(k => k.Key, StringComparer.Ordinal))
        {
            var isCustom = kv.Key == "custom.dialogue";
            list.Add(new FrameworkTrace
            {
                Id = kv.Key,
                DisplayName = isCustom ? "自定义 *Dialog*/*Talk* 命名" : TextPatterns.FrameworkDisplayName(kv.Key),
                Category = isCustom ? "custom" : "known",
                TypeCount = kv.Value.types,
                MethodCount = kv.Value.methods,
                SampleTypes = kv.Value.samples,
                AffectsScoring = true,
            });
        }
        return list;
    }

    internal static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    internal static string SafeSha256(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>记录解析失败的解析器：解析失败不能中断分析，但要进 warnings。</summary>
    private sealed class RecordingResolver : DefaultAssemblyResolver
    {
        public List<string> Failures { get; } = new();

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            try { return base.Resolve(name); }
            catch (AssemblyResolutionException) { Failures.Add(name.Name); throw; }
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            try { return base.Resolve(name, parameters); }
            catch (AssemblyResolutionException) { Failures.Add(name.Name); throw; }
        }
    }
}
