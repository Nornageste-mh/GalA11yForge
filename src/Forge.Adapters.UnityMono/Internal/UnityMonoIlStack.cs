using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GalA11yForge.Adapters.UnityMono.Internal;

/// <summary>
/// 极简 IL 反向栈分析：判断某条调用指令的第 N 个实参由什么压栈。
/// 只做一层（不跨基本块、不追局部变量定义），用于判断文本写入的是常量还是变量。
/// 来源：src/experiments/unity-mono-analyzer/src/Analysis/IlStackAnalyzer.cs（W2-H 复制并改名，逻辑未改）。
/// </summary>
internal static class UnityMonoIlStack
{
    private const int MaxWalk = 200;

    /// <summary>argIndex 从 1 开始（0 表示 this）。返回 (来源, 常量长度)。</summary>
    public static (string Source, int ConstantLength) ClassifyArgument(Instruction call, int argIndex)
    {
        if (call.Operand is not MethodReference md)
        {
            return ("unknown", 0);
        }

        var paramCount = md.Parameters.Count;
        if (paramCount == 0)
        {
            return ("unknown", 0);
        }

        var depth = paramCount - argIndex + 1;
        if (depth < 1)
        {
            depth = 1;
        }

        var instruction = call.Previous;
        var need = depth;
        var guard = 0;
        while (instruction is not null && guard++ < MaxWalk)
        {
            if (IsTransparent(instruction))
            {
                instruction = instruction.Previous;
                continue;
            }

            var pushes = PushCount(instruction);
            var pops = PopCount(instruction);
            if (pushes >= need)
            {
                return ClassifyProducer(instruction);
            }

            need = need - pushes + pops;
            instruction = instruction.Previous;
        }

        return ("unknown", 0);
    }

    private static bool IsTransparent(Instruction instruction) => instruction.OpCode.Code is
        Code.Nop or Code.Break or Code.Castclass or Code.Box or Code.Unbox or Code.Unbox_Any
        or Code.Constrained or Code.Readonly or Code.Volatile or Code.Tail or Code.Unaligned;

    private static (string Source, int ConstantLength) ClassifyProducer(Instruction instruction) =>
        instruction.OpCode.Code switch
        {
            Code.Ldstr => ("constant", (instruction.Operand as string)?.Length ?? 0),
            Code.Ldnull => ("constant", 0),
            Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3 or Code.Ldarg_S => ("parameter", 0),
            Code.Ldfld or Code.Ldsfld or Code.Ldflda or Code.Ldsflda => ("field", 0),
            Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 or Code.Ldloc_S or Code.Ldloca_S =>
                ("local", 0),
            Code.Call or Code.Callvirt or Code.Newobj => ("call", 0),
            Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3
                or Code.Ldc_I4_4 or Code.Ldc_I4_5 or Code.Ldc_I4_6 or Code.Ldc_I4_7 or Code.Ldc_I4_8
                or Code.Ldc_I4_M1 or Code.Ldc_I8 or Code.Ldc_R4 or Code.Ldc_R8 => ("constant", 0),
            _ => ("other", 0),
        };

    private static int PushCount(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
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

    private static int PopCount(Instruction instruction) => instruction.OpCode.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 => 1,
        StackBehaviour.Pop1_pop1 => 2,
        StackBehaviour.Popi => 1,
        StackBehaviour.Popi_popi => 2,
        StackBehaviour.Popi_popi8 => 2,
        StackBehaviour.Popi_popi_popi => 3,
        StackBehaviour.Popi_popr4 => 2,
        StackBehaviour.Popi_popr8 => 2,
        StackBehaviour.Popref => 1,
        StackBehaviour.Popref_pop1 => 2,
        StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popref_popi_popi => 3,
        StackBehaviour.Popref_popi_popi8 => 3,
        StackBehaviour.Popref_popi_popr4 => 3,
        StackBehaviour.Popref_popi_popr8 => 3,
        StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.PopAll => 1,
        StackBehaviour.Varpop when instruction.Operand is MethodReference md => VarpopCount(md, instruction),
        StackBehaviour.Varpop => 1,
        _ => 0,
    };

    private static int VarpopCount(MethodReference md, Instruction instruction)
    {
        var count = md.Parameters.Count;
        if (instruction.OpCode.Code != Code.Newobj)
        {
            count += md.HasThis ? 1 : 0;
        }

        return count;
    }
}
