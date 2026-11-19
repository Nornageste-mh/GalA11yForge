namespace GalA11yForge.Generators.Templates;

/// <summary>
/// 选项朗读与选择通道（只在 plan 里出现「选项列表」钩子时生成）。
/// 背景（R23 实测）：选项界面会主动禁用鼠标、提交键只绑手柄，键鼠玩家无法选择；
/// 选项文本在 ChoiceAsset.Choice1..3（QuadLang）里，同时被写入 UIText._textB1..3。
/// 因此补丁必须自己朗读选项，并用数字键调用 ButtonN.onClick.Invoke() 完成选择。
/// </summary>
internal static class ChoiceReaderTemplate
{
    internal const string Text = """
// 本文件由 Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo）生成，生成器版本 {{GENERATOR_VERSION}}。
// 作用：朗读选项列表，并提供数字键选择通道。
// 依据：research/23-choice（运行期实测）——选项期间游戏禁用鼠标，键鼠必须由补丁代劳。
// 本文件不引用 Assembly-CSharp，全部字段通过反射读取（找不到只警告，不抛异常）。
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace {{NS}}
{
    /// <summary>选项朗读 + 数字键选择。文本来源优先画面上的 _textB1..3，其次 ChoiceN.Sc。</summary>
    internal static class ChoiceReader
    {
        private const int MaxChoices = 3;
        private const string ChoiceTag = "choice";

        private static bool _active;
        private static object? _instance;
        private static int _count;
        private static int _hideMask;
        private static readonly string[] _texts = new string[MaxChoices + 1];

        /// <summary>钩子入口：在 UIText::Choice 之后调用。</summary>
        internal static void OnChoice(object[]? args, object? instance)
        {
            A11yHost.Diag("[Hook:choice] OnChoice 被调用（选项处理入口已命中）。");
            _active = false;
            _instance = instance;
            _count = 0;
            _hideMask = 0;
            Array.Clear(_texts, 0, _texts.Length);

            if (args == null || args.Length == 0 || args[0] == null)
            {
                A11yHost.Diag("[Choice] 钩子没有 ChoiceAsset 参数，无法读取选项。");
                return;
            }

            var asset = args[0];
            if (args.Length > 1 && args[1] is int mask)
            {
                _hideMask = mask;
            }

            _count = ReadCount(asset, instance);

            var parts = new List<string>();
            for (var i = 1; i <= _count && i <= MaxChoices; i++)
            {
                if ((_hideMask & (1 << (i - 1))) != 0)
                {
                    continue;
                }

                var text = ReadText(instance, asset, i);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                _texts[i] = text;
                parts.Add("选项" + i + "：" + TrimOption(text));
            }

            if (parts.Count == 0)
            {
                A11yHost.Diag("[Choice] 选项文本读取失败（字段名可能不同，请按报告的人工待办检查）。");
                Repeat.Push("有选项出现，但补丁读不到选项文本。按数字键仍可尝试选择。", ChoiceTag);
                _active = _count > 0;
                return;
            }

            var message = "有 " + parts.Count + " 个选项。" + string.Join("；", parts.ToArray()) + "。按数字键选择。";
            A11yHost.Diag("[Choice] " + message);
            Repeat.Push(message, ChoiceTag);
            _active = true;
        }

        /// <summary>每帧轮询数字键；只有选项激活时才处理，不影响其它操作。</summary>
        internal static void Update()
        {
            if (!_active || _instance == null)
            {
                return;
            }

            for (var i = 1; i <= _count && i <= MaxChoices; i++)
            {
                if ((_hideMask & (1 << (i - 1))) != 0)
                {
                    continue;
                }

                if (!WasNumberPressed(i))
                {
                    continue;
                }

                Select(i);
                return;
            }
        }

        private static int ReadCount(object asset, object? instance)
        {
            var value = ReadMember(asset, "ChoiceCount");
            if (value is int count && count > 0)
            {
                return Mathf.Clamp(count, 1, MaxChoices);
            }

            var max = 0;
            for (var i = 1; i <= MaxChoices; i++)
            {
                if (!string.IsNullOrEmpty(ReadText(instance, asset, i)))
                {
                    max = i;
                }
            }

            return max == 0 ? MaxChoices : max;
        }

        private static string ReadText(object? instance, object asset, int index)
        {
            if (instance != null)
            {
                var field = ReadMember(instance, "_textB" + index);
                var fromField = ReadTextValue(field);
                if (!string.IsNullOrEmpty(fromField))
                {
                    return fromField;
                }
            }

            var quad = ReadMember(asset, "Choice" + index);
            if (quad == null)
            {
                return string.Empty;
            }

            var sc = ReadMember(quad, "Sc") as string;
            if (!string.IsNullOrEmpty(sc))
            {
                return sc!.Trim();
            }

            var ja = ReadMember(quad, "Ja") as string;
            return string.IsNullOrEmpty(ja) ? string.Empty : ja!.Trim();
        }

        private static string ReadTextValue(object? value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var text = ReadMember(value, "text") as string;
            return string.IsNullOrEmpty(text) ? string.Empty : text!.Trim();
        }

        /// <summary>选项文本通常自带句末标点，拼进列表时去掉，避免出现「？」「。」连写。</summary>
        private static string TrimOption(string text)
        {
            return text.TrimEnd('。', '？', '！', '…', '，', ',', '.', '?', '!', ' ', '\u3000');
        }

        private static object? ReadMember(object instance, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = instance.GetType();

            try
            {
                var field = type.GetField(name, flags);
                if (field != null)
                {
                    return field.GetValue(instance);
                }

                var property = type.GetProperty(name, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(instance, null);
                }
            }
            catch (Exception ex)
            {
                A11yHost.Diag("[Choice] 读取 " + name + " 失败：" + ex.Message);
            }

            return null;
        }

        private static bool WasNumberPressed(int index)
        {
            var alpha = (KeyCode)((int)KeyCode.Alpha1 + index - 1);
            var keypad = (KeyCode)((int)KeyCode.Keypad1 + index - 1);
            return Input.GetKeyDown(alpha) || Input.GetKeyDown(keypad);
        }

        private static void Select(int index)
        {
            _active = false;

            var text = _texts[index];
            var confirm = string.IsNullOrEmpty(text)
                ? "已选择第 " + index + " 项。"
                : "已选择第 " + index + " 项：" + text + "。";

            try
            {
                if (_instance != null)
                {
                    var buttonObject = ReadMember(_instance, "Button" + index);
                    if (buttonObject != null)
                    {
                        var buttonType = AccessTools.TypeByName("UnityEngine.UI.Button");
                        if (buttonType != null)
                        {
                            var component = buttonObject is GameObject gameObject
                                ? gameObject.GetComponent(buttonType)
                                : buttonObject is Component unityComponent
                                    ? unityComponent.GetComponent(buttonType)
                                    : null;

                            var onClick = component == null
                                ? null
                                : buttonType.GetProperty("onClick", BindingFlags.Instance | BindingFlags.Public)?.GetValue(component, null);

                            onClick?.GetType().GetMethod("Invoke", Type.EmptyTypes)?.Invoke(onClick, null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                A11yHost.Diag("[Choice] 选择第 " + index + " 项失败：" + ex.Message);
            }

            Repeat.Drop(ChoiceTag);
            if (!string.IsNullOrEmpty(text))
            {
                SpeechGate.RegisterChoiceEcho(text);
            }

            Repeat.Say(confirm, true);
        }
    }
}
""";
}
