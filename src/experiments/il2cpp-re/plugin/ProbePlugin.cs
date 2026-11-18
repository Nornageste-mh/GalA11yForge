using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Naninovel;

namespace SiliconHeartReProbe
{
    /// <summary>
    /// R18 / task-21 最小 Hook 探针 v2：《硅心》(SiliconHeart, Unity 2021.3.15f1 IL2CPP + Naninovel)
    /// v1 结论：setter 补丁装上并触发，但抓到的 value 全为空串；PrintTextAsync 前缀 0 命中；
    ///           OnPrintTextStarted 订阅后 0 回调；TMP 补丁目标解析失败。
    /// v2 目标：把"空串"与"真的没台词"区分开，并补 uGUI 分支 RevealableUIText，跑更久等首行台词。
    ///   A  Naninovel.UITextPrinter.set_Text          → 记录 null/len + 实例类型 + 首 3 条真样本
    ///   B  RevealableTextPrinterPanel.set_PrintedText
    ///   C  RevealableTextPrinterPanel.set_AuthorNameText
    ///   D  Naninovel.UI.RevealableTMProText.set_Text
    ///   E  TMPro.TMP_Text.set_text（诊断，v1 因 attribute 指向子类而失败）
    ///   F  UnityEngine.UI.Text.set_text（诊断，带实例层级路径）
    ///   G  TextPrinterManager.PrintTextAsync（Prefix；struct 返回值，v1 命中 0）
    ///   I  Naninovel.UI.RevealableUIText.set_Text（uGUI 分支的 IRevealableText.Text 实现；注意大写 Text，基类才是 set_text）
    ///   H  DelegateSupport.ConvertDelegate → add_OnPrintTextStarted（v1 0 回调）
    /// </summary>
    [BepInPlugin(Guid, "SiliconHeart RE Probe", "0.2.0")]
    public class ProbePlugin : BasePlugin
    {
        public const string Guid = "siliconheart.re.probe";

        internal static ManualLogSource L;
        internal static string OutPath;
        internal static string LinePath;

        internal static int CntText, CntTextEmpty, CntTextNonEmpty;
        internal static int CntPrinted, CntPrintedEmpty;
        internal static int CntAuthor, CntAuthorEmpty;
        internal static int CntRevealTmp, CntTmp, CntUgui, CntRevealUi, CntPrintAsync, CntEvent, CntDialogueUgui;
        internal static int HookFailures;
        internal static int Written;
        internal static readonly System.Collections.Generic.List<string> Samples = new System.Collections.Generic.List<string>();
        internal static readonly System.Collections.Generic.Dictionary<string, int> Printers = new System.Collections.Generic.Dictionary<string, int>();

        public override void Load()
        {
            L = Log;
            OutPath = Path.Combine(Paths.BepInExRootPath, "re-probe.log");
            LinePath = Path.Combine(Paths.BepInExRootPath, "re-lines.txt");
            try
            {
                File.WriteAllText(OutPath,
                    "=== SiliconHeart RE probe v2 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\n",
                    new UTF8Encoding(false));
                File.WriteAllText(LinePath,
                    "# redacted line stats: src<TAB>len<TAB>cjk<TAB>has_richtag<TAB>sha1_8  (samples<=3,<=50 chars)\n",
                    new UTF8Encoding(false));
            }
            catch { }

            L.LogInfo("[probe] Load() entered; root=" + Paths.BepInExRootPath);

            TryPatch("A UITextPrinter.set_Text", typeof(UITextPrinter), "set_Text", typeof(HookText));
            TryPatch("B RevealableTextPrinterPanel.set_PrintedText", typeof(Naninovel.UI.RevealableTextPrinterPanel), "set_PrintedText", typeof(HookPrinted));
            TryPatch("C RevealableTextPrinterPanel.set_AuthorNameText", typeof(Naninovel.UI.RevealableTextPrinterPanel), "set_AuthorNameText", typeof(HookAuthor));
            TryPatch("D RevealableTMProText.set_Text", typeof(Naninovel.UI.RevealableTMProText), "set_Text", typeof(HookRevealTmp));
            TryPatch("E TMPro.TMP_Text.set_text", typeof(TMPro.TMP_Text), "set_text", typeof(HookTmp));
            TryPatch("F UnityEngine.UI.Text.set_text", typeof(UnityEngine.UI.Text), "set_text", typeof(HookUgui));
            TryPatch("G TextPrinterManager.PrintTextAsync", typeof(TextPrinterManager), "PrintTextAsync", typeof(HookPrintAsync));
            TryPatch("I RevealableUIText.set_Text", typeof(Naninovel.UI.RevealableUIText), "set_Text", typeof(HookRevealUi));

            try { AddComponent<ProbeBehaviour>(); L.LogInfo("[probe] H watcher component added"); }
            catch (Exception e) { HookFailures++; L.LogError("[probe] H AddComponent failed: " + e); }

            try
            {
                var prev = UnityEngine.Application.runInBackground;
                UnityEngine.Application.runInBackground = true;
                L.LogInfo("[probe] Application.runInBackground " + prev + " -> true");
            }
            catch (Exception e) { L.LogWarning("[probe] set runInBackground failed: " + e.Message); }
        }

