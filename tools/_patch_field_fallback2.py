# -*- coding: utf-8 -*-
"""v0.2.5 字段兜底朗读：生成侧（SpeechGate/UnityMonoGenerator/README）。"""
import io
import sys


def patch(path, old, new, count=1):
    s = io.open(path, encoding="utf-8").read()
    if s.count(old) < count:
        print("MISS in", path)
        print("OLD:", old[:140])
        sys.exit(1)
    s = s.replace(old, new, count)
    io.open(path, "w", encoding="utf-8", newline="").write(s)
    print("patched", path)


GATE = "src/Forge.Generators/Templates/SpeechGateTemplate.cs"
patch(
    GATE,
    "using UnityEngine;\nusing System.Collections.Generic;\n",
    "using UnityEngine;\nusing System.Collections.Generic;\nusing System.Reflection;\n",
)

patch(
    GATE,
    '''        /// <summary>主朗读出口：同一句在 2 秒内重复出现只念一次。</summary>
        internal static void Say(string? text)''',
    '''        /// <summary>
        /// 字段兜底：有些游戏把台词写在实例字段里（TMP_Text/UI.Text 组件），方法本身没有 string 参数。
        /// 这里按字段名反射取值，拼成一句话再走 Say；读不到就静默，不影响游戏。
        /// </summary>
        internal static void SayFields(object? instance, string[] fieldNames)
        {
            var text = CollectFields(instance, fieldNames);
            if (text is not null)
            {
                Say(text);
            }
        }

        /// <summary>字段兜底的辅助出口：只进重读缓冲，不主动出声。</summary>
        internal static void NoteFields(object? instance, string[] fieldNames)
        {
            var text = CollectFields(instance, fieldNames);
            if (text is not null)
            {
                Note(text);
            }
        }

        private static string? CollectFields(object? instance, string[] fieldNames)
        {
            if (instance is null || fieldNames is null || fieldNames.Length == 0)
            {
                return null;
            }

            var parts = new List<string>(fieldNames.Length);
            foreach (var name in fieldNames)
            {
                try
                {
                    var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var text = ExtractText(field?.GetValue(instance));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text.Trim());
                    }
                }
                catch (System.Exception ex)
                {
                    A11yHost.Diag("[Speech:fields] 读取字段失败 " + name + "：" + ex.GetType().Name);
                }
            }

            if (parts.Count == 0)
            {
                A11yHost.Diag("[Speech:fields] 命中钩子但字段为空，未朗读。");
                return null;
            }

            return string.Join("，", parts);
        }

        /// <summary>从字段值里取文本：string 直接返回；组件取 text 属性（TMP_Text/UI.Text/TextMesh）。</summary>
        private static string? ExtractText(object? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is string direct)
            {
                return direct;
            }

            var property = value.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(value) as string;
        }

        /// <summary>主朗读出口：同一句在 2 秒内重复出现只念一次。</summary>
        internal static void Say(string? text)''',
)

GEN = "src/Forge.Generators/UnityMonoGenerator.cs"
patch(
    GEN,
    '''                ["POSTFIX_SIGNATURE"] = hook.ChoiceList
                    ? "object[] __args, object __instance"
                    : hook.TextParameter is null
                        ? string.Empty
                        : "string " + hook.TextParameter,''',
    '''                ["POSTFIX_SIGNATURE"] = hook.ChoiceList
                    ? "object[] __args, object __instance"
                    : hook.TextParameter is not null
                        ? "string " + hook.TextParameter
                        : hook.InstanceTextFields.Count > 0
                            ? "object __instance"
                            : string.Empty,''',
)

