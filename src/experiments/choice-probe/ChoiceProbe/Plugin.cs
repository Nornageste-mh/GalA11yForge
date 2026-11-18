using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace GalA11y.ChoiceProbe
{
    /// <summary>
    /// task-28 / R23 选项朗读运行期探针（只读观测，不改游戏行为）。
    ///
    /// 要回答的问题：
    ///   1) 选项文本在 ChoiceAsset 的哪个字段、什么类型、Choice() 调用时是否已可用；
    ///   2) 画面上的选项文本（_textB1..3）与 ChoiceAsset.ChoiceN.Sc 是否逐字相同、顺序是否 1:1；
    ///   3) 文本是在 Choice() 内同步写入，还是之后由协程/按钮创建时写入；
    ///   4) 不引用 Assembly-CSharp 的生成器模板（object[] __args + 反射）能否读到同样的文本；
    ///   5) 选项按钮点击与 ChoiceN 的对应关系、选项模式开始/结束时机。
    ///
    /// 隐私策略：每个选项只采样前 3 个 Choice 事件的短文本（≤40 字）；其余只落长度/FNV。
    /// </summary>
    [BepInPlugin(Guid, "GalA11y Choice Probe", "0.0.1.0")]
    public class ChoiceProbePlugin : BaseUnityPlugin
    {
        public const string Guid = "gala11y.choice.probe";

        private const int SampleChoiceEvents = 3;
        private const int SampleChars = 40;
        private const int MaxSetTextPerWindow = 24;

        private static StreamWriter _ev;
        private static readonly object Gate = new object();
        private static string _dir;
        private static string _statePath;
        private static ManualLogSource _log;
        private static readonly List<string> PatchResults = new List<string>();

        private static int _choiceSeq;
        private static float _choiceAt = -1f;
        private static int _choiceFrame;
        private static int _setTextInWindow;
        private static int _setTextTotal;
        private static string _lastChoiceSummary = "";

        private static int _beginModeCount, _endModeCount, _selectCount;

        private static ChoiceProbePlugin _inst;
        private static UIText _ui;
        private static DateTime _cmdStamp;
        private static long _cmdPos;
        private static float _nextCursorLog;
        private static bool _inputDumped;

        private void Awake()
        {
            _inst = this;
            _log = Logger;
            _dir = Path.Combine(Paths.GameRootPath, "BepInEx", "choice-probe");
            Directory.CreateDirectory(_dir);
            _ev = new StreamWriter(Path.Combine(_dir, "events.tsv"), false, new UTF8Encoding(false));
            _ev.AutoFlush = true;
            _statePath = Path.Combine(_dir, "state.tsv");

            LogLine("=== GalA11y Choice Probe 0.0.1 ===");
            LogLine("utc=" + DateTime.UtcNow.ToString("O"));
            LogLine("game=" + Application.productName + " version=" + Application.version + " unity=" + Application.unityVersion);
            LogLine("screen=" + Screen.width + "x" + Screen.height + " root=" + Paths.GameRootPath);
            LogLine("policy=choice samples: first " + SampleChoiceEvents + " events, <=" + SampleChars + " chars/option");

            DumpSchema();

            var harmony = new Harmony(Guid);
            // 有类型引用（探针用）：验证 ChoiceAsset/UIText 字段语义
            Patch(harmony, typeof(UIText), "Choice", "Choice_Typed_Postfix", null, true);
            // 生成器可用形态：不声明任何游戏类型，只用 __args/__instance + 反射
            Patch(harmony, typeof(UIText), "Choice", "Choice_Untyped_Postfix", null, true);
            Patch(harmony, typeof(TMP_Text), "set_text", "SetText_Prefix", new[] { typeof(string) }, false);
            Patch(harmony, typeof(UIText), "PushButton1", "PushButton1_Prefix", null, false);
            Patch(harmony, typeof(UIText), "PushButton2", "PushButton2_Prefix", null, false);
            Patch(harmony, typeof(UIText), "PushButton3", "PushButton3_Prefix", null, false);
            Patch(harmony, typeof(UIText), "ChoiceCo", "ChoiceCo_Prefix", null, false);
            Patch(harmony, typeof(VirtualCursorManager), "BeginChoiceMode", "BeginChoiceMode_Prefix", null, false);
            Patch(harmony, typeof(VirtualCursorManager), "EndChoiceMode", "EndChoiceMode_Prefix", null, false);
            Patch(harmony, typeof(Button), "Press", "ButtonPress_Prefix", null, false);

            foreach (var p in PatchResults) LogLine("PATCH\t" + p);
            WriteState("startup");
            StartCoroutine(Heartbeat());
        }

        private static void Patch(Harmony h, Type type, string method, string prefixOrPostfix, Type[] args, bool postfix)
        {
            string target = type.FullName + "::" + method;
            try
            {
                var m = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                if (m == null)
                {
                    PatchResults.Add("FAIL\t" + target + "\treason=method-not-found");
                    return;
                }
                var pm = new HarmonyMethod(typeof(ChoiceProbePlugin).GetMethod(prefixOrPostfix,
                    BindingFlags.Static | BindingFlags.NonPublic));
                if (postfix) h.Patch(m, postfix: pm);
                else h.Patch(m, prefix: pm);
                PatchResults.Add("OK\t" + target + "\tparams=" + m.GetParameters().Length + (postfix ? "\tpostfix" : "\tprefix"));
            }
            catch (Exception e)
            {
                PatchResults.Add("FAIL\t" + target + "\treason=" + e.GetType().Name + ": " + Trim(e.Message, 200));
            }
        }

        // ---------------- schema ----------------

        private static void DumpSchema()
        {
            var sb = new StringBuilder();
            sb.Append("SCHEMA\tChoiceAsset");
            foreach (var f in typeof(ChoiceAsset).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                sb.Append("|").Append(f.Name).Append(":").Append(f.FieldType.FullName);
            Ev("SCHEMA", sb.ToString());

            sb = new StringBuilder("SCHEMA\tChoiceAsset.QuadLang");
            foreach (var f in typeof(ChoiceAsset.QuadLang).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                sb.Append("|").Append(f.Name).Append(":").Append(f.FieldType.FullName);
            Ev("SCHEMA", sb.ToString());

            sb = new StringBuilder("SCHEMA\tUIText.choice-fields");
            foreach (var f in typeof(UIText).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                string n = f.Name;
                if (n.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("choice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("lang", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    sb.Append("|").Append(n).Append(":").Append(f.FieldType.Name);
                }
            }
            Ev("SCHEMA", sb.ToString());
        }

        // ---------------- hooks ----------------

        private static void Choice_Typed_Postfix(UIText __instance, ChoiceAsset cs, int hideMask)
        {
            try
            {
                _ui = __instance;
                int seq = ++_choiceSeq;
                _choiceAt = Time.realtimeSinceStartup;
                _choiceFrame = Time.frameCount;
                _setTextInWindow = 0;

                var sb = new StringBuilder();
                sb.Append("seq=").Append(seq);
                sb.Append("|asset=").Append(cs != null ? Trim(cs.name, 60) : "(null)");
                if (cs == null) { Ev("CHOICE", sb.ToString()); return; }

                sb.Append("|num=").Append(cs.ChoiceNum);
                sb.Append("|count=").Append(cs.ChoiceCount);
                sb.Append("|hideMask=").Append(hideMask);
                sb.Append("|uiChoiceNum=").Append(ReadInt(__instance, "ChoiceNum"));
                sb.Append("|isChoicing=").Append(ReadBool(__instance, "isChoicing"));
                bool sample = seq <= SampleChoiceEvents;

                AppendOption(sb, 1, cs.Choice1, __instance, sample);
                AppendOption(sb, 2, cs.Choice2, __instance, sample);
                AppendOption(sb, 3, cs.Choice3, __instance, sample);

                _lastChoiceSummary = "seq=" + seq + "|asset=" + Trim(cs.name, 60) + "|count=" + cs.ChoiceCount;
                Ev("CHOICE", sb.ToString());
                DumpGeometry(__instance, seq);
            }
            catch (Exception e)
            {
                Ev("CHOICE", "seq=" + _choiceSeq + "|error=" + e.GetType().Name + ":" + Trim(e.Message, 160));
            }
        }

        private static void Choice_Untyped_Postfix(object[] __args, object __instance)
        {
            try
            {
                int argc = __args == null ? -1 : __args.Length;
                object cs = argc >= 1 ? __args[0] : null;
                object q1 = cs == null ? null : RefField(cs, "Choice1");
                string sc1 = q1 == null ? null : (string)RefField(q1, "Sc");
                string ui1 = ReadTmpText(__instance, "_textB1");
                string asset = cs == null ? "(null)" : SafeUnityName(cs);
                Ev("CHOICE_UNTYPED",
                    "args=" + argc + "|arg0=" + (cs == null ? "null" : cs.GetType().FullName) +
                    "|inst=" + (__instance == null ? "null" : __instance.GetType().FullName) +
                    "|asset=" + Trim(asset, 60) +
                    "|sc1=" + Len(sc1) + ":" + Fnv(sc1).ToString("x8") +
                    "|ui1=" + Len(ui1) + ":" + Fnv(ui1).ToString("x8") +
                    "|equal=" + (string.Equals(sc1, ui1, StringComparison.Ordinal) ? "1" : "0"));
            }
            catch (Exception e)
            {
                Ev("CHOICE_UNTYPED", "error=" + e.GetType().Name + ":" + Trim(e.Message, 160));
            }
        }

        private static void DumpGeometry(UIText inst, int seq)
        {
            try
            {
                var canvas = inst.GetComponentInParent<Canvas>();
                Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                var sb = new StringBuilder();
                sb.Append("seq=").Append(seq);
                sb.Append("|screen=").Append(Screen.width).Append('x').Append(Screen.height);
                sb.Append("|canvas=").Append(canvas != null ? canvas.name + "/" + canvas.renderMode : "(none)");
                for (int n = 1; n <= 3; n++)
                {
                    var go = RefField(inst, "Button" + n) as GameObject;
                    var txt = RefField(inst, "_textB" + n) as TMP_Text;
                    sb.Append("|G").Append(n).Append(":btn=");
                    if (go != null)
                    {
                        var rt = go.GetComponent<RectTransform>();
                        sb.Append(GoPath(go));
                        sb.Append(",active=").Append(go.activeInHierarchy ? 1 : 0);
                        sb.Append(",rect=").Append(rt != null ? RectStr(rt, cam) : "na");
                        var cg = go.GetComponent<CanvasGroup>();
                        sb.Append(",cg=").Append(cg != null ? "inter=" + (cg.interactable ? 1 : 0) + ";blocks=" + (cg.blocksRaycasts ? 1 : 0) : "na");
                        var btn = go.GetComponent<Button>();
                        sb.Append(",button=").Append(btn != null ? "inter=" + (btn.interactable ? 1 : 0) + ";ray=" + (btn.targetGraphic != null && btn.targetGraphic.raycastTarget ? 1 : 0) : "na");
                        var img = go.GetComponent<Image>();
                        sb.Append(",img=").Append(img != null ? "ray=" + (img.raycastTarget ? 1 : 0) + ";en=" + (img.enabled ? 1 : 0) : "na");
                    }
                    else sb.Append("(null)");
                    if (txt != null)
                    {
                        var rt2 = txt.rectTransform;
                        sb.Append(",text=").Append(GoPath(txt.gameObject));
                        sb.Append(",trect=").Append(RectStr(rt2, cam));
                        sb.Append(",tray=").Append(txt.raycastTarget ? 1 : 0);
                    }
                }
                Ev("GEOMETRY", sb.ToString());
            }
            catch (Exception e)
            {
                Ev("GEOMETRY", "error=" + e.GetType().Name + ":" + Trim(e.Message, 160));
            }
        }

        private static string RectStr(RectTransform rt, Camera cam)
        {
            if (rt == null) return "na";
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            var p0 = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
            var p2 = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            return "(" + p0.x.ToString("F0") + "," + p0.y.ToString("F0") + ")-(" + p2.x.ToString("F0") + "," + p2.y.ToString("F0") + ")";
        }

        private static string GoPath(GameObject go)
        {
            if (go == null) return "(null)";
            string p = go.name;
            var t = go.transform;
            for (int i = 0; i < 3 && t != null && t.parent != null; i++)
            {
                t = t.parent;
                p = t.name + "/" + p;
            }
            return p;
        }

        private void Update()
        {
            try
            {
                PollCmd();
                if (Time.realtimeSinceStartup >= _nextCursorLog)
                {
                    _nextCursorLog = Time.realtimeSinceStartup + 1f;
                    var ui = _ui != null ? _ui : UnityEngine.Object.FindObjectOfType<UIText>();
                    if (ui != null && ReadBool(ui, "isChoicing")) Ev("CURSOR", CursorState());
                }
                if (!Input.GetMouseButtonDown(0)) return;
                var pos = Input.mousePosition;
                var es = EventSystem.current;
                string hits = "";
                string over = "na";
                if (es != null)
                {
                    over = es.IsPointerOverGameObject() ? "1" : "0";
                    var ped = new PointerEventData(es) { position = pos };
                    var list = new List<RaycastResult>();
                    es.RaycastAll(ped, list);
                    hits = string.Join("<", list.Take(5).Select(r => r.gameObject.name + "@" + (r.gameObject.transform.parent != null ? r.gameObject.transform.parent.name : "-")).ToArray());
                }
                Ev("MOUSEDOWN", "pos=" + pos.x.ToString("F0") + "," + pos.y.ToString("F0") + "|over=" + over + "|hits=" + Trim(hits, 160));
            }
            catch { }
        }

        private static void ButtonPress_Prefix(Button __instance)
        {
            Ev("BTNPRESS", "btn=" + GoPath(__instance.gameObject) + "|inter=" + (__instance.interactable ? 1 : 0) + "|isChoicing=" + _choiceSeq);
        }

        private static void AppendOption(StringBuilder sb, int n, ChoiceAsset.QuadLang q, UIText inst, bool sample)
        {
            string ui = ReadTmpText(inst, "_textB" + n);
            bool active = ReadActive(inst, "Button" + n);
            sb.Append("|c").Append(n)
              .Append("=ja:").Append(Len(q.Ja)).Append(':').Append(Fnv(q.Ja).ToString("x8"))
              .Append(",sc:").Append(Len(q.Sc)).Append(':').Append(Fnv(q.Sc).ToString("x8"))
              .Append(",en:").Append(Len(q.En)).Append(':').Append(Fnv(q.En).ToString("x8"))
              .Append(",tc:").Append(Len(q.Tc)).Append(':').Append(Fnv(q.Tc).ToString("x8"));
            sb.Append("|u").Append(n).Append('=').Append(Len(ui)).Append(':').Append(Fnv(ui).ToString("x8"));
            sb.Append("|btn").Append(n).Append('=').Append(active ? "1" : "0");
            sb.Append("|eq").Append(n).Append('=').Append(string.Equals(q.Sc, ui, StringComparison.Ordinal) ? "1" : "0");
            if (sample)
            {
                sb.Append("|sc").Append(n).Append('=').Append(Sanitize(q.Sc, SampleChars));
                sb.Append("|ui").Append(n).Append('=').Append(Sanitize(ui, SampleChars));
            }
        }

        private static void SetText_Prefix(TMP_Text __instance, string value)
        {
            _setTextTotal++;
            if (_choiceAt < 0f) return;
            float dt = Time.realtimeSinceStartup - _choiceAt;
            if (dt > 2.0f || _setTextInWindow >= MaxSetTextPerWindow) return;
            _setTextInWindow++;
            Ev("SETTEXT",
                "dt=" + dt.ToString("F3") + "|df=" + (Time.frameCount - _choiceFrame) +
                "|target=" + Trim(TargetPath(__instance), 80) +
                "|len=" + Len(value) + "|hash=" + Fnv(value).ToString("x8") +
                "|stack=" + ShortStack() +
                "|text=" + Sanitize(value, SampleChars));
        }

        private static void PushButton1_Prefix(UIText __instance) { OnSelect(1, __instance); }
        private static void PushButton2_Prefix(UIText __instance) { OnSelect(2, __instance); }
        private static void PushButton3_Prefix(UIText __instance) { OnSelect(3, __instance); }

        private static void OnSelect(int n, UIText inst)
        {
            _selectCount++;
            Ev("SELECT",
                "n=" + n + "|at=" + Time.realtimeSinceStartup.ToString("F2") +
                "|dtChoice=" + (_choiceAt < 0f ? "na" : (Time.realtimeSinceStartup - _choiceAt).ToString("F2")) +
                "|isChoicing=" + ReadBool(inst, "isChoicing") +
                "|uiChoiceNum=" + ReadInt(inst, "ChoiceNum") +
                "|last=" + _lastChoiceSummary);
        }

        private static void ChoiceCo_Prefix(UIText __instance)
        {
            Ev("CHOICECO",
                "at=" + Time.realtimeSinceStartup.ToString("F2") +
                "|dtChoice=" + (_choiceAt < 0f ? "na" : (Time.realtimeSinceStartup - _choiceAt).ToString("F3")) +
                "|seq=" + _choiceSeq);
        }

        private static void BeginChoiceMode_Prefix()
        {
            _beginModeCount++;
            if (!_inputDumped) { _inputDumped = true; try { DumpInput(); } catch { } }
            Ev("BEGINCHOICE",
                "at=" + Time.realtimeSinceStartup.ToString("F2") +
                "|dtChoice=" + (_choiceAt < 0f ? "na" : (Time.realtimeSinceStartup - _choiceAt).ToString("F3")) +
                "|seq=" + _choiceSeq + "|last=" + _lastChoiceSummary);
        }

        private static void EndChoiceMode_Prefix()
        {
            _endModeCount++;
            Ev("ENDCHOICE",
                "at=" + Time.realtimeSinceStartup.ToString("F2") +
                "|dtChoice=" + (_choiceAt < 0f ? "na" : (Time.realtimeSinceStartup - _choiceAt).ToString("F3")) +
                "|seq=" + _choiceSeq);
        }

        // ---------------- cmd / input injection ----------------

        private void PollCmd()
        {
            try
            {
                if (_dir == null) return;
                string path = Path.Combine(_dir, "cmd.txt");
                if (!File.Exists(path)) return;
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _cmdStamp) return;
                _cmdStamp = stamp;
                string all;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    all = sr.ReadToEnd();
                if (all.Length < _cmdPos) _cmdPos = 0;
                string chunk = all.Substring((int)_cmdPos);
                _cmdPos = all.Length;
                foreach (var raw in chunk.Split('\n'))
                {
                    string s = raw.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    try { DoCmd(s); }
                    catch (Exception e) { Ev("CMDERR", s + "|" + e.GetType().Name + ":" + Trim(e.Message, 140)); }
                }
            }
            catch { }
        }

        private static void DoCmd(string s)
        {
            int sp = s.IndexOf(' ');
            string verb = (sp < 0 ? s : s.Substring(0, sp)).ToLowerInvariant();
            string arg = sp < 0 ? "" : s.Substring(sp + 1).Trim().ToLowerInvariant();
            switch (verb)
            {
                case "geom":
                    {
                        var ui = _ui != null ? _ui : UnityEngine.Object.FindObjectOfType<UIText>();
                        if (ui != null) DumpGeometry(ui, _choiceSeq);
                        Ev("CMD", "geom|ui=" + (ui != null));
                        break;
                    }
                case "cursor":
                    Ev("CURSOR", CursorState());
                    break;
                case "dumpinput":
                    DumpInput();
                    break;
                case "pad":
                    InjectPad(arg);
                    break;
                case "key":
                    InjectKey(arg);
                    break;
                case "click":
                    {
                        int idx = 1;
                        if (arg.Length > 0) int.TryParse(arg, out idx);
                        var ui = _ui != null ? _ui : UnityEngine.Object.FindObjectOfType<UIText>();
                        var go = ui != null ? RefField(ui, "Button" + idx) as GameObject : null;
                        var btn = go != null ? go.GetComponent<Button>() : null;
                        Ev("INVOKE", "n=" + idx + "|btn=" + (btn != null) + "|ui=" + (ui != null));
                        if (btn != null) btn.onClick.Invoke();
                        break;
                    }
                default:
                    Ev("CMD", "unknown=" + Trim(s, 60));
                    break;
            }
        }

        private static void InjectPad(string name)
        {
            Gamepad pad = Gamepad.current;
            bool added = false;
            if (pad == null) { pad = InputSystem.AddDevice<Gamepad>(); added = true; }
            GamepadButton b;
            switch (name)
            {
                case "down": b = GamepadButton.DpadDown; break;
                case "up": b = GamepadButton.DpadUp; break;
                case "left": b = GamepadButton.DpadLeft; break;
                case "right": b = GamepadButton.DpadRight; break;
                case "submit": case "south": case "a": b = GamepadButton.South; break;
                case "cancel": case "east": case "b": b = GamepadButton.East; break;
                default: Ev("PADINJECT", "unknown=" + Trim(name, 20)); return;
            }
            InputSystem.QueueStateEvent(pad, new GamepadState().WithButton(b));
            Ev("PADINJECT", "button=" + b + "|pad=" + pad.deviceId + "|added=" + added + "|" + CursorState());
            _inst.StartCoroutine(ReleasePad(pad, b, 0.18f));
        }

        private static IEnumerator ReleasePad(Gamepad pad, GamepadButton b, float t)
        {
            yield return new WaitForSecondsRealtime(t);
            InputSystem.QueueStateEvent(pad, new GamepadState());
        }

        private static void InjectKey(string name)
        {
            var kb = Keyboard.current;
            if (kb == null) { Ev("KEYINJECT", "no-keyboard"); return; }
            Key k;
            switch (name)
            {
                case "enter": k = Key.Enter; break;
                case "space": k = Key.Space; break;
                case "down": k = Key.DownArrow; break;
                case "up": k = Key.UpArrow; break;
                case "left": k = Key.LeftArrow; break;
                case "right": k = Key.RightArrow; break;
                case "esc": k = Key.Escape; break;
                default: Ev("KEYINJECT", "unknown=" + Trim(name, 20)); return;
            }
            InputSystem.QueueStateEvent(kb, new KeyboardState(k));
            Ev("KEYINJECT", "key=" + k + "|" + CursorState());
            _inst.StartCoroutine(ReleaseKey(kb, k, 0.15f));
        }

        private static IEnumerator ReleaseKey(Keyboard kb, Key k, float t)
        {
            yield return new WaitForSecondsRealtime(t);
            InputSystem.QueueStateEvent(kb, new KeyboardState());
        }

        private static void DumpInput()
        {
            var sb = new StringBuilder("INPUT");
            try
            {
                var pi = UnityEngine.Object.FindObjectOfType<PlayerInput>();
                if (pi != null && pi.actions != null)
                {
                    sb.Append("|playerInput=").Append(pi.name).Append("|scheme=").Append(pi.currentControlScheme);
                    foreach (var a in pi.actions)
                    {
                        var binds = new List<string>();
                        foreach (var b in a.bindings) binds.Add(b.path + (string.IsNullOrEmpty(b.name) ? "" : "#" + b.name));
                        sb.Append("\nACT ").Append(a.name).Append(" [").Append(a.type).Append("] = ").Append(string.Join(",", binds));
                    }
                }
                else sb.Append("|playerInput=none");

                var es = EventSystem.current;
                if (es != null && es.currentInputModule is InputSystemUIInputModule uiMod)
                {
                    sb.Append("\nMODULE ").Append(uiMod.GetType().FullName);
                    AppendActionRef(sb, "point", uiMod.point);
                    AppendActionRef(sb, "leftClick", uiMod.leftClick);
                    AppendActionRef(sb, "move", uiMod.move);
                    AppendActionRef(sb, "submit", uiMod.submit);
                    AppendActionRef(sb, "cancel", uiMod.cancel);
                }
                else sb.Append("\nMODULE na");

                sb.Append("\nVCM ").Append(CursorState());
                var pad = Gamepad.current;
                sb.Append("\nDEV gamepad=").Append(pad != null ? pad.deviceId + "/" + pad.name : "none");
                sb.Append("|keyboard=").Append(Keyboard.current != null ? Keyboard.current.deviceId.ToString() : "none");
                sb.Append("|mouse=").Append(Mouse.current != null ? Mouse.current.deviceId.ToString() : "none");
            }
            catch (Exception e) { sb.Append("|error=").Append(e.GetType().Name).Append(':').Append(Trim(e.Message, 160)); }
            Ev("INPUT", sb.ToString());
        }

        private static void AppendActionRef(StringBuilder sb, string label, InputActionReference r)
        {
            sb.Append("\nUI ").Append(label).Append("=");
            if (r == null || r.action == null) { sb.Append("na"); return; }
            var binds = new List<string>();
            foreach (var b in r.action.bindings) binds.Add(b.path);
            sb.Append(r.action.name).Append(" [").Append(r.action.type).Append("] = ").Append(string.Join(",", binds));
        }

        private static string CursorState()
        {
            try
            {
                var vcm = UnityEngine.Object.FindObjectOfType<VirtualCursorManager>();
                var sb = new StringBuilder();
                sb.Append("vcm=").Append(vcm != null ? vcm.name : "na");
                if (vcm != null)
                {
                    sb.Append("|mode=").Append(Member(vcm, "currentMode"));
                    sb.Append("|choiceBlock=").Append(Member(vcm, "choiceBlock"));
                    sb.Append("|modeBeforeChoice=").Append(Member(vcm, "modeBeforeChoice"));
                    sb.Append("|pointerBlocked=").Append(Member(vcm, "pointerBlocked"));
                    sb.Append("|hidden=").Append(Member(vcm, "cursorHiddenInBlock"));
                    sb.Append("|modal=").Append(Member(vcm, "modalBlock"));
                }
                var es = EventSystem.current;
                var sel = es != null ? es.currentSelectedGameObject : null;
                sb.Append("|selected=").Append(sel != null ? GoPath(sel) : "na");
                var ui = _ui != null ? _ui : UnityEngine.Object.FindObjectOfType<UIText>();
                sb.Append("|isChoicing=").Append(ui != null ? ReadBool(ui, "isChoicing").ToString() : "na");
                sb.Append("|choiceNum=").Append(ui != null ? ReadInt(ui, "ChoiceNum").ToString() : "na");
                return sb.ToString();
            }
            catch (Exception e) { return "err=" + e.GetType().Name + ":" + Trim(e.Message, 120); }
        }

        private static string Member(object o, string name)
        {
            if (o == null) return "na";
            var t = o.GetType();
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return Convert.ToString(f.GetValue(o));
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p != null ? Convert.ToString(p.GetValue(o)) : "?";
        }

        // ---------------- state ----------------

        private IEnumerator Heartbeat()
        {
            while (true)
            {
                yield return new WaitForSeconds(5f);
                WriteState("heartbeat");
            }
        }

        private static void WriteState(string tag)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("key\tvalue");
                sb.AppendLine("tag\t" + tag);
                sb.AppendLine("utc\t" + DateTime.UtcNow.ToString("O"));
                sb.AppendLine("uptime_s\t" + Time.realtimeSinceStartup.ToString("F1"));
                sb.AppendLine("frames\t" + Time.frameCount);
                sb.AppendLine("choice_events\t" + _choiceSeq);
                sb.AppendLine("settext_total\t" + _setTextTotal);
                sb.AppendLine("select_events\t" + _selectCount);
                sb.AppendLine("begin_choice_mode\t" + _beginModeCount);
                sb.AppendLine("end_choice_mode\t" + _endModeCount);
                sb.AppendLine("last_choice\t" + _lastChoiceSummary);
                File.WriteAllText(_statePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private void OnApplicationQuit()
        {
            Ev("QUIT", "uptime=" + Time.realtimeSinceStartup.ToString("F1") + "|frames=" + Time.frameCount);
            WriteState("quit");
            try { _ev.Flush(); _ev.Dispose(); } catch { }
        }

        // ---------------- helpers ----------------

        private static object RefField(object obj, string name)
        {
            if (obj == null) return null;
            var f = obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f == null ? null : f.GetValue(obj);
        }

        private static string SafeUnityName(object obj)
        {
            var uo = obj as UnityEngine.Object;
            return uo != null ? uo.name : "(non-unity)";
        }

        private static string ReadTmpText(object inst, string field)
        {
            object v = RefField(inst, field);
            var t = v as TMP_Text;
            return t != null ? t.text : null;
        }

        private static int ReadInt(object inst, string field)
        {
            object v = RefField(inst, field);
            return v is int i ? i : -1;
        }

        private static bool ReadBool(object inst, string field)
        {
            object v = RefField(inst, field);
            return v is bool b && b;
        }

        private static bool ReadActive(object inst, string field)
        {
            object v = RefField(inst, field);
            var go = v as GameObject;
            return go != null && go.activeInHierarchy;
        }

        private static void Ev(string kind, string detail)
        {
            if (_ev == null) return;
            try
            {
                lock (Gate) _ev.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "\t" + kind + "\t" + detail);
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
                    if (tn == "ChoiceProbePlugin") continue;
                    parts.Add(tn + "." + m.Name);
                    if (parts.Count >= 4) break;
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
                for (int i = 0; i < 3 && t != null && t.parent != null; i++)
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

        private static int Len(string s) => s == null ? 0 : s.Length;

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


