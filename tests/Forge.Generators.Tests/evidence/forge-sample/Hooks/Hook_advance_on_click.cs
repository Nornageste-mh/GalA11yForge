// 本文件由 Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo）生成，生成器版本 0.2.86.0。
// 补丁点：advance-on-click
// 目标：GameNS.ADV.Advance::OnClick(string)（来自 plan.json，未凭空编造）
// 候选：candidate-2（对应分析阶段 textCandidate.id）
using System.Reflection;
using HarmonyLib;

namespace GalA11y.ForgeGolden.Hooks
{
    /// <summary>
    /// 补丁点 advance-on-click：确认目标在当前游戏程序集里存在，并在每帧调用时把文本交给朗读出口。
    /// 文本参数来自分析证据里的完整签名；本类不读游戏状态，也不包含任何台词。
    /// </summary>
    internal static class Hook_advance_on_click
    {
        internal const string Target = "GameNS.ADV.Advance:OnClick";

        internal static MethodBase Resolve()
        {
            return AccessTools.Method(Target, new[] { typeof(string) });
        }

        private static void Postfix()
        {
            A11yHost.Diag("[Hook:advance-on-click] 命中 " + Target + "；该目标没有可提取的文本参数，只能做调用计数。");
        }
    }
}