patch(
    GEN,
    '''        if (hook.TextParameter is null)
        {
            return "            A11yHost.Diag(\\"[Hook:" + hook.Point.Id.Trim() +
                   "] 命中 \\" + Target + \\"；该目标没有可提取的文本参数，只能做调用计数。\\");";
        }
''',
    '''        if (hook.TextParameter is null && hook.InstanceTextFields.Count > 0)
        {
            var fields = string.Join(", ", hook.InstanceTextFields.Select(f => "\\"" + f + "\\""));
            var call = hook.AutoSpeak
                ? "SpeechGate.SayFields(__instance, new[] { " + fields + " });"
                : "SpeechGate.NoteFields(__instance, new[] { " + fields + " });";
            return "            A11yHost.Diag(\\"[Hook:" + hook.Point.Id.Trim() +
                   "] 命中 \\" + Target + \\"；按实例文本字段兜底朗读。\\");" + "\\n            " + call;
        }

        if (hook.TextParameter is null)
        {
            return "            A11yHost.Diag(\\"[Hook:" + hook.Point.Id.Trim() +
                   "] 命中 \\" + Target + \\"；该目标没有可提取的文本参数，只能做调用计数。\\");";
        }
''',
)

patch(
    GEN,
    '''            var status = hook.ChoiceList
                ? "已接线（选项列表 → ChoiceReader + 数字键选择）"
                : hook.TextParameter is null
                    ? "已生成骨架（无文本参数，仅计数）"
                    : hook.AutoSpeak''',
    '''            var status = hook.ChoiceList
                ? "已接线（选项列表 → ChoiceReader + 数字键选择）"
                : hook.TextParameter is null && hook.InstanceTextFields.Count > 0
                    ? "已接线（读实例文本字段兜底）"
                    : hook.TextParameter is null
                        ? "已生成骨架（无文本参数，仅计数）"
                        : hook.AutoSpeak''',
)

patch(
    GEN,
    '''                    Status = h.ChoiceList
                        ? "choices"
                        : h.AutoSpeak
                            ? "speaks"
                            : h.TextParameter is not null
                                ? "buffered"
                                : "skeleton_unwired",''',
    '''                    Status = h.ChoiceList
                        ? "choices"
                        : h.AutoSpeak
                            ? "speaks"
                            : h.TextParameter is not null || h.InstanceTextFields.Count > 0
                                ? "buffered"
                                : "skeleton_unwired",''',
)

README = "src/Forge.Generators/Templates/ReadmeTemplate.cs"
patch(
    README,
    '''分析阶段给出的 Harmony 钩子拼在一起。生成器会按分析证据里每个钩子的完整签名，把带
字符串参数的钩子接到朗读出口（主钩子朗读，其余只进重读缓冲）；证据里没有字符串参数的
目标只做命中计数。''',
    '''分析阶段给出的 Harmony 钩子拼在一起。生成器会按分析证据里每个钩子的完整签名，把带
字符串参数的钩子接到朗读出口（主钩子朗读，其余只进重读缓冲）；没有字符串参数、但证据
给出实例文本字段（TMP_Text/UI.Text）的钩子，会反射读字段兜底朗读；两者都没有的目标只做
命中计数。''',
)

patch(README, "| 把游戏台词自动送进朗读 | 分析证据里带字符串参数的钩子已接线；其余只记命中，需人工 |",
      "| 把游戏台词自动送进朗读 | 分析证据里带字符串参数或实例文本字段的钩子已接线；其余只记命中，需人工 |")

patch(README, "| `Hooks/Hook_*.cs` | 每个 Hook 补丁点一个钩子：带字符串参数的按签名接朗读，其余只记命中 |",
      "| `Hooks/Hook_*.cs` | 每个 Hook 补丁点一个钩子：带字符串参数或实例文本字段的按签名/字段接朗读，其余只记命中 |")

patch(README, '''- 自动朗读只覆盖「分析证据里给出字符串参数」的钩子；主朗读钩子固定取 plan 里第一个这样的补丁点。若游戏真正的台词来自别的方法，需要人工按日志补钩子或调整 plan 后重新生成。
- 证据里没有字符串参数的钩子只做命中计数，**不会**把台词送去朗读。''',
      '''- 自动朗读只覆盖「分析证据里给出字符串参数或实例文本字段」的钩子；主朗读钩子固定取 plan 里第一个带字符串参数的钩子（没有则取第一个带实例文本字段的）。若游戏真正的台词来自别的方法或别的字段，需要人工按日志补钩子或调整 plan 后重新生成。
- 证据里既没有字符串参数、也没有实例文本字段的钩子只做命中计数，**不会**把台词送去朗读。''')

print("done")
