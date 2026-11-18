using System.Globalization;
using System.Text;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// R17 静态候选探针（只读 Assembly-CSharp，不写入游戏目录）。
// 输出结构化证据；**不导出任何字符串字面量内容**，只记长度/CJK 标记，
// 避免把游戏台词写进工作区。

internal static class Program
{
    private static readonly string[] TextApiTypes =
    {
        "TMPro.TMP_Text", "TMPro.TextMeshProUGUI", "TMPro.TextMeshPro",
        "UnityEngine.UI.Text", "UnityEngine.TextMesh"
    };

    private static readonly HashSet<string> TextApiMethods = new()
    {
        "set_text", "SetText", "SetCharArray", "set_text_Internal"
    };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: StaticProbe <Assembly-CSharp.dll> <outDir>");
            return 2;
        }
        var asmPath = Path.GetFullPath(args[0]);
        var outDir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(outDir);

        using var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters
        {
            ReadSymbols = false,
            InMemory = true
        });

        var types = asm.MainModule.Types.SelectMany(Flatten).ToList();
        Console.WriteLine($"types={types.Count}");

        // ---------- 类型清单 ----------
        var typeRows = new List<object>();
        foreach (var t in types)
        {
            var textFields = t.Fields.Where(f => IsTextType(f.FieldType)).Select(f => new
            {
                name = f.Name,
                type = f.FieldType.FullName,
                isStatic = f.IsStatic
            }).ToList();
            typeRows.Add(new
            {
                fullName = t.FullName,
                baseType = t.BaseType?.FullName ?? "",
                isMonoBehaviour = InheritsMonoBehaviour(t),
                isCompilerGenerated = t.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute"),
                methodCount = t.Methods.Count(m => m.HasBody),
                textFieldCount = textFields.Count,
                textFields, isPublic = t.IsPublic, isNestedPublic = t.IsNestedPublic,
                fields = t.Fields.Select(f => new { name = f.Name, type = f.FieldType.FullName, isStatic = f.IsStatic }).ToList()
            });
        }

        // ---------- 方法清单 + 调用关系 ----------
        var callerCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var methods = new List<MethodDefinition>();
        foreach (var t in types)
            foreach (var m in t.Methods)
                if (m.HasBody) methods.Add(m);

        foreach (var m in methods)
        {
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode.Code is Code.Call or Code.Callvirt && ins.Operand is MethodReference mr)
                {
                    var key = CalleeKey(mr);
                    callerCount.TryGetValue(key, out var n);
                    callerCount[key] = n + 1;
                }
            }
        }

        var methodRows = new List<object>();
        foreach (var m in methods)
        {
            var calls = new List<string>();
            var textSites = new List<object>();
            int ldstr = 0, cjk = 0;
            foreach (var ins in m.Body.Instructions)
            {
                if (ins.OpCode.Code == Code.Ldstr)
                {
                    ldstr++;
                    if (ins.Operand is string s && ContainsCjk(s)) cjk++;
                }
                if (ins.OpCode.Code is Code.Call or Code.Callvirt && ins.Operand is MethodReference mr)
                {
                    var key = CalleeKey(mr);
                    calls.Add(key);
                    if (IsTextSetter(mr))
                        textSites.Add(DescribeTextSite(m, ins, mr));
                }
            }
            methodRows.Add(new
            {
                type = m.DeclaringType.FullName,
                name = m.Name,
                signature = Signature(m),
                isStatic = m.IsStatic,
                isCoroutineStateMachine = m.DeclaringType.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute"),
                ldstrCount = ldstr,
                cjkLiteralCount = cjk,
                calls = calls.Distinct().ToList(),
                textSites,
                callerCount = callerCount.TryGetValue(CalleeKey(m), out var c) ? c : 0
            });
        }

        var probe = new
        {
            assembly = asmPath,
            assemblySha256 = Sha256(asmPath),
            generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            typeCount = types.Count,
            methodCount = methods.Count,
            textFieldTypes = typeRows.Cast<dynamic>().Where(x => (int)x.textFieldCount > 0).Select(x => (string)x.fullName).ToList(),
            types = typeRows,
            methods = methodRows
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Path.Combine(outDir, "cecil-probe.json"), JsonSerializer.Serialize(probe, opts), new UTF8Encoding(false));

        // ---------- 人读摘要：UIText 与对话相关类型的方法 + 调用 ----------
        var sb = new StringBuilder();
        sb.AppendLine("# UIText / Talk / Dialog 类型方法摘要（无字符串内容）");
        sb.AppendLine();
        foreach (var t in types.Where(t =>
                     t.Name == "UIText" ||
                     t.Name.Contains("Talk", StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Contains("Dialog", StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Contains("Choice", StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Contains("Message", StringComparison.OrdinalIgnoreCase) ||
                     t.Name.Contains("Text", StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.FullName))
        {
            sb.AppendLine($"## {t.FullName}  (base={t.BaseType?.FullName})");
            foreach (var f in t.Fields)
            {
                if (IsTextType(f.FieldType))
                    sb.AppendLine($"  field: {f.FieldType.Name} {f.Name}");
            }
            foreach (var m in t.Methods.Where(x => x.HasBody))
            {
                var callNames = new List<string>();
                int ldstr = 0;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode.Code == Code.Ldstr) ldstr++;
                    if (ins.OpCode.Code is Code.Call or Code.Callvirt && ins.Operand is MethodReference mr)
                    {
                        if (IsTextSetter(mr) || mr.Name.Contains("DOText") || mr.Name.Contains("Talk") ||
                            mr.Name.Contains("Text", StringComparison.OrdinalIgnoreCase) || mr.Name.Contains("Choice"))
                            callNames.Add(CalleeKey(mr));
                    }
                }
                if (callNames.Count == 0 && ldstr == 0) continue;
                sb.AppendLine($"  {Signature(m)}  [ldstr={ldstr}]");
                if (callNames.Count > 0)
                    sb.AppendLine($"      -> {string.Join(", ", callNames.Distinct())}");
            }
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(outDir, "uitext-methods.md"), sb.ToString(), new UTF8Encoding(false));

        // ---------- 关键方法 IL 观测（ldstr 只记长度，不记内容） ----------
        var watch = new HashSet<string> { "AnimateText", "DOText", "HandleMainText", "HandleMessageText",
            "Choice", "StartSpeak", "PushSkip", "TalkNext1D", "ReDisplayTalkWindows", "EndMessage" };
        var ib = new StringBuilder();
        foreach (var m in methods.Where(x => x.DeclaringType.FullName == "UIText" && watch.Contains(x.Name)))
        {
            ib.AppendLine($"## {Signature(m)}");
            foreach (var ins in m.Body.Instructions)
            {
                string op = ins.OpCode.Name;
                string operand = "";
                switch (ins.Operand)
                {
                    case null: break;
                    case string s:
                        operand = $" (len={s.Length}, cjk={ContainsCjk(s)})";
                        break;
                    case MethodReference mr:
                        operand = " " + CalleeKey(mr);
                        break;
                    case FieldReference fr:
                        operand = " " + fr.DeclaringType.Name + "::" + fr.Name;
                        break;
                    case ParameterDefinition pd:
                        operand = " " + pd.Name;
                        break;
                    case VariableDefinition vd:
                        operand = " V_" + vd.Index;
                        break;
                    default:
                        operand = " " + ins.Operand;
                        break;
                }
                ib.AppendLine($"    IL_{ins.Offset:x4}: {op}{operand}");
            }
            ib.AppendLine();
        }
        File.WriteAllText(Path.Combine(outDir, "il-watch.txt"), ib.ToString(), new UTF8Encoding(false));

        Console.WriteLine($"wrote {outDir}");
        return 0;
    }

    private static object DescribeTextSite(MethodDefinition caller, Instruction ins, MethodReference mr)
    {
        var arg = PrevNonNop(ins);
        string kind = "unknown", name = "", text = "";
        if (arg != null)
        {
            switch (arg.OpCode.Code)
            {
                case Code.Ldstr:
                    kind = "constant";
                    text = arg.Operand as string ?? "";
                    break;
                case Code.Ldarg:
                case Code.Ldarg_S:
                case Code.Ldarg_0:
                case Code.Ldarg_1:
                case Code.Ldarg_2:
                case Code.Ldarg_3:
                    kind = "parameter";
                    name = ArgName(caller, arg);
                    break;
                case Code.Ldfld:
                    kind = "field";
                    name = (arg.Operand as FieldReference)?.Name ?? "";
                    break;
                case Code.Ldsfld:
                    kind = "static-field";
                    name = (arg.Operand as FieldReference)?.Name ?? "";
                    break;
                case Code.Ldloc:
                case Code.Ldloc_S:
                case Code.Ldloc_0:
                case Code.Ldloc_1:
                case Code.Ldloc_2:
                case Code.Ldloc_3:
                    kind = "local";
                    name = arg.Operand is VariableDefinition v ? $"V_{v.Index}" : arg.OpCode.Name;
                    break;
                case Code.Call:
                case Code.Callvirt:
                    kind = "call";
                    name = (arg.Operand as MethodReference) is { } m ? CalleeKey(m) : "";
                    break;
            }
        }

        // 目标 UI 组件字段：在调用点之前找 ldfld/ldsfld 且字段类型是 Text/TMP
        string target = "";
        var cursor = ins.Previous;
        for (int i = 0; i < 8 && cursor != null; i++, cursor = cursor.Previous)
        {
            if (cursor.OpCode.Code is Code.Ldfld or Code.Ldsfld && cursor.Operand is FieldReference fr && IsTextType(fr.FieldType))
            {
                target = fr.Name;
                break;
            }
        }

        return new
        {
            caller = CalleeKey(caller),
            api = CalleeKey(mr),
            argKind = kind,
            argName = name,
            constantLength = kind == "constant" ? text.Length : 0,
            constantHasCjk = kind == "constant" && ContainsCjk(text),
            targetField = target
        };
    }

    private static string ArgName(MethodDefinition m, Instruction arg)
    {
        int idx = arg.OpCode.Code switch
        {
            Code.Ldarg_0 => 0,
            Code.Ldarg_1 => 1,
            Code.Ldarg_2 => 2,
            Code.Ldarg_3 => 3,
            _ => arg.Operand is ParameterDefinition p ? p.Index + (m.IsStatic ? 0 : 1) : -1
        };
        if (!m.IsStatic)
        {
            if (idx == 0) return "this";
            idx--;
        }
        return idx >= 0 && idx < m.Parameters.Count ? m.Parameters[idx].Name : $"arg{idx}";
    }

    private static Instruction PrevNonNop(Instruction ins)
    {
        var p = ins.Previous;
        while (p != null && p.OpCode.Code == Code.Nop) p = p.Previous;
        return p;
    }

    private static string CalleeKey(MethodReference mr) => $"{mr.DeclaringType.FullName}::{mr.Name}";

    private static string Signature(MethodDefinition m)
    {
        var ps = string.Join(", ", m.Parameters.Select(p => $"{Simple(p.ParameterType)} {p.Name}"));
        return $"{Simple(m.ReturnType)} {m.Name}({ps})";
    }

    private static string Simple(TypeReference t)
    {
        if (t is GenericInstanceType g)
            return $"{g.ElementType.Name}<{string.Join(",", g.GenericArguments.Select(Simple))}>";
        return t.Name;
    }

    private static bool IsTextSetter(MethodReference mr)
    {
        if (!TextApiMethods.Contains(mr.Name)) return false;
        return IsTextType(mr.DeclaringType);
    }

    private static bool IsTextType(TypeReference t)
    {
        var cur = t;
        int guard = 0;
        while (cur != null && guard++ < 8)
        {
            var n = cur.FullName;
            if (TextApiTypes.Contains(n) || n.StartsWith("TMPro.TMP_Text", StringComparison.Ordinal) ||
                n.StartsWith("TMPro.TextMeshPro", StringComparison.Ordinal)) return true;
            try { cur = cur.Resolve()?.BaseType; } catch { break; }
        }
        return false;
    }

    private static bool InheritsMonoBehaviour(TypeDefinition t)
    {
        var cur = t.BaseType;
        int guard = 0;
        while (cur != null && guard++ < 12)
        {
            if (cur.FullName == "UnityEngine.MonoBehaviour") return true;
            try { cur = cur.Resolve()?.BaseType; } catch { break; }
        }
        return false;
    }

    private static bool ContainsCjk(string s)
    {
        foreach (var ch in s)
        {
            if ((ch >= 0x3040 && ch <= 0x30FF) || (ch >= 0x3400 && ch <= 0x9FFF) ||
                (ch >= 0xF900 && ch <= 0xFAFF) || (ch >= 0xFF66 && ch <= 0xFF9F))
                return true;
        }
        return false;
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in Flatten(n))
                yield return x;
    }

    private static string Sha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}


