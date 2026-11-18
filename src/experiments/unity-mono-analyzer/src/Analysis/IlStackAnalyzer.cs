using Mono.Cecil;
using Mono.Cecil.Cil;

namespace UnityMonoAnalyzer.Analysis;

/// <summary>
/// 极简 IL 反向栈分析：判断某条调用指令的第 N 个实参是由什么压栈的。
/// 只做一层（不跨基本块、不追局部变量定义），够用于"这个 set_text 传的是常量还是变量"。
/// </summary>
public static class IlStackAnalyzer
{
    private const int MaxWalk = 200;

    /// <summary>argIndex 从 1 开始（0 表示 this）。返回 (来源, 常量长度)。</summary>
    public static (string Source, int ConstantLength) ClassifyArgument(Instruction call, int argIndex)
    {
        if (call.Operand is not MethodReference md) return ("unknown", 0);
        int paramCount = md.Parameters.Count;
        if (paramCount == 0) return ("unknown", 0);
        int depth = paramCount - argIndex + 1;
        if (depth < 1) depth = 1;

        var ins = call.Previous;
        int need = depth, guard = 0;
        while (ins != null && guard++ < MaxWalk)
        {
            if (IsTransparent(ins)) { ins = ins.Previous; continue; }
            int pushes = PushCount(ins);
            int pops = PopCount(ins);
            if (pushes >= need) return ClassifyProducer(ins);
            need = need - pushes + pops;
            ins = ins.Previous;
        }
        return ("unknown", 0);
    }

    private static bool IsTransparent(Instruction ins) => ins.OpCode.Code is
        Code.Nop or Code.Break or Code.Castclass or Code.Box or Code.Unbox or Code.Unbox_Any
        or Code.Constrained or Code.Readonly or Code.Volatile or Code.Tail or Code.Unaligned;

    private static (string Source, int ConstantLength) ClassifyProducer(Instruction ins) => ins.OpCode.Code switch
    {
        Code.Ldstr => ("constant", (ins.Operand as string)?.Length ?? 0),
        Code.Ldnull => ("constant", 0),
        Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3 or Code.Ldarg_S => ("parameter", 0),
        Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda => ("field", 0),
        Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 or Code.Ldloc_S or Code.Ldloca_S => ("local", 0),
        Code.Call or Code.Callvirt or Code.Newobj => ("call", 0),
        Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3
            or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8
            or Code.Ldc_I4_M1 or Code.Ldc_I8 or Code.Ldc_R4 or Code.Ldc_R8 => ("constant", 0),
        _ => ("other", 0),
    };

    private static int PushCount(Instruction ins) => ins.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Push1 => 1,
        StackBehaviour.Pushi => 1,
        StackBehaviour.Pushi8 => 1,
        StackBehaviour.Pushr4 => 1,
        StackBehaviour.Pushr8 => 1,
        StackBehaviour.Pushref => 1,
        StackBehaviour.Varpush => 1,
        _ => 1,
    };

    private static int PopCount(Instruction ins)
    {
        switch (ins.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: return 1;
            case StackBehaviour.Pop1_pop1: return 2;
            case StackBehaviour.Popi: return 1;
            case StackBehaviour.Popi_popi: return 2;
            case StackBehaviour.Popi_popi8: return 2;
            case StackBehaviour.Popi_popi_popi: return 3;
            case StackBehaviour.Popi_popr4: return 2;
            case StackBehaviour.Popi_popr8: return 2;
            case StackBehaviour.Popref: return 1;
            case StackBehaviour.Popref_pop1: return 2;
            case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popref_popi_popi: return 3;
            case StackBehaviour.Popref_popi_popi8: return 3;
            case StackBehaviour.Popref_popi_popr4: return 3;
            case StackBehaviour.Popref_popi_popr8: return 3;
            case StackBehaviour.Popref_popi_popref: return 3;
            case StackBehaviour.PopAll: return 1;
            case StackBehaviour.Varpop:
                if (ins.Operand is MethodReference md)
                {
                    int n = md.Parameters.Count;
                    if (ins.OpCode.Code != Code.Newobj) n += md.HasThis ? 1 : 0;
                    return n;
                }
                return 1;
            default: return 0;
        }
    }
}
