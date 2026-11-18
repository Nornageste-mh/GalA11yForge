// 本文件由 GalA11y Forge（无障碍补丁工坊）生成，生成器版本 0.2.85.0。
// 作用：把钩子抓到的文本整理后交给平台朗读层；去重与缓冲策略集中在这里，
// 避免同一个钩子或相邻钩子把一句话重复念好几遍。平台纪律：逐作层只经 Repeat 说话。
using System;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace GalA11y.ForgeGolden
{
    /// <summary>朗读闸门：主钩子走 Say（立即朗读），辅助钩子走 Note（只进重读缓冲）。</summary>
    internal static class SpeechGate
    {
        private const int MaxTextLength = 1000;
        private const float DuplicateWindowSeconds = 2f;

        private static string _lastSpoken = string.Empty;
        private static float _lastSpokenAt = -999f;
        private static string _lastNoted = string.Empty;

        // 选项回显：玩家选完某项后，游戏可能在几秒到几分钟后把同一句作为台词再发一次。
        // 这里登记已选文本，回声到达时只记诊断、不再朗读（避免同一句读两遍）。
        private static readonly HashSet<string> _choiceEchoes = new HashSet<string>(System.StringComparer.Ordinal);

        // 字段快照：字段是类型级声明，同一类型的多个方法共享同一组字段；
        // 记录每个实例上次读到的字段值，只朗读“本次调用后发生变化”的字段，避免把旧句/调试文本再念一遍。
        private static readonly ConditionalWeakTable<object, Dictionary<string, string>> _fieldSnapshots =
            new ConditionalWeakTable<object, Dictionary<string, string>>();

        // 延迟复查：真机 GalMaster 首句的文本是在钩子返回之后才赋给实例字段的，
        // 当时读不到、之后又没有第二次钩子，于是整句漏读（0.2.6 复验记录 docs/43）。
        // 只在本次读不到时登记，Plugin.Update 里按小间隔复查；等待上限可由配置覆盖（0=关闭）。
        private const float FieldRecheckDelaySeconds = 0.25f;
        private const float DefaultFieldRecheckMaxWaitSeconds = 4.0f;
        private static float _fieldRecheckMaxWaitSeconds = DefaultFieldRecheckMaxWaitSeconds;
        private static readonly List<PendingFieldCheck> _pendingFieldChecks = new List<PendingFieldCheck>();

        private sealed class PendingFieldCheck
        {
            internal System.WeakReference Instance = null!;
            internal string[] FieldNames = System.Array.Empty<string>();
            internal float StartedAt;
            internal float DueAt;
        }

        /// <summary>选中选项后登记文本，供回声去重。</summary>
        internal static void RegisterChoiceEcho(string? text)
        {
            if (TryNormalize(text, out var clean))
            {
                _choiceEchoes.Add(clean);
            }
        }

        /// <summary>配置延迟复查的等待上限（秒）。0=关闭补读；负数按 0；超过 15 秒按 15 秒。</summary>
        internal static void ConfigureFieldRecheck(float maxWaitSeconds)
        {
            _fieldRecheckMaxWaitSeconds = maxWaitSeconds <= 0f ? 0f : Mathf.Min(maxWaitSeconds, 15f);
        }

        /// <summary>
        /// 字段兜底：有些游戏把台词写在实例字段里（TMP_Text/UI.Text 组件），方法本身没有 string 参数。
        /// 这里按字段名反射取值，拼成一句话再走 Say；读不到就静默，不影响游戏。
        /// </summary>
        internal static void SayFields(object? instance, string[] fieldNames)
        {
            var text = CollectFields(instance, fieldNames);
            if (text is not null)
            {
                Say(text);
                return;
            }

            ScheduleFieldRecheck(instance, fieldNames);
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

        /// <summary>
        /// 属性链兜底（0.2.48）：有些框架（如 Utage/宴）的正文入口没有 string 参数，文本挂在实例
        /// 属性链上（例：AdvMessageWindow.Text.OriginalText）。这里按链逐段反射，最后一段取 string 朗读。
        /// 读不到只记一条诊断，不影响游戏。
        /// </summary>
        internal static void SayPropertyChain(object? instance, string[] chain)
        {
            var text = CollectPropertyChain(instance, chain);
            if (text is not null)
            {
                Say(text);
                return;
            }

            A11yHost.Diag("[Speech:chain] 命中钩子但属性链为空（" + DescribeChain(chain) + "），未朗读。");
        }

        /// <summary>属性链兜底的辅助出口：只进重读缓冲，不主动出声。</summary>
        internal static void NotePropertyChain(object? instance, string[] chain)
        {
            var text = CollectPropertyChain(instance, chain);
            if (text is not null)
            {
                Note(text);
            }
        }

        private static string? CollectPropertyChain(object? instance, string[] chain)
        {
            if (instance is null || chain is null || chain.Length == 0)
            {
                return null;
            }

            object? current = instance;
            try
            {
                for (var i = 0; i < chain.Length; i++)
                {
                    if (current is null || string.IsNullOrEmpty(chain[i]))
                    {
                        return null;
                    }

                    var type = current.GetType();
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    object? next;
                    var property = type.GetProperty(chain[i], flags);
                    if (property is not null && property.GetIndexParameters().Length == 0)
                    {
                        next = property.GetValue(current, null);
                    }
                    else
                    {
                        var field = type.GetField(chain[i], flags);
                        next = field?.GetValue(current);
                    }

                    current = next;
                }

                if (current is not string raw)
                {
                    return null;
                }

                var clean = raw.Trim();
                return clean.Length == 0 ? null : clean;
            }
            catch (System.Exception ex)
            {
                A11yHost.Diag("[Speech:chain] 读取属性链失败（" + DescribeChain(chain) + "）：" + ex.GetType().Name);
                return null;
            }
        }

        private static string DescribeChain(string[]? chain)
        {
            if (chain is null || chain.Length == 0)
            {
                return "空链";
            }

            var parts = new List<string>(chain.Length);
            foreach (var part in chain)
            {
                parts.Add(part ?? "?");
            }

            return string.Join(".", parts);
        }

        /// <summary>登记一次延迟复查：同一实例只留一条，避免每个钩子都排一条。</summary>
        private static void ScheduleFieldRecheck(object? instance, string[] fieldNames)
        {
            if (_fieldRecheckMaxWaitSeconds <= 0f || instance is null || fieldNames is null || fieldNames.Length == 0)
            {
                return;
            }

            for (var i = 0; i < _pendingFieldChecks.Count; i++)
            {
                if (ReferenceEquals(_pendingFieldChecks[i].Instance.Target, instance))
                {
                    return;
                }
            }

            var now = Time.unscaledTime;
            A11yHost.Diag("[Speech:fields] 已登记延迟复查（" + fieldNames.Length + " 个字段）。");
            _pendingFieldChecks.Add(new PendingFieldCheck
            {
                Instance = new System.WeakReference(instance),
                FieldNames = fieldNames,
                StartedAt = now,
                DueAt = now + FieldRecheckDelaySeconds,
            });
        }

        /// <summary>每帧入口（Plugin.Update 调用）：到点复查一次；仍读不到就再等一轮，默认最多 4 秒。</summary>
        internal static void Update()
        {
            if (_fieldRecheckMaxWaitSeconds <= 0f || _pendingFieldChecks.Count == 0)
            {
                return;
            }

            var now = Time.unscaledTime;
            for (var i = _pendingFieldChecks.Count - 1; i >= 0; i--)
            {
                var pending = _pendingFieldChecks[i];
                var instance = pending.Instance.Target;
                if (instance is null)
                {
                    _pendingFieldChecks.RemoveAt(i);
                    continue;
                }

                if (now < pending.DueAt)
                {
                    continue;
                }

                _pendingFieldChecks.RemoveAt(i);
                try
                {
                    var text = CollectFields(instance, pending.FieldNames);
                    if (text is not null)
                    {
                        A11yHost.Diag("[Speech:fields] 延迟复查补读。");
                        Say(text);
                    }
                    else if (now - pending.StartedAt < _fieldRecheckMaxWaitSeconds)
                    {
                        pending.DueAt = now + FieldRecheckDelaySeconds;
                        _pendingFieldChecks.Add(pending);
                    }
                    else
                    {
                        A11yHost.Diag("[Speech:fields] 延迟复查超时放弃（" + pending.FieldNames.Length + " 个字段）。");
                    }
                }
                catch (System.Exception ex)
                {
                    A11yHost.Diag("[Speech:fields] 延迟复查失败：" + ex.GetType().Name);
                }
            }
        }

        private static string? CollectFields(object? instance, string[] fieldNames)
        {
            if (instance is null || fieldNames is null || fieldNames.Length == 0)
            {
                return null;
            }

            Dictionary<string, string>? snapshot = null;
            try
            {
                snapshot = _fieldSnapshots.GetOrCreateValue(instance);
            }
            catch (System.Exception ex)
            {
                A11yHost.Diag("[Speech:fields] 快照失败：" + ex.GetType().Name);
            }

            var all = new List<string>(fieldNames.Length);
            var changed = new List<string>(fieldNames.Length);
            foreach (var name in fieldNames)
            {
                try
                {
                    var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var text = ExtractText(field?.GetValue(instance));
                    var clean = string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
                    if (snapshot is not null)
                    {
                        if (clean is not null)
                        {
                            if (!snapshot.TryGetValue(name, out var previous) || !string.Equals(previous, clean, System.StringComparison.Ordinal))
                            {
                                changed.Add(clean);
                            }

                            snapshot[name] = clean;
                        }
                        else
                        {
                            snapshot.Remove(name);
                        }
                    }

                    if (clean is not null)
                    {
                        all.Add(clean);
                    }
                }
                catch (System.Exception ex)
                {
                    A11yHost.Diag("[Speech:fields] 读取字段失败 " + name + "：" + ex.GetType().Name);
                }
            }

            var selected = snapshot is null ? all : changed;
            if (selected.Count == 0)
            {
                A11yHost.Diag(all.Count == 0
                    ? "[Speech:fields] 命中钩子但字段为空，未朗读。"
                    : "[Speech:fields] 字段与上次相同，跳过（避免重读旧句）。");
                return null;
            }

            var unique = new List<string>(selected.Count);
            foreach (var part in selected)
            {
                if (!unique.Contains(part))
                {
                    unique.Add(part);
                }
            }

            return string.Join("，", unique);
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
        internal static void Say(string? text)
        {
            if (!TryNormalize(text, out var clean))
            {
                return;
            }

            if (_choiceEchoes.Remove(clean))
            {
                A11yHost.Diag("[Speech:choice-echo] " + clean);
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (clean == _lastSpoken && now - _lastSpokenAt < DuplicateWindowSeconds)
            {
                return;
            }

            _lastSpoken = clean;
            _lastSpokenAt = now;
            A11yHost.Diag("[Speech] " + clean);
            Repeat.Say(clean, true);
        }

        /// <summary>辅助出口：只更新重读缓冲，不主动出声（避免同句多念一遍）。</summary>
        internal static void Note(string? text)
        {
            if (!TryNormalize(text, out var clean) || clean == _lastNoted)
            {
                return;
            }

            _lastNoted = clean;
            A11yHost.Diag("[Speech:note] " + clean);
            Repeat.Note(clean);
        }

        // 有些游戏会调用“设置标题/标签”的接口并把占位符当正文传进来
        // （真机例子：Naninovel 的 SetTitleText 反复收到 "[8] Empty"）。
        // 这类值不是台词，念出来只会制造噪声，直接丢弃。
        private static readonly Regex PlaceholderTextRx = new Regex(
            @"^(?:\[\d+\])?\s*(?:empty|none|null|n/a|未设置|无内容)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ── 文本兜底轮询（0.2.51）──────────────────────────────────────────
        // 有些 Unity 游戏（尤其未知框架）没能识别到正文入口：钩子挂上了，剧情却一句都不念。
        // 这里做一层兜底：定时扫描场景里可见的 UGUI / TMP 文本组件，只把“新出现或变化”的文本交给 Say()。
        // 类型和 text 属性都用反射取：游戏没有 UGUI/TMP 时也不会编译失败，只是这一路不生效。
        private static bool _watchInitialized;
        private static bool _watchEnabled;
        private static float _watchInterval = 0.3f;
        private static float _watchTimer;
        private static readonly List<Type> WatchTypes = new List<Type>();
        private static readonly Dictionary<Type, PropertyInfo> WatchProps = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<int, string> WatchSeen = new Dictionary<int, string>();

        internal static void ConfigureWatcher(BepInEx.Configuration.ConfigFile config)
        {
            var enabled = config.Bind("朗读", "文本兜底轮询", true,
                "没识别到正文入口时的兜底：定时扫描界面上的文本组件，新出现的文本会被朗读。默认开。");
            var interval = config.Bind("朗读", "兜底轮询间隔秒", 0.3f,
                "兜底轮询的间隔（秒），越小越灵敏但越耗性能；范围 0.1–2。");
            _watchEnabled = enabled.Value;
            _watchInterval = Mathf.Clamp(interval.Value, 0.1f, 2f);
            AddWatchType("UnityEngine.UI.Text, UnityEngine.UI");
            AddWatchType("TMPro.TMP_Text, Unity.TextMeshPro");
            _watchInitialized = true;
            A11yHost.Diag("[Speech:watch] 文本兜底轮询：" + (_watchEnabled ? "开" : "关") + "，间隔 " + _watchInterval + " 秒。");
        }

        private static void AddWatchType(string assemblyQualifiedName)
        {
            try
            {
                var type = Type.GetType(assemblyQualifiedName);
                if (type == null)
                {
                    return;
                }

                var prop = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (prop == null)
                {
                    return;
                }

                WatchTypes.Add(type);
                WatchProps[type] = prop;
            }
            catch (Exception ex)
            {
                A11yHost.Diag("[Speech:watch] 找不到文本类型：" + ex.GetType().Name);
            }
        }

        internal static void TickWatcher()
        {
            if (!_watchInitialized || !_watchEnabled)
            {
                return;
            }

            _watchTimer += Time.unscaledDeltaTime;
            if (_watchTimer < _watchInterval)
            {
                return;
            }

            _watchTimer = 0f;
            foreach (var type in WatchTypes)
            {
                ScanWatchType(type);
            }

            if (WatchSeen.Count > 4096)
            {
                WatchSeen.Clear();
            }
        }

        // 0.2.52：TMP 富文本标签与纯数字/页码类文本不念（真机实测：<u><size=50>…</s> 与「9」「1/4」都会被读到）。
        private static readonly Regex WatchMarkupRx = new Regex("<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex WatchNumberRx = new Regex("^[0-9]{1,3}(/[0-9]{1,3})?$", RegexOptions.Compiled);
        private static readonly Regex WatchPlaceholderRx = new Regex("^【[\\s?？]{1,12}】$", RegexOptions.Compiled);

        private static readonly Regex WatchResolutionRx = new Regex("^[0-9]{3,4}x[0-9]{3,4}$", RegexOptions.Compiled);

        // 0.2.54：设置项取值取消念（真机实测：Off / On / Windowed / FullScreen 会被当文本读）。
        private static readonly HashSet<string> WatchIgnoreExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "on", "off", "true", "false", "yes", "no", "none", "auto", "default",
            "enable", "enabled", "disable", "disabled", "windowed", "fullscreen", "full screen",
            "new text", "new text (legacy)", "text", "???", "? ?",
        };

        private static string? CleanWatchText(string? text)
        {
            if (text is null) { return null; }
            var clean = WatchMarkupRx.Replace(text, string.Empty).Trim();
            if (clean.Length == 0) { return null; }
            if (WatchNumberRx.IsMatch(clean)) { return null; }
            if (WatchResolutionRx.IsMatch(clean)) { return null; }
            if (WatchPlaceholderRx.IsMatch(clean)) { return null; }
            if (WatchIgnoreExact.Contains(clean)) { return null; }
            return clean;
        }

        private static void ScanWatchType(Type type)
        {
            if (!WatchProps.TryGetValue(type, out var prop))
            {
                return;
            }

            UnityEngine.Object[] objects;
            try
            {
                objects = Resources.FindObjectsOfTypeAll(type);
            }
            catch (Exception ex)
            {
                A11yHost.Diag("[Speech:watch] 扫描失败：" + ex.GetType().Name);
                return;
            }

            foreach (var obj in objects)
            {
                var component = obj as Component;
                if (component == null)
                {
                    continue;
                }

                try
                {
                    if (!component.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    var text = CleanWatchText(prop.GetValue(obj, null) as string);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var id = component.GetInstanceID();
                    if (WatchSeen.TryGetValue(id, out var last) && string.Equals(last, text, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    WatchSeen[id] = text;
                    A11yHost.Diag("[Speech:watch] 兜底读到：" + (text.Length > 40 ? text.Substring(0, 40) + "…" : text));
                    Say(text);
                }
                catch (Exception ex)
                {
                    A11yHost.Diag("[Speech:watch] 读取失败：" + ex.GetType().Name);
                }
            }
        }

        private static bool TryNormalize(string? text, out string clean)
        {
            clean = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (clean.Length == 0)
            {
                return false;
            }

            if (PlaceholderTextRx.IsMatch(clean))
            {
                A11yHost.Diag("[Speech:placeholder] 跳过占位文本：" + clean);
                return false;
            }

            if (clean.Length > MaxTextLength)
            {
                clean = clean[..MaxTextLength];
            }

            return true;
        }
    }
}