// 本文件由 GalA11y Forge（无障碍补丁工坊）生成，生成器版本 0.2.85.0。
// 补丁点：dialogue-set-text
// 目标：GameNS.ADV.Dialogue::SetText（来自 plan.json，未凭空编造）
// 候选：candidate-1（对应分析阶段 textCandidate.id）
using System.Reflection;
using HarmonyLib;

namespace GalA11y.ForgeGolden.Hooks
{
    /// <summary>
    /// 补丁点 dialogue-set-text：确认目标在当前游戏程序集里存在，并在每帧调用时把文本交给朗读出口。
    /// 文本参数来自分析证据里的完整签名；本类不读游戏状态，也不包含任何台词。
    /// </summary>
    internal static class Hook_dialogue_set_text
    {
        internal const string Target = "GameNS.ADV.Dialogue:SetText";

        internal static MethodBase Resolve()
        {
            return MethodResolver.Resolve(Target, new string[] { "System.String" });
        }

        private static void Postfix(string text)
        {
            SpeechGate.Say(text);
        }
    }
}