        private void TryPatch(string label, Type type, string method, Type patchType)
        {
            try
            {
                var mi = AccessTools.Method(type, method);
                if (mi == null)
                {
                    HookFailures++;
                    L.LogError("[probe] " + label + " -> AccessTools.Method null（interop 未暴露该方法）");
                    Append("PATCH-NULL\t" + label);
                    return;
                }
                var h = new Harmony(Guid + "." + patchType.Name);
                h.PatchAll(patchType);
                L.LogInfo("[probe] " + label + " patched OK (ret=" + mi.ReturnType.Name + ")");
                Append("PATCH-OK\t" + label + "\t" + mi.ReturnType.Name);
            }
            catch (Exception e)
            {
                HookFailures++;
                L.LogError("[probe] " + label + " PATCH FAILED: " + e.GetType().Name + ": " + e.Message);
                Append("PATCH-FAIL\t" + label + "\t" + e.GetType().Name + ": " + e.Message);
            }
        }

        internal static void Append(string line)
        {
            try { File.AppendAllText(OutPath, line + "\n", new UTF8Encoding(false)); } catch { }
        }

        /// <summary>
        /// 记录一条文本事件。为遵守"不导出剧本"，原始串只在内存里统计，
        /// 落盘只写：长度 / CJK 字数 / 是否含富文本标签 / sha1 前 8 位；样本最多 3 条且截断到 50 字。
        /// </summary>
        internal static void Emit(string src, string author, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Written++;
            int cjk = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 0x4E00 && c <= 0x9FFF) cjk++;
            }
            bool tag = text.IndexOf('<') >= 0;
            string h;
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                h = BitConverter.ToString(bytes).Replace("-", "").Substring(0, 8);
            }
            try
            {
                File.AppendAllText(LinePath,
                    string.Format("{0}\t{1}\t{2}\t{3}\t{4}\n", src, text.Length, cjk, tag, h),
                    new UTF8Encoding(false));
            }
            catch { }
            if (Samples.Count < 3 && cjk > 0 && text.Length <= 200)
            {
                string s = text.Length <= 50 ? text : text.Substring(0, 50);
                Samples.Add(src + " len=" + text.Length + " cjk=" + cjk + " :: " + s.Replace("\n", " ").Replace("\r", " "));
            }
        }

        internal static void LogLen(string tag, string value)
        {
            // 只记长度与 null，不记内容（区分"空串"和"真文本"）
            L.LogInfo("[probe] " + tag + " value=" + (value == null ? "null" : "len=" + value.Length));
        }

        internal static void WriteSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[summary] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("A UITextPrinter.set_Text=" + CntText + " (empty/null=" + CntTextEmpty + ", nonEmpty=" + CntTextNonEmpty + ")");
            sb.AppendLine("B set_PrintedText=" + CntPrinted + " (empty/null=" + CntPrintedEmpty + ")");
            sb.AppendLine("C set_AuthorNameText=" + CntAuthor + " (empty/null=" + CntAuthorEmpty + ")");
            sb.AppendLine("D RevealableTMProText.set_Text=" + CntRevealTmp);
            sb.AppendLine("E TMP_Text.set_text=" + CntTmp);
            sb.AppendLine("F UnityEngine.UI.Text.set_text=" + CntUgui);
            sb.AppendLine("I RevealableUIText.set_Text=" + CntRevealUi);
            sb.AppendLine("J dialogue-uGUI (under TextPrinter)=" + CntDialogueUgui);
            sb.AppendLine("G TextPrinterManager.PrintTextAsync=" + CntPrintAsync);
            sb.AppendLine("H OnPrintTextStarted event=" + CntEvent);
            sb.AppendLine("hook_failures=" + HookFailures + " nonEmpty_lines=" + Written);
            foreach (var kv in Printers) sb.AppendLine("printer " + kv.Key + " = " + kv.Value);
            for (int i = 0; i < Samples.Count; i++) sb.AppendLine("sample" + (i + 1) + ": " + Samples[i]);
            Append(sb.ToString());
            L.LogInfo("[probe] summary:\n" + sb);
        }
    }

    public class ProbeBehaviour : UnityEngine.MonoBehaviour
    {
        private int _ticks;
        private bool _subscribed;
        private bool _clicked;
        private bool _skipClicked;
        private int _nextSkipAt;
        private int _nextClickAt;
        private int _nextBeat;

        public void Awake() { ProbePlugin.Append("LIFECYCLE\tAwake"); }
        public void Start() { ProbePlugin.Append("LIFECYCLE\tStart"); }
        public void OnDestroy() { ProbePlugin.Append("LIFECYCLE\tOnDestroy"); }

        public void Update()
        {
            _ticks++;
            if (_ticks == 1) ProbePlugin.L.LogInfo("[probe] Update alive");

            try
            {
                if (!_subscribed && Naninovel.Engine.Initialized)
                {
                    var mgr = Naninovel.Engine.GetService<ITextPrinterManager>();
                    if (mgr != null)
                    {
                        Action<PrintTextArgs> handler = OnPrint;
                        var del = Il2CppInterop.Runtime.DelegateSupport
                            .ConvertDelegate<Il2CppSystem.Action<PrintTextArgs>>(handler);
                        mgr.add_OnPrintTextStarted(del);
                        _subscribed = true;
                        _nextClickAt = _ticks + 60;
                        ProbePlugin.L.LogInfo("[probe] H SUBSCRIBED at tick=" + _ticks);
                        ProbePlugin.Append("SUBSCRIBED\tOnPrintTextStarted\ttick=" + _ticks);
                    }
                }
                else if (_subscribed && !_clicked && _ticks >= _nextClickAt)
                {
                    _nextClickAt = _ticks + 300;
                    _clicked = TryClickStart();
                    if (_clicked) _nextSkipAt = _ticks + 900; // 15s 后点 Skip，让引擎自己把台词推下去
                }
                else if (_clicked && !_skipClicked && _ticks >= _nextSkipAt)
                {
                    TryPassNameGate();
                    DumpActiveButtons();
                    _skipClicked = TryClickSkip();
                    TryClickAuto();
                    _nextSkipAt = _ticks + 900;
                }

                if (_ticks >= _nextBeat)
                {
                    _nextBeat = _ticks + 300;
                    ProbePlugin.L.LogInfo(string.Format(
                        "[probe] t={0} ticks={1} A={2}(e{3}/n{4}) B={5} C={6} D={7} E={8} F={9} I={10} G={11} H={12} lines={13}",
                        UnityEngine.Time.realtimeSinceStartup.ToString("F0"), _ticks,
                        ProbePlugin.CntText, ProbePlugin.CntTextEmpty, ProbePlugin.CntTextNonEmpty,
                        ProbePlugin.CntPrinted, ProbePlugin.CntAuthor, ProbePlugin.CntRevealTmp,
                        ProbePlugin.CntTmp, ProbePlugin.CntUgui, ProbePlugin.CntRevealUi,
                        ProbePlugin.CntPrintAsync, ProbePlugin.CntEvent, ProbePlugin.Written));
                }
            }
            catch (Exception e)
            {
                if (_ticks % 300 == 0) ProbePlugin.L.LogWarning("[probe] loop: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void OnPrint(PrintTextArgs args)
        {
            ProbePlugin.CntEvent++;
            try
            {
                string text = args == null || args.Text == null ? null : args.Text.ToString();
                string author = args == null || args.AuthorId == null ? null : args.AuthorId.ToString();
                ProbePlugin.Emit("H-EVENT", author, text);
            }
            catch (Exception e) { ProbePlugin.L.LogError("[probe] OnPrint: " + e.Message); }
        }

        /// <summary>点一次标题界面“开始”，成功即停（v1 会重复点 3 次）。</summary>
        private bool TryClickStart()
        {
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>();
                UnityEngine.UI.Button best = null;
                int bestScore = 0;
                foreach (var b in all)
                {
                    if (b == null || b.gameObject == null) continue;
                    var go = b.gameObject;
                    string nm = go.name == null ? "" : go.name.ToString();
                    string path = PathOf(go);
                    string low = (nm + "|" + path).ToLowerInvariant();
                    int score = 0;
                    if (!go.activeInHierarchy) score -= 100;
                    if (!b.interactable) score -= 50;
                    if (low.Contains("newgame") || low.Contains("start") || low.Contains("开始") || low.Contains("新游戏")) score += 100;
                    if (low.Contains("continue")) score -= 40;
                    if (low.Contains("load")) score -= 40;
                    if (low.Contains("settings") || low.Contains("option")) score -= 60;
                    if (low.Contains("quit") || low.Contains("exit")) score -= 80;
                    if (low.Contains("memories") || low.Contains("gallery")) score -= 30;
                    if (low.Contains("title")) score += 10;
                    if (score > bestScore) { bestScore = score; best = b; }
                }
                if (best == null || bestScore <= 0)
                {
                    ProbePlugin.L.LogInfo("[probe] click: no candidate (best=" + bestScore + ")");
                    return false;
                }
                string bpath = PathOf(best.gameObject);
                ProbePlugin.L.LogInfo("[probe] >>> CLICK '" + best.gameObject.name + "' score=" + bestScore + " path=" + bpath);
                ProbePlugin.Append("CLICK\t" + best.gameObject.name + "\tscore=" + bestScore + "\t" + bpath);
                best.onClick.Invoke();
                return true;
            }
            catch (Exception e)
            {
                ProbePlugin.L.LogError("[probe] TryClickStart: " + e.GetType().Name + ": " + e.Message);
                ProbePlugin.Append("CLICK-FAIL\t" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>v9：v8 发现进游戏后卡在 UI_SetPlayerName（SubmitButton interactable=False）。
        /// 直接写入名字并触发 onValueChanged + 点 Submit，验证后续主对话能否开始。</summary>
        private void TryPassNameGate()
        {
            try
            {
                string name = "A11yProbe";
                int filled = 0;
                foreach (var inf in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.InputField>())
                {
                    if (inf == null || inf.gameObject == null) continue;
                    string p = PathOf(inf.gameObject);
                    if (p.IndexOf("SetPlayerName", StringComparison.Ordinal) < 0 && p.IndexOf("VariableInput", StringComparison.Ordinal) < 0) continue;
                    inf.text = name;
                    inf.onValueChanged.Invoke(name);
                    filled++;
                    ProbePlugin.Append("NAME-FILLED\t" + p);
                }
                bool submitted = false;
                foreach (var b in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
                {
                    if (b == null || b.gameObject == null) continue;
                    string nm = b.gameObject.name == null ? "" : b.gameObject.name.ToString();
                    if (nm != "SubmitButton") continue;
                    string p = PathOf(b.gameObject);
                    if (p.IndexOf("SetPlayerName", StringComparison.Ordinal) < 0 && p.IndexOf("VariableInput", StringComparison.Ordinal) < 0) continue;
                    ProbePlugin.Append("NAME-SUBMIT\tinteractable=" + b.interactable + "\t" + p);
                    b.onClick.Invoke();
                    submitted = true;
                }
                ProbePlugin.Append("NAME-GATE\tfilled=" + filled + "\tsubmitted=" + submitted);
            }
            catch (Exception e) { ProbePlugin.Append("NAME-GATE-FAIL\t" + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>诊断：把当前 active 的 Button 名字+路径打一次（判断卡在哪个面板）。</summary>
        private void DumpActiveButtons()
        {
            try
            {
                var seen = new System.Collections.Generic.List<string>();
                int n = 0;
                foreach (var b in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
                {
                    if (b == null || b.gameObject == null || !b.gameObject.activeInHierarchy) continue;
                    string path = PathOf(b.gameObject);
                    if (seen.Contains(path)) continue;
                    seen.Add(path);
                    ProbePlugin.Append("BUTTON-ACTIVE\t" + b.gameObject.name + "\tinteractable=" + b.interactable + "\t" + path);
                    if (++n >= 40) break;
                }
                ProbePlugin.Append("BUTTON-ACTIVE-COUNT\t" + n);
            }
            catch (Exception e) { ProbePlugin.Append("BUTTON-DUMP-FAIL\t" + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>点 Auto：自动播放模式不需要逐句点击，能验证“多行台词是否可连续观测”。</summary>
        private void TryClickAuto()
        {
            try
            {
                foreach (var b in UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>())
                {
                    if (b == null || b.gameObject == null || !b.gameObject.activeInHierarchy || !b.interactable) continue;
                    string nm = b.gameObject.name == null ? "" : b.gameObject.name.ToString();
                    if (nm.IndexOf("Auto", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string path = PathOf(b.gameObject);
                    if (path.IndexOf("TextPrinter", StringComparison.Ordinal) < 0) continue;
                    ProbePlugin.Append("CLICK-AUTO\t" + nm + "\t" + path);
                    b.onClick.Invoke();
                    return;
                }
                ProbePlugin.Append("CLICK-AUTO-MISS");
            }
            catch (Exception e) { ProbePlugin.Append("CLICK-AUTO-FAIL\t" + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>点 Skip：跳过模式会让脚本播放器自己往下走，不需要逐句输入。</summary>
        private bool TryClickSkip()
        {
            try
            {
                var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UI.Button>();
                foreach (var b in all)
                {
                    if (b == null || b.gameObject == null) continue;
                    var go = b.gameObject;
                    string nm = go.name == null ? "" : go.name.ToString();
                    if (nm.IndexOf("Skip", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!go.activeInHierarchy || !b.interactable) continue;
                    string path = PathOf(go);
                    if (path.IndexOf("TextPrinter", StringComparison.Ordinal) < 0) continue;
                    ProbePlugin.L.LogInfo("[probe] >>> CLICK skip '" + nm + "' path=" + path);
                    ProbePlugin.Append("CLICK-SKIP\t" + nm + "\t" + path);
                    b.onClick.Invoke();
                    return true;
                }
                ProbePlugin.Append("CLICK-SKIP-MISS");
            }
            catch (Exception e) { ProbePlugin.Append("CLICK-SKIP-FAIL\t" + e.GetType().Name + ": " + e.Message); }
            return false;
        }

        private static string PathOf(UnityEngine.GameObject go)
        {
            var sb = new StringBuilder();
            var t = go.transform == null ? null : go.transform.parent;
            int guard = 0;
            while (t != null && guard++ < 8) { sb.Insert(0, t.gameObject.name.ToString() + "/"); t = t.parent; }
            return sb.ToString();
        }

        public void OnApplicationQuit() { ProbePlugin.WriteSummary(); }
    }

    [HarmonyPatch(typeof(UITextPrinter), "set_Text")]
    public static class HookText
    {
        [HarmonyPostfix]
        public static void Postfix(UITextPrinter __instance, string value)
        {
            ProbePlugin.CntText++;
            if (string.IsNullOrEmpty(value)) { ProbePlugin.CntTextEmpty++; }
            else
            {
                ProbePlugin.CntTextNonEmpty++;
                if (ProbePlugin.CntTextNonEmpty <= 12) ProbePlugin.LogLen("A", value);
            }
            if (ProbePlugin.CntText <= 6 && __instance != null)
                ProbePlugin.L.LogInfo("[probe] A instance type=" + __instance.GetType().Name);
            ProbePlugin.Emit("A-UITextPrinter", "", value);
        }
    }

    [HarmonyPatch(typeof(Naninovel.UI.RevealableTextPrinterPanel), "set_PrintedText")]
    public static class HookPrinted
    {
        [HarmonyPostfix]
        public static void Postfix(string value)
        {
            ProbePlugin.CntPrinted++;
            if (string.IsNullOrEmpty(value)) ProbePlugin.CntPrintedEmpty++;
            else if (ProbePlugin.CntPrinted <= 12) ProbePlugin.LogLen("B", value);
            ProbePlugin.Emit("B-PrintedText", "", value);
        }
    }

    [HarmonyPatch(typeof(Naninovel.UI.RevealableTextPrinterPanel), "set_AuthorNameText")]
    public static class HookAuthor
    {
        [HarmonyPostfix]
        public static void Postfix(string value)
        {
            ProbePlugin.CntAuthor++;
            if (string.IsNullOrEmpty(value)) ProbePlugin.CntAuthorEmpty++;
            else if (ProbePlugin.CntAuthor <= 12) ProbePlugin.LogLen("C", value);
            ProbePlugin.Emit("C-AuthorName", "", value);
        }
    }

    [HarmonyPatch(typeof(Naninovel.UI.RevealableTMProText), "set_Text")]
    public static class HookRevealTmp
    {
        [HarmonyPostfix]
        public static void Postfix(string value) { ProbePlugin.CntRevealTmp++; ProbePlugin.Emit("D-RevealableTMPro", "", value); }
    }

    // 注意：Harmony 用 attribute 上的类型定位目标，改 PatchAll 的入参没用（v1 踩过）。
    [HarmonyPatch(typeof(TMPro.TMP_Text), "set_text")]
    public static class HookTmp
    {
        [HarmonyPostfix]
        public static void Postfix(string value)
        {
            ProbePlugin.CntTmp++;
            if (ProbePlugin.CntTmp <= 30) ProbePlugin.Emit("E-TMP", "", value);
        }
    }

    [HarmonyPatch(typeof(UnityEngine.UI.Text), "set_text")]
    public static class HookUgui
    {
        [HarmonyPostfix]
        public static void Postfix(UnityEngine.UI.Text __instance, string value)
        {
            ProbePlugin.CntUgui++;
            if (ProbePlugin.CntUgui <= 25 && __instance != null)
                ProbePlugin.L.LogInfo("[probe] F path=" + ProbeBehaviour2.PathOf(__instance.gameObject) + " len=" + (value == null ? -1 : value.Length));
            if (ProbePlugin.CntUgui <= 120) ProbePlugin.Emit("F-uGUI", "", value);

            // J：监控“对话框里的”uGUI Text 到底叫什么类型（v2 的 D/E/I 全 0，需要确认实际 widget 类）
            try
            {
                if (__instance != null && __instance.gameObject != null)
                {
                    string p = ProbeBehaviour2.PathOf(__instance.gameObject);
                    if (p.IndexOf("PrinterPanel", StringComparison.Ordinal) >= 0 ||
                        p.IndexOf("TextPrinter", StringComparison.Ordinal) >= 0)
                    {
                        ProbePlugin.CntDialogueUgui++;
                        if (ProbePlugin.CntDialogueUgui <= 20)
                            ProbePlugin.L.LogInfo("[probe] J dialogue-uGUI type=" + __instance.GetType().Name +
                                " len=" + (value == null ? -1 : value.Length) + " path=" + p);
                        ProbePlugin.Emit("J-DialogueText", "", value);
                    }
                }
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(Naninovel.UI.RevealableUIText), "set_Text")]
    public static class HookRevealUi
    {
        [HarmonyPostfix]
        public static void Postfix(string value)
        {
            ProbePlugin.CntRevealUi++;
            if (ProbePlugin.CntRevealUi <= 12) ProbePlugin.LogLen("I", value);
            ProbePlugin.Emit("I-RevealableUIText", "", value);
        }
    }

    [HarmonyPatch(typeof(TextPrinterManager), "PrintTextAsync")]
    public static class HookPrintAsync
    {
        [HarmonyPrefix]
        public static void Prefix(string printerId, string text, string authorId, float speed)
        {
            ProbePlugin.CntPrintAsync++;
            ProbePlugin.Emit("G-PrintTextAsync", authorId, text);
            if (!ProbePlugin.Printers.ContainsKey(printerId ?? "<null>")) ProbePlugin.Printers[printerId ?? "<null>"] = 0;
            ProbePlugin.Printers[printerId ?? "<null>"]++;
            if (ProbePlugin.CntPrintAsync <= 3)
                ProbePlugin.L.LogInfo("[probe] G fired: printer=" + printerId + " author=" + authorId + " speed=" + speed);
        }
    }

    internal static class ProbeBehaviour2
    {
        internal static string PathOf(UnityEngine.GameObject go)
        {
            if (go == null) return "";
            var sb = new StringBuilder();
            var t = go.transform == null ? null : go.transform.parent;
            int guard = 0;
            while (t != null && guard++ < 8) { sb.Insert(0, t.gameObject.name.ToString() + "/"); t = t.parent; }
            return sb.ToString();
        }
    }
}
