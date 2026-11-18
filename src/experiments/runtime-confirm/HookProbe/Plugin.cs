using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GalA11y.RuntimeConfirm
{
    /// <summary>
    /// R17 运行时确认探针（只读观测，不改游戏行为）。
    ///
    /// 目标：验证静态候选 AnimateText / DOText / Choice / HandleMessageText 能否被
    /// 自动生成的 Harmony Hook 稳定命中，并量化：
    ///   · 每句台词是否只出现一次（双 Hook 去重）
    ///   · 打字机是否逐字调用 TMP_Text.set_text（决定生成器要不要挂 sink）
    ///   · 选项、消息框分支是否可区分
    ///
    /// 隐私/版权约束：正文样本只写前 12 条、每条截 60 字；选项每个资产只采样一次。
    /// 绝不写整段剧本，也不写图片/音频。
    /// </summary>
    [BepInPlugin(Guid, "GalA11y Runtime Confirm Probe", "0.0.1.0")]
    public class RuntimeConfirmPlugin : BaseUnityPlugin
    {
        public const string Guid = "gala11y.runtime.confirm";

        private const int TextSampleLimit = 12;
        private const int StackSampleLimit = 60;
        private const int TextSampleChars = 60;

        private static StreamWriter _events;
        private static readonly object Gate = new object();
        private static string _dir;
        private static string _statsPath;
        private static ManualLogSource _log;
        private static readonly List<string> PatchResults = new List<string>();
        private static readonly List<string> UnityErrors = new List<string>();

        // ---- 计数器 ----
        private static int _animateTotal, _animateEmpty, _animateWithName, _animateWithVoice;
        private static readonly int[] AnimateNum = new int[4];
        private static readonly HashSet<string> AnimateKeys = new HashSet<string>();
        private static int _sampleCount, _stackCount;

        private static int _dotextTotal, _dotextEmptySpeak, _dotextWithVoice;
        private static readonly HashSet<string> DotextKeys = new HashSet<string>();

        private static int _choiceTotal, _choiceOptions;
        private static readonly HashSet<string> ChoiceAssets = new HashSet<string>();

        private static int _msgTotal, _msgEmpty;

        private static long _stTotal, _stEmpty, _stNonEmpty, _stFullMatch;
        private static int _stMaxLen, _stLongest;
        private static string _lastLine;
        private static int _lastLineLen;

        private static long _unityErrors, _unityWarnings;
        private static int _framesAtHb;
        private static float _timeAtHb;
        private static float _fpsMin = float.MaxValue, _fpsSum;
        private static int _fpsSamples;
        private static int _hbCount;

        private void Awake()
        {
            _log = Logger;
            _dir = Path.Combine(Paths.GameRootPath, "BepInEx", "runtime-confirm");
            Directory.CreateDirectory(_dir);
            _events = new StreamWriter(Path.Combine(_dir, "events.tsv"), false, new UTF8Encoding(false));
            _events.AutoFlush = true;
            _statsPath = Path.Combine(_dir, "stats.tsv");

            Application.logMessageReceived += OnUnityLog;

            LogLine("=== GalA11y Runtime Confirm Probe 0.0.1 ===");
            LogLine($"utc={DateTime.UtcNow:O}");
            LogLine($"game={Application.productName} version={Application.version} unity={Application.unityVersion}");
            LogLine($"screen={Screen.width}x{Screen.height} cpus={SystemInfo.processorCount} gfx={SystemInfo.graphicsDeviceName}");
            LogLine($"root={Paths.GameRootPath}");
            LogLine("policy=text samples: first 12 lines, <=60 chars each; choices: one sample per asset");

            var harmony = new Harmony(Guid);
            Patch(harmony, typeof(UIText), "AnimateText", nameof(AnimateText_Prefix), null);
            Patch(harmony, typeof(UIText), "DOText", nameof(Dotext_Prefix), null);
            Patch(harmony, typeof(UIText), "Choice", nameof(Choice_Prefix), null);
            Patch(harmony, typeof(UIText), "HandleMessageText", nameof(Message_Prefix), null);
            Patch(harmony, typeof(TMP_Text), "set_text", nameof(SetText_Prefix), new[] { typeof(string) });

            foreach (var p in PatchResults) LogLine("PATCH\t" + p);
            WriteStats("startup");
            StartCoroutine(Heartbeat());
        }

        private static void Patch(Harmony h, Type type, string method, string prefix, Type[] args)
        {
            try
            {
                var m = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                if (m == null)
                {
                    PatchResults.Add($"FAIL\t{type.Name}::{method}\treason=method-not-found");
                    return;
                }
                var pm = new HarmonyMethod(typeof(RuntimeConfirmPlugin).GetMethod(prefix,
                    BindingFlags.Static | BindingFlags.NonPublic));
                h.Patch(m, prefix: pm);
                PatchResults.Add($"OK\t{type.FullName}::{method}\tparams={m.GetParameters().Length}");
            }
            catch (Exception e)
            {
                PatchResults.Add($"FAIL\t{type.Name}::{method}\treason={e.GetType().Name}: {Trim(e.Message, 160)}");
            }
        }

        // ---------------- Hooks ----------------

        private static void AnimateText_Prefix(TextMeshProUGUI targetText, string text, float duration,
            UIText.CharaStatus charaStatus, int Num)
        {
            int len = text?.Length ?? 0;
            uint hash = Fnv(text);
            string key = Num + "|" + hash;
            _animateTotal++;
            AnimateKeys.Add(key);
            if (len == 0) _animateEmpty++;
            if (Num >= 0 && Num < AnimateNum.Length) AnimateNum[Num]++;
            if (!string.IsNullOrEmpty(charaStatus.charaName)) _animateWithName++;
            if (charaStatus.Voice != null) _animateWithVoice++;

            _lastLine = text;
            _lastLineLen = len;

            bool sample = _sampleCount < TextSampleLimit;
            string detail =
                $"num={Num}|len={len}|hash={hash:x8}|target={Trim(TargetPath(targetText), 80)}" +
                $"|dur={duration:F2}|nameLen={(charaStatus.charaName?.Length ?? 0)}" +
                $"|speakLen={(charaStatus.speak?.Length ?? 0)}|voice={(charaStatus.Voice != null ? Trim(charaStatus.Voice.name, 40) : "")}" +
                $"|lang={(int)charaStatus.DisplayNum}"; // DisplayNum 是频道标记，不是语言；保留字段名避免误读
            if (sample)
            {
                detail += $"|name={Sanitize(charaStatus.charaName, 40)}|text={Sanitize(text, TextSampleChars)}";
                _sampleCount++;
            }
            if (_stackCount < StackSampleLimit && sample)
            {
                detail += "|stack=" + ShortStack();
                _stackCount++;
            }
            Ev("ANIMATE", detail);
        }

        private static void Dotext_Prefix(UIText.CharaStatus charaStatus)
        {
            _dotextTotal++;
            if (string.IsNullOrEmpty(charaStatus.speak) && string.IsNullOrEmpty(charaStatus.charaName)) _dotextEmptySpeak++;
            if (charaStatus.Voice != null) _dotextWithVoice++;
            DotextKeys.Add(Fnv(charaStatus.speak).ToString("x8"));

            bool sample = _sampleCount < TextSampleLimit;
            string detail =
                $"display={charaStatus.DisplayNum}|voice={(charaStatus.Voice != null ? Trim(charaStatus.Voice.name, 40) : "")}" +
                $"|jaLen={(charaStatus.speak?.Length ?? 0)}|scLen={(charaStatus.speakSC?.Length ?? 0)}" +
                $"|enLen={(charaStatus.speakEN?.Length ?? 0)}|tcLen={(charaStatus.speakTC?.Length ?? 0)}" +
                $"|ja={Fnv(charaStatus.speak):x8}|sc={Fnv(charaStatus.speakSC):x8}" +
                $"|en={Fnv(charaStatus.speakEN):x8}|tc={Fnv(charaStatus.speakTC):x8}" +
                $"|nameLen={(charaStatus.charaName?.Length ?? 0)}";
            if (sample)
            {
                detail += $"|name={Sanitize(charaStatus.charaName, 40)}|jaText={Sanitize(charaStatus.speak, TextSampleChars)}";
                _sampleCount++;
            }
            Ev("DOTEXT", detail);
        }

        private static void Choice_Prefix(ChoiceAsset cs, int hideMask)
        {
            _choiceTotal++;
            if (cs == null) { Ev("CHOICE", "asset=(null)"); return; }
            string asset = string.IsNullOrEmpty(cs.name) ? "(unnamed)" : cs.name;
            ChoiceAssets.Add(asset);
            int count = Mathf.Clamp(cs.ChoiceCount, 1, 3);
            _choiceOptions += count;
            bool first = ChoiceAssets.Count <= 3 || !_choiceSeen.Contains(asset);
            bool sample = first && _choiceSeen.Count < 3;
            if (sample) _choiceSeen.Add(asset);

            var sb = new StringBuilder();
            sb.Append("asset=").Append(Trim(asset, 60));
            sb.Append("|choiceNum=").Append(cs.ChoiceNum).Append("|count=").Append(count).Append("|hideMask=").Append(hideMask);
            AppendChoice(sb, 1, cs.Choice1, sample);
            AppendChoice(sb, 2, cs.Choice2, sample);
            AppendChoice(sb, 3, cs.Choice3, sample);
            Ev("CHOICE", sb.ToString());
        }

        private static readonly HashSet<string> _choiceSeen = new HashSet<string>();

        private static void AppendChoice(StringBuilder sb, int n, ChoiceAsset.QuadLang q, bool sample)
        {
            sb.Append($"|c{n}Ja={q.Ja?.Length ?? 0}:{Fnv(q.Ja):x8}");
            sb.Append($",Sc={q.Sc?.Length ?? 0}:{Fnv(q.Sc):x8}");
            sb.Append($",En={q.En?.Length ?? 0}:{Fnv(q.En):x8}");
            sb.Append($",Tc={q.Tc?.Length ?? 0}:{Fnv(q.Tc):x8}");
            if (sample)
            {
                sb.Append($"|c{n}Sc={Sanitize(q.Sc, 40)}|c{n}Ja={Sanitize(q.Ja, 40)}");
            }
        }

        private static void Message_Prefix(UIText.CharaStatus charaStatus, float valueTime)
        {
            _msgTotal++;
            if (string.IsNullOrEmpty(charaStatus.speak)) _msgEmpty++;
            Ev("MESSAGE", $"len={(charaStatus.speak?.Length ?? 0)}|hash={Fnv(charaStatus.speak):x8}" +
                $"|nameLen={(charaStatus.charaName?.Length ?? 0)}|voice={(charaStatus.Voice != null ? Trim(charaStatus.Voice.name, 40) : "")}" +
                $"|time={valueTime:F2}");
        }

        private static void SetText_Prefix(TMP_Text __instance, string value)
        {
            int len = value?.Length ?? 0;
            Interlocked.Increment(ref _stTotal);
            if (len == 0) Interlocked.Increment(ref _stEmpty);
            else Interlocked.Increment(ref _stNonEmpty);
            if (len > _stMaxLen)
            {
                _stMaxLen = len;
                _stLongest = __instance != null ? __instance.GetInstanceID() : 0;
            }
            string last = _lastLine;
            if (last != null && len == _lastLineLen && string.Equals(value, last, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _stFullMatch);
            }
        }

        // ---------------- Stats ----------------

        private IEnumerator Heartbeat()
        {
            while (true)
            {
                yield return new WaitForSeconds(10f);
                _hbCount++;
                float now = Time.realtimeSinceStartup;
                int frames = Time.frameCount;
                if (_timeAtHb > 0f && now > _timeAtHb)
                {
                    float fps = (frames - _framesAtHb) / (now - _timeAtHb);
                    _fpsSum += fps;
                    _fpsSamples++;
                    if (fps < _fpsMin) _fpsMin = fps;
                }
                _timeAtHb = now;
                _framesAtHb = frames;
                WriteStats("heartbeat");
            }
        }

        private static void WriteStats(string tag)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("key\tvalue");
                sb.AppendLine("tag\t" + tag);
                sb.AppendLine("utc\t" + DateTime.UtcNow.ToString("O"));
                sb.AppendLine("uptime_s\t" + Time.realtimeSinceStartup.ToString("F1"));
                sb.AppendLine("animate_total\t" + _animateTotal);
                sb.AppendLine("animate_unique\t" + AnimateKeys.Count);
                sb.AppendLine("animate_empty\t" + _animateEmpty);
                sb.AppendLine("animate_with_name\t" + _animateWithName);
                sb.AppendLine("animate_with_voice\t" + _animateWithVoice);
                sb.AppendLine("animate_num0\t" + AnimateNum[0]);
                sb.AppendLine("animate_num1\t" + AnimateNum[1]);
                sb.AppendLine("animate_num2\t" + AnimateNum[2]);
                sb.AppendLine("dotext_total\t" + _dotextTotal);
                sb.AppendLine("dotext_unique\t" + DotextKeys.Count);
                sb.AppendLine("dotext_empty_speak\t" + _dotextEmptySpeak);
                sb.AppendLine("dotext_with_voice\t" + _dotextWithVoice);
                sb.AppendLine("choice_total\t" + _choiceTotal);
                sb.AppendLine("choice_assets\t" + ChoiceAssets.Count);
                sb.AppendLine("choice_options\t" + _choiceOptions);
                sb.AppendLine("message_total\t" + _msgTotal);
                sb.AppendLine("message_empty\t" + _msgEmpty);
                sb.AppendLine("settext_total\t" + Interlocked.Read(ref _stTotal));
                sb.AppendLine("settext_empty\t" + Interlocked.Read(ref _stEmpty));
                sb.AppendLine("settext_nonempty\t" + Interlocked.Read(ref _stNonEmpty));
                sb.AppendLine("settext_full_line_match\t" + Interlocked.Read(ref _stFullMatch));
                sb.AppendLine("settext_max_len\t" + _stMaxLen);
                sb.AppendLine("settext_longest_id\t" + _stLongest);
                sb.AppendLine("unity_errors\t" + Interlocked.Read(ref _unityErrors));
                sb.AppendLine("unity_warnings\t" + Interlocked.Read(ref _unityWarnings));
                sb.AppendLine("fps_min\t" + (_fpsSamples > 0 ? _fpsMin.ToString("F1") : "na"));
                sb.AppendLine("fps_avg\t" + (_fpsSamples > 0 ? (_fpsSum / _fpsSamples).ToString("F1") : "na"));
                sb.AppendLine("fps_samples\t" + _fpsSamples);
                sb.AppendLine("frames\t" + Time.frameCount);
                sb.AppendLine("heartbeats\t" + _hbCount);
                if (UnityErrors.Count > 0)
                {
                    sb.AppendLine("unity_error_samples\t" + UnityErrors.Count);
                    for (int i = 0; i < UnityErrors.Count; i++) sb.AppendLine("unity_error_" + i + "\t" + UnityErrors[i]);
                }
                File.WriteAllText(_statsPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                Interlocked.Increment(ref _unityErrors);
                if (UnityErrors.Count < 5) UnityErrors.Add(Trim(condition, 200));
            }
            else if (type == LogType.Warning)
            {
                Interlocked.Increment(ref _unityWarnings);
            }
        }

        private void OnApplicationQuit()
        {
            Ev("QUIT", $"uptime={Time.realtimeSinceStartup:F1}|frames={Time.frameCount}");
            WriteStats("quit");
            try { _events?.Flush(); _events?.Dispose(); } catch { }
            Application.logMessageReceived -= OnUnityLog;
        }

        // ---------------- helpers ----------------

        private static void Ev(string kind, string detail)
        {
            if (_events == null) return;
            try
            {
                lock (Gate) _events.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + kind + "\t" + detail);
            }
            catch { }
        }

        private static void LogLine(string s)
        {
            try { _log?.LogInfo(s); } catch { }
            Ev("LOG", s);
        }

        private static string ShortStack()
        {
            try
            {
                var st = new StackTrace(2, false);
                var parts = new List<string>();
                foreach (var f in st.GetFrames() ?? Array.Empty<StackFrame>())
                {
                    var m = f.GetMethod();
                    if (m == null) continue;
                    string tn = m.DeclaringType != null ? m.DeclaringType.Name : "?";
                    if (tn == "RuntimeConfirmPlugin") continue;
                    parts.Add(tn + "." + m.Name);
                    if (parts.Count >= 3) break;
                }
                return string.Join("<", parts);
            }
            catch { return "na"; }
        }

        private static string TargetPath(Component c)
        {
            if (c == null) return "(null)";
            try
            {
                string p = c.name;
                var t = c.transform;
                for (int i = 0; i < 2 && t != null && t.parent != null; i++)
                {
                    t = t.parent;
                    p = t.name + "/" + p;
                }
                return p;
            }
            catch { return c.name; }
        }

        private static string Sanitize(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static uint Fnv(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h;
        }
    }
}
