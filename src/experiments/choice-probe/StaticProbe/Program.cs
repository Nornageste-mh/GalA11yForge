using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

// task-28 / R23 静态探针：只读 Assembly-CSharp，不写游戏目录。
// 目标：导出 UIText::Choice 及其编译器生成局部函数（Q/Loc）的完整 IL，
// 弄清选项文本到底是 ChoiceAsset.ChoiceN.Sc 原样，还是经过 Loc() 本地化/替换。
// 字符串字面量只记长度与是否含 CJK，不落原文（版权与隐私红线）。

internal static class Program
{
    private static readonly string[] Roots = { "UIText", "ChoiceAsset" };

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ChoiceStaticProbe <Assembly-CSharp.dll> <outTxt>");
            return 2;
        }
        var asmPath = Path.GetFullPath(args[0]);
        var outPath = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        using var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { ReadSymbols = false, InMemory = true });
        var types = asm.MainModule.Types.SelectMany(Flatten).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("# R23 choice static probe");
        sb.AppendLine($"asm={asmPath}");
        sb.AppendLine($"types={types.Count}");
        sb.AppendLine();

        foreach (var name in Roots)
        {
            var t = types.FirstOrDefault(x => x.FullName == name);
            sb.AppendLine($"## TYPE {name} exists={(t != null)}");
            if (t == null) continue;
            sb.AppendLine($"base={t.BaseType?.FullName}");
            foreach (var f in t.Fields)
                sb.AppendLine($"  field {f.Name} : {f.FieldType.FullName} (public={f.IsPublic}, static={f.IsStatic})");
            foreach (var p in t.Properties)
                sb.AppendLine($"  prop {p.Name} : {p.PropertyType.FullName} get={p.GetMethod?.Name} set={p.SetMethod?.Name}");
            foreach (var nt in t.NestedTypes)
                sb.AppendLine($"  nested {nt.FullName} (public={nt.IsNestedPublic}) fields={string.Join(",", nt.Fields.Select(f => f.Name + ":" + f.FieldType.Name))}");
            sb.AppendLine();
        }

        // 1) UIText::Choice 本体
        var choice = types.Where(t => t.FullName == "UIText")
                          .SelectMany(t => t.Methods)
                          .FirstOrDefault(m => m.Name == "Choice");
        if (choice != null)
        {
            DumpMethod(sb, choice);
        }
        else
        {
            sb.AppendLine("## UIText::Choice NOT FOUND");
        }

        // 2) 编译器生成的局部函数（Q / Loc）以及协程 ChoiceCo
        var generated = types
            .SelectMany(t => t.Methods.Select(m => (type: t, method: m)))
            .Where(x => x.method.Name.Contains("<Choice>") || x.method.Name == "ChoiceCo" ||
                        (x.method.Name.StartsWith("g__") && x.method.Name.Contains("Choice")))
            .OrderBy(x => x.method.Name, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine($"## GENERATED CHOICE METHODS count={generated.Count}");
        foreach (var (type, method) in generated)
        {
            sb.AppendLine($"  {type.FullName}::{method.Name}");
        }
        sb.AppendLine();

        foreach (var (type, method) in generated)
        {
            DumpMethod(sb, method);
        }

        // 3) 按钮推进方法（选择映射/状态复位）
        foreach (var name in new[] { "PushButton1", "PushButton2", "PushButton3", "PushButton1D", "PushButton2D", "PushButton3D" })
        {
            var m = types.Where(t => t.FullName == "UIText").SelectMany(t => t.Methods).FirstOrDefault(x => x.Name == name);
            if (m != null) DumpMethod(sb, m);
        }


        // 4) 谁调用 PushButton1/2/3（选择链路的入口）
        sb.AppendLine("## CALLERS of UIText::PushButton1/2/3");
        foreach (var t in types)
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is MethodReference mr && mr.DeclaringType?.Name == "UIText" &&
                        (mr.Name == "PushButton1" || mr.Name == "PushButton2" || mr.Name == "PushButton3"))
                    {
                        sb.AppendLine($"  {t.FullName}::{m.Name} -> {mr.Name} @IL_{ins.Offset:x4}");
                    }
                }
            }
        }
        sb.AppendLine();

        // 5) VirtualCursorManager 全貌
        var vcm = types.FirstOrDefault(x => x.FullName == "VirtualCursorManager");
        sb.AppendLine($"## TYPE VirtualCursorManager exists={(vcm != null)}");
        if (vcm != null)
        {
            sb.AppendLine($"base={vcm.BaseType?.FullName}");
            foreach (var f in vcm.Fields) sb.AppendLine($"  field {f.Name} : {f.FieldType.FullName} public={f.IsPublic}");
            foreach (var m in vcm.Methods) sb.AppendLine($"  method {Sig(m)} public={m.IsPublic} static={m.IsStatic}");
        }
        sb.AppendLine();

        foreach (var name in new[] { "BeginChoiceMode", "EndChoiceMode" })
        {
            var m = vcm == null ? null : vcm.Methods.FirstOrDefault(x => x.Name == name);
            if (m != null && m.HasBody) { DumpMethod(sb, m); }
        }

        // 6) choice 模式下的输入/光标细节
        if (vcm != null)
        {
            foreach (var name in new[] { "Update", "SetMode", "HideCursorAndDisablePointer", "RaycastByVirtualCursor", "DisplayCursor", "UnDisplayCursor", "UpdateSwitch", "ChangedNavigationMode" })
            {
                var m = vcm.Methods.FirstOrDefault(x => x.Name == name);
                if (m != null && m.HasBody) DumpMethod(sb, m);
            }
            foreach (var nested in vcm.NestedTypes)
            {
                sb.AppendLine($"## NESTED {nested.FullName}");
                if (nested.IsEnum)
                {
                    foreach (var f in nested.Fields.Where(f => f.IsStatic && f.HasConstant))
                        sb.AppendLine($"  enum {f.Name} = {f.Constant}");
                }
            }
        }
        // 7) 选项按钮的 Selectable.navigation / 事件绑定线索：UIText 中所有 Button/Image 相关字段已在 SCHEMA

        // 8) ChoiceCo 状态机（选项出现后的时序/选择初始化）
        var uiT = types.FirstOrDefault(x => x.FullName == "UIText");
        if (uiT != null)
        {
            foreach (var nt in uiT.NestedTypes)
            {
                sb.AppendLine($"## NESTED UIText/{nt.Name}");
                foreach (var f in nt.Fields) sb.AppendLine($"  field {f.Name} : {f.FieldType.FullName}");
                var mv = nt.Methods.FirstOrDefault(x => x.Name == "MoveNext");
                if (mv != null && mv.HasBody) DumpMethod(sb, mv);
            }
        }
        // 9) 自定义虚拟鼠标 + UIText.Update：choice 期间到底谁读输入
        foreach (var name in new[] { "UnityEngine.InputSystem.UI.MyVirtualMouseInput", "UIText", "VirtualCursorManager" })
        {
            var t3 = types.FirstOrDefault(x => x.FullName == name);
            if (t3 == null) { sb.AppendLine($"## EXTRA {name} exists=False"); continue; }
            sb.AppendLine($"## EXTRA TYPE {name} fields={t3.Fields.Count} methods={t3.Methods.Count}");
            foreach (var m in t3.Methods)
            {
                if (!m.HasBody) continue;
                if (name == "UIText" && !(m.Name == "Update" || m.Name.Contains("Selection") || m.Name == "Start" || m.Name == "Awake" || m.Name == "SetNavifromStick" || m.Name.Contains("Navigate"))) continue;
                if (name == "VirtualCursorManager" && !(m.Name == "Start" || m.Name == "UpdateWindows" || m.Name == "RaycastByVirtualCursor" || m.Name.Contains("PointerInput"))) continue;
                DumpMethod(sb, m);
            }
        }

        sb.AppendLine("## TYPES matching Choice|Cursor|Option|Select");
        foreach (var t2 in types.Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Name, "Choice|Cursor|Option|Select")))
            sb.AppendLine($"  {t2.FullName} : {t2.BaseType?.Name}");
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"wrote {outPath} ({sb.Length} chars)");
        return 0;
    }

    private static void DumpMethod(StringBuilder sb, MethodDefinition m)
    {
        sb.AppendLine($"## {m.DeclaringType.FullName}::{m.Name}");
        sb.AppendLine($"   signature={Sig(m)}");
        sb.AppendLine($"   attrs={m.Attributes} impl={m.ImplAttributes} static={m.IsStatic} public={m.IsPublic}");
        if (!m.HasBody)
        {
            sb.AppendLine("   (no body)");
            sb.AppendLine();
            return;
        }
        sb.AppendLine("   locals: " + string.Join(", ", m.Body.Variables.Select(v => $"V_{v.Index}:{v.VariableType.Name}")));
        foreach (var ins in m.Body.Instructions)
        {
            string operand;
            switch (ins.Operand)
            {
                case null: operand = ""; break;
                case string s: operand = $" \"len={s.Length},cjk={HasCjk(s)},escapes={HasEscape(s)}\""; break;
                case MethodReference mr: operand = " " + CalleeKey(mr); break;
                case FieldReference fr: operand = " " + fr.DeclaringType.Name + "::" + fr.Name; break;
                case ParameterDefinition pd: operand = " " + pd.Name + ":" + pd.ParameterType.Name; break;
                case VariableDefinition vd: operand = " V_" + vd.Index; break;
                case Instruction it: operand = " -> IL_" + it.Offset.ToString("x4"); break;
                case Instruction[] arr: operand = " [" + string.Join(",", arr.Select(a => "IL_" + a.Offset.ToString("x4"))) + "]"; break;
                default: operand = " " + ins.Operand; break;
            }
            sb.AppendLine($"   IL_{ins.Offset:x4}: {ins.OpCode.Name}{operand}");
        }
        sb.AppendLine();
    }

    private static string Sig(MethodDefinition m) =>
        (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" +
        string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name)) + ")";

    private static string CalleeKey(MethodReference mr)
    {
        var dt = mr.DeclaringType?.Name ?? "?";
        return dt + "::" + mr.Name + "(" + string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name)) + ")";
    }

    private static bool HasCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    private static bool HasEscape(string s) => s.Contains('{') || s.Contains('}') || s.Contains('%');

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t)
    {
        yield return t;
        foreach (var n in t.NestedTypes)
            foreach (var x in Flatten(n)) yield return x;
    }
}



