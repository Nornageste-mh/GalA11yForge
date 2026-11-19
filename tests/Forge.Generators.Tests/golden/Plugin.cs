// 本文件由 Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo）生成，生成器版本 0.2.86.0。
// 输入：plan.json 的补丁点与 profile。请改 profile 或重新生成，不要直接改这里的 const 区。
using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace GalA11y.ForgeGolden
{
    /// <summary>
    /// 游戏无障碍补丁的插件入口（显示名见 BepInPlugin 特性）。
    /// 语音桥来自GalA11y Demo的 Framework（Platform/ 目录），命名空间已替换为 GalA11y.ForgeGolden。
    /// </summary>
    [BepInPlugin(Guid, DisplayName, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string Guid = "09789c27-ba82-2a51-abdf-195685135660";
        internal const string Version = "0.2.86.0";
        internal const string DisplayName = "GalgameSample 无障碍补丁";

        private readonly Harmony _harmony = new Harmony(Guid);
        private Hotkey.Spec _repeatKey;
        private bool _repeatKeyReady;
        private bool _hooksPatched;

        private void Awake()
        {
            // 1) 先把日志交出去：语音桥与钩子都通过 A11yHost.Log 写日志。
            A11yHost.Log = Logger;

            // 2) 配置项：全部给安全默认值，用户可在 BepInEx\config\09789c27-ba82-2a51-abdf-195685135660.cfg 里改。
            A11yHost.CfgSpeechBackend = Config.Bind("语音", "后端", "auto",
                "语音后端：auto / nvda / sapi / zdsr。本补丁不捆绑任何语音库；缺少 NVDA/Tolk 时自动退回系统语音。");
            A11yHost.CfgDiagLog = Config.Bind("诊断", "详细日志", false,
                "开启后把钩子命中、文本过滤等细节写进 BepInEx 日志；只用于排查问题。");
            A11yHost.CfgVisibleOnly = Config.Bind("朗读", "只读可见文本", true,
                "只朗读当前可见的文本，避免把隐藏分支或未显示台词读出来。");
            A11yHost.CfgSortByPosition = Config.Bind("朗读", "按位置排序", false,
                "同一屏有多段文本时，按屏幕位置从上到下排序。");
            A11yHost.CfgExcludeNames = Config.Bind("朗读", "排除节点名", string.Empty,
                "逗号分隔的 UI 节点名，命中就不朗读；留空表示不排除。");
            var fieldRecheckSeconds = Config.Bind("朗读", "字段补读最长等待秒", 4.0f,
                "首句文本比对话框晚出现时，最多再等几秒补读；0 表示不补读。");
            A11yHost.CfgQuitConfirm = Config.Bind("确认", "退出需确认", false,
                "开启后，退出类按钮需要二次确认才生效。");
            A11yHost.CfgQuitNames = Config.Bind("确认", "退出按钮名", string.Empty,
                "逗号分隔的退出类按钮名；留空表示按默认关键词判断。");
            A11yHost.DiagOn = A11yHost.CfgDiagLog.Value;

            // 3) 导航模式未启用：给契约要求的槽位填惰性实现，避免 Validate() 一直报缺项。
            //    需要 UI 导航时，按分析结论补上 TickUiNav / BlockGameAdvance 的真实实现。
            A11yHost.TickUiNav = static () => { };
            A11yHost.BlockGameAdvance = static () => false;

            Speech.Init(Logger);
            SpeechGate.ConfigureFieldRecheck(fieldRecheckSeconds.Value);
                        SpeechGate.ConfigureWatcher(Config);
            A11yHost.Diag("语音后端：" + Speech.BackendName);

            // 4) 重读键（默认 =）：走 Unity Input，不启用全局键钩子。
            var repeatSpec = Config.Bind("快捷键", "重读", "=",
                "重读上一段文本的按键。");
            if (Hotkey.Parse(repeatSpec.Value, out var parsed, out var why))
            {
                _repeatKey = parsed;
                _repeatKeyReady = true;
            }
            else
            {
                Logger.LogWarning("[快捷键] 重读键配置无效（" + repeatSpec.Value + "）：" + why + "；本次会话禁用重读键。");
            }

            // 5) 钩子：逐个解析目标。找不到目标只警告、不抛异常，保证游戏能启动。
            PatchHooks();

            // 6) 契约自检：缺项会写 warning，但不阻断启动。
            A11yHost.Validate();
        }

        private void PatchHooks()
        {
            if (_hooksPatched)
            {
                return;
            }

            _hooksPatched = true;

            var hooks = new List<Type>();
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (!type.IsAbstract || !type.IsSealed)
                {
                    continue;
                }

                if (type.Namespace != "GalA11y.ForgeGolden.Hooks" || !type.Name.StartsWith("Hook_", StringComparison.Ordinal))
                {
                    continue;
                }

                hooks.Add(type);
            }

            hooks.Sort(static (a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            foreach (var type in hooks)
            {
                var resolve = type.GetMethod("Resolve", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var postfix = type.GetMethod("Postfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (resolve is null || postfix is null)
                {
                    Logger.LogWarning("[Hook] " + type.FullName + " 缺少 Resolve/Postfix，已跳过。");
                    continue;
                }

                MethodBase target;
                try
                {
                    target = (MethodBase)resolve.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    Logger.LogWarning("[Hook] " + type.FullName + " 解析目标时出错：" + ex.InnerException?.Message);
                    continue;
                }

                if (target is null)
                {
                    Logger.LogWarning("[Hook] " + type.FullName + " 的目标不在当前游戏程序集里，已跳过（骨架保留）。");
                    continue;
                }

                try
                {
                    _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                    Logger.LogInfo("[Hook] 已挂载 " + type.Name + " → " + target.DeclaringType?.FullName + "::" + target.Name);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("[Hook] 挂载 " + type.FullName + " 失败：" + ex.Message);
                }
            }
        }

        private void Update()
        {
            if (!_repeatKeyReady)
            {
                return;
            }

            try
            {
                if (Hotkey.Down(_repeatKey))
                {
                    Repeat.Again();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[快捷键] 重读失败：" + ex.Message);
                _repeatKeyReady = false;
            }

            SpeechGate.Update();
            SpeechGate.TickWatcher();

        }

        private void OnDestroy()
        {
            try
            {
                _harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("[Hook] 卸载钩子时出错：" + ex.Message);
            }

            Speech.Stop();
            Speech.Shutdown();
            A11yHost.Clear();
        }
    }
}