namespace GalA11yForge.Generators.Templates;

/// <summary>
/// 单个 Harmony 钩子。目标来自 plan.json 的分析证据；如果证据里的完整签名带字符串参数，
/// 生成器就按签名接一条朗读线（主钩子朗读、其它钩子只进重读缓冲），否则只做命中计数。
/// 不猜游戏逻辑、不硬编码台词、不引用 Assembly-CSharp。
/// </summary>
internal static class HookTemplate
{
    internal const string Text = """
// 本文件由 GalA11y Forge（无障碍补丁工坊）生成，生成器版本 {{GENERATOR_VERSION}}。
// 补丁点：{{POINT_ID}}
// 目标：{{TARGET_DISPLAY}}（来自 plan.json，未凭空编造）
{{CANDIDATE_COMMENT}}using System.Reflection;
using HarmonyLib;

namespace {{NS}}.Hooks
{
    /// <summary>
    /// 补丁点 {{POINT_ID}}：确认目标在当前游戏程序集里存在，并在每帧调用时把文本交给朗读出口。
    /// 文本参数来自分析证据里的完整签名；本类不读游戏状态，也不包含任何台词。
    /// </summary>
    internal static class Hook_{{CLASS}}
    {
        internal const string Target = "{{TARGET_CS}}";

        internal static MethodBase Resolve()
        {
            return {{RESOLVE_EXPRESSION}};
        }

        private static void Postfix({{POSTFIX_SIGNATURE}})
        {
{{POSTFIX_BODY}}
        }
    }
}
""";
}
