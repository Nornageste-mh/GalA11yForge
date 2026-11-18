using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GalA11yForge.ExternalReader.Capture;
using GalA11yForge.ExternalReader.Native;
using GalA11yForge.ExternalReader.Speech;

namespace GalA11yForge.ExternalReader.Runtime;

/// <summary>
/// 主循环：附着 → 轮询（UIA / Win32 / OCR）→ 稳定闸门 → 朗读。
///
/// 设计原则（来自 R9 §7 与本次原型目标）：
///   * 只读：不注入、不发消息给游戏、不写游戏目录；
///   * 永不阻塞在朗读上：SAPI 用异步，路由在独立线程；
///   * 每轮都有超时预算：UIA 单次 Poll 有节点数/时间上限，OCR 有间隔；
///   * 一切可观测：每秒多少轮、UIA 中位耗时、丢了多少重复句，都落到 jsonl。
/// </summary>
public sealed class ReaderEngine : IDisposable
{
    private readonly Options _o;
    private readonly Log _log;
    private readonly GateStats _stats = new();

    private WindowTarget? _target;
    private UiaSource? _uia;
    private Win32Source? _win32;
    private OcrSource? _ocr;
    private SpeechRouter? _speech;
    private HotkeyListener? _hotkeys;
    private SpeechGate _gate;

    private long _polls, _uiaPolls, _win32Polls, _uiaErrors, _uiaFatals, _nodesSeen;
    private long _lastTextMs = -1_000_000;
    private long _lastSpokenMs = -1_000_000;
    private string _lastSpoken = string.Empty;
    private long _ocrFrames, _ocrChars, _ocrSkips, _ocrErrors;
    private long _chromeSkipped, _skipTextHits;
    private readonly List<double> _uiaMs = new();
    private readonly List<double> _win32Ms = new();
    private readonly List<double> _ocrCaptureMs = new();
    private readonly List<double> _ocrRecognizeMs = new();
    private bool _paused;
    private bool _quit;

    public ReaderEngine(Options options, Log log)
    {
        _o = options;
        _log = log;
        _gate = new SpeechGate(options, _stats);
    }

    public int Run()
    {
        // 附着重试：窗口刚出现时偶发“枚举不到”（实测 e2e 首跑，1.5s 后仍失败），
        // 真实用户也会先开着游戏再启动读屏器。默认等 8 秒，每 250ms 再试一次。
        var attachDeadline = _log.ElapsedMs + Math.Max(0, _o.AttachTimeoutMs);
        WindowTarget? target = null;
        var attempts = 0;
        while (true)
        {
            attempts++;
            target = WindowLocator.Find(_o.Hwnd, _o.Pid, _o.Title, _o.Exe, _o.IncludeInvisible, _log);
            if (target is not null) break;
            if (_log.ElapsedMs >= attachDeadline) break;
            _log.Evt("attach.retry", null, ("attempt", attempts), ("waitedMs", _log.ElapsedMs));
            Thread.Sleep(250);
        }

        if (target is null)
        {
            _log.Evt("attach.failed", "找不到匹配的可见窗口（--pid / --title / --exe）", ("attempts", attempts));
            return 2;
        }
        _target = target;
        _log.Evt("attach.ok", null,
            ("hwnd", $"0x{target.Hwnd.ToInt64():X}"), ("pid", target.Pid), ("exe", target.ProcessName),
            ("class", target.ClassName), ("title", target.Title), ("bounds", target.Bounds.ToString()),
            ("client", target.ClientBounds.ToString()),
            ("foreground", Win32.GetForegroundWindow() == target.Hwnd));

        if (_o.UseUia)
        {
            try
            {
                _uia = new UiaSource(target, _o, _log);
                _log.Evt("uia.init", null, ("ok", true), ("maxNodes", _o.MaxNodes), ("pollMs", _o.PollMs));
            }
            catch (Exception ex)
            {
                _log.Evt("uia.init", null, ("ok", false), ("error", ex.GetType().Name + ": " + ex.Message));
            }
        }
        if (_o.UseWin32 || _uia is null)
        {
            _win32 = new Win32Source(target, _o);
            _log.Evt("win32.init", null, ("enabled", true));
        }

        if (_o.Ocr)
        {
            _ocr = new OcrSource(target, _o, _log);
            if (_ocr.TryInit(out var err))
            {
                _log.Evt("ocr.ready", "OCR 兜底已启用（实验性）", ("lang", _o.OcrLang),
                    ("region", _o.Region ?? "auto"), ("intervalMs", _o.OcrIntervalMs), ("capture", _o.Capture),
                    ("resolved", _ocr.ResolveRegion().ToString()));
            }
            else
            {
                _log.Evt("ocr.unavailable", err);
                _ocr.Dispose();
                _ocr = null;
            }
        }

        _speech = new SpeechRouter(_o, _log);
        if (_o.Hotkeys)
        {
            _hotkeys = new HotkeyListener();
            _hotkeys.Start(_log);
        }

        _log.Evt("run.start", "开始只读轮询（Ctrl+C 也可退出）",
            ("speech", _speech.BackendName), ("pollMs", _o.PollMs), ("settleMs", _o.SettleMs),
            ("repeatWindowMs", _o.RepeatWindowMs), ("maxLen", _o.MaxLen), ("seconds", _o.Seconds));

        long deadline = _o.Seconds > 0 ? _o.Seconds * 1000L : long.MaxValue;
        long nextUia = 0, nextWin32 = 0, nextOcr = 0, nextBeat = 0, nextAlive = 0, nextFatalLog = 0;
        var ocrInterval = Math.Max(200, _o.OcrIntervalMs);

        try
        {
            while (true)
            {
                long now = _log.ElapsedMs;
                if (_quit) break;
                if (now >= deadline)
                {
                    _log.Evt("run.deadline", null, ("seconds", _o.Seconds));
                    break;
                }

                HandleInput();

                if (now >= nextAlive)
                {
                    nextAlive = now + 500;
                    if (_target is not null)
                    {
                        bool alive = WindowLocator.StillAlive(_target);
                        var why = alive ? "iswindow" : WindowLocator.Recheck(_target).Reason;
                        if (!alive) alive = WindowLocator.Recheck(_target).Alive;
                        if (_o.Verbose)
                            _log.Evt("alive.check", null, ("alive", alive), ("why", why),
                                ("hwnd", $"0x{_target.Hwnd.ToInt64():X}"));
                        if (!alive)
                        {
                            _log.Evt("window.closed", "目标窗口已关闭，退出", ("why", why));
                            break;
                        }
                    }
                }

                if (_paused)
                {
                    Thread.Sleep(60);
                    continue;
                }

                _polls++;
                bool uiaHadText = false;

                if (_uia is not null && now >= nextUia)
                {
                    nextUia = now + Math.Max(20, _o.PollMs);
                    var r = PollUia();
                    uiaHadText = r.Observations.Count > 0;
                    if (uiaHadText) _lastTextMs = now;
                    ProcessObservations(r.Observations, now);

                    if (r.Fatal is not null && now >= nextFatalLog)
                    {
                        nextFatalLog = now + 3000;
                        _log.Evt("uia.fatal", r.Fatal, ("polls", _uiaPolls));
                    }
                }

                if (_win32 is not null && now >= nextWin32)
                {
                    nextWin32 = now + Math.Max(100, _o.PollMs * 2);
                    var r = PollWin32();
                    if (r.Observations.Count > 0) { _lastTextMs = now; uiaHadText |= true; }
                    ProcessObservations(r.Observations, now);
                }

                if (_ocr is not null && now >= nextOcr)
                {
                    nextOcr = now + ocrInterval;
                    if (!_o.OcrForce && (uiaHadText || now - _lastTextMs < 2000))
                    {
                        _ocrSkips++;
                    }
                    else
                    {
                        RunOcrFrame(now);
                    }
                }

                Emit(now);

                if (now >= nextBeat)
                {
                    nextBeat = now + Math.Max(1000, _o.HeartbeatMs);
                    Heartbeat();
                }

                Thread.Sleep(15);
            }
        }
        finally
        {
            _log.Evt("run.stop", null, ("polls", _polls), ("emitted", _stats.Emitted));
        }

        WriteSummary();
        return 0;
    }

    // ---------------------------------------------------------------- poll

    private UiaPollResult PollUia()
    {
        var r = new UiaPollResult { Fatal = "not-run" };
        try
        {
            r = _uia!.Poll();
        }
        catch (Exception ex)
        {
            r.Fatal = ex.GetType().Name + ": " + ex.Message;
        }
        _uiaPolls++;
        _uiaMs.Add(r.Ms);
        _uiaErrors += r.Errors;
        _nodesSeen += r.Nodes;
        if (r.Fatal is not null) _uiaFatals++;
        if (_o.Verbose)
        {
            _log.Evt("uia.poll", null, ("nodes", r.Nodes), ("texts", r.Observations.Count),
                ("errors", r.Errors), ("truncated", r.Truncated), ("ms", Math.Round(r.Ms, 1)),
                ("qpc", System.Diagnostics.Stopwatch.GetTimestamp()), ("fatal", r.Fatal));
        }
        return r;
    }

    private Win32PollResult PollWin32()
    {
        Win32PollResult r;
        try
        {
            r = _win32!.Poll();
        }
        catch (Exception ex)
        {
            _log.Evt("win32.error", ex.GetType().Name + ": " + ex.Message);
            return new Win32PollResult();
        }
        _win32Polls++;
        _win32Ms.Add(r.Ms);
        if (_o.Verbose)
        {
            _log.Evt("win32.poll", null, ("children", r.ChildCount), ("withText", r.WithText),
                ("texts", r.Observations.Count), ("ms", Math.Round(r.Ms, 1)));
        }
        return r;
    }

    private void ProcessObservations(List<TextObservation> observations, long now)
    {
        if (observations.Count == 0) return;

        List<TextObservation>? kept = null;
        foreach (var obs in observations)
        {
            if (_o.SkipChrome && TextNoise.IsChrome(obs, _target?.Title))
            {
                _chromeSkipped++;
                if (_o.Verbose)
                    _log.Evt("text.skip_chrome", null, ("source", obs.Source), ("node", obs.NodeId),
                        ("controlType", obs.ControlType), ("len", obs.Text.Length), ("text", obs.Text));
                continue;
            }
            if (_o.SkipTexts.Count > 0 && _o.SkipTexts.Any(s => obs.Text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                _skipTextHits++;
                if (_o.Verbose)
                    _log.Evt("text.skip_rule", null, ("node", obs.NodeId), ("len", obs.Text.Length), ("text", obs.Text));
                continue;
            }
            (kept ??= new List<TextObservation>(observations.Count)).Add(obs);
        }
        if (kept is null || kept.Count == 0) return;

        _gate.Observe(kept, now);
        if (_o.Verbose)
        {
            foreach (var obs in kept)
                _log.Evt("text.observe", null, ("source", obs.Source), ("node", obs.NodeId),
                    ("controlType", obs.ControlType), ("len", obs.Text.Length),
                    ("qpc", System.Diagnostics.Stopwatch.GetTimestamp()),
                    ("head", obs.Text.Length <= 8 ? obs.Text : obs.Text[..8]));
        }
    }

    private void RunOcrFrame(long now)
    {
        var f = _ocr!.Capture();
        _ocrFrames++;
        if (f.Error is not null)
        {
            _ocrErrors++;
            _log.Evt("ocr.frame_error", f.Error, ("totalMs", Math.Round(f.TotalMs, 1)));
            return;
        }
        _ocrCaptureMs.Add(f.CaptureMs);
        _ocrRecognizeMs.Add(f.OcrMs);
        _ocrChars += f.Chars;

        var (len, head, hash) = TextNormalizer.Fingerprint(f.Text);
        _log.Evt("ocr.frame", null,
            ("frames", _ocrFrames), ("region", f.Region), ("lines", f.Lines), ("chars", f.Chars),
            ("captureMs", Math.Round(f.CaptureMs, 1)), ("ocrMs", Math.Round(f.OcrMs, 1)),
            ("totalMs", Math.Round(f.TotalMs, 1)), ("head", head), ("hash", hash),
            ("png", f.PngPath), ("text", _o.Verbose ? f.Text : null));

        if (f.Chars >= Math.Max(2, _o.MinLen))
        {
            _gate.Observe(new[] { new TextObservation("ocr", "ocr-region", "Region", f.Text, false) }, now);
        }
    }

    private void Emit(long now)
    {
        foreach (var item in _gate.Flush(now))
        {
            _lastSpoken = item.Text;
            _lastSpokenMs = now;
            var (len, head, hash) = TextNormalizer.Fingerprint(item.Text);
            _log.Evt("text.emit", null,
                ("source", item.Source), ("node", item.NodeId), ("len", len), ("head", head), ("hash", hash),
                ("settleMs", item.SettledMs - item.FirstSeenMs), ("tFirstSeen", item.FirstSeenMs),
                ("text", _o.Verbose ? item.Text : null));
            _speech?.Speak(item.Text);
        }
    }

    private void Heartbeat()
    {
        _log.Evt("heartbeat", null,
            ("polls", _polls), ("uiaPolls", _uiaPolls), ("uiaText", _stats.Emitted),
            ("uiaMsP50", P50(_uiaMs)), ("uiaMsMax", Max(_uiaMs)),
            ("win32Polls", _win32Polls), ("win32MsP50", P50(_win32Ms)),
            ("nodes", _nodesSeen), ("uiaErrors", _uiaErrors),
            ("observed", _stats.Observed), ("dup", _stats.SuppressedRepeat), ("short", _stats.SuppressedShort),
            ("filtered", _stats.SuppressedFilter), ("pendingChange", _stats.SuppressedSameAsPending),
            ("unchanged", _stats.SuppressedUnchanged),
            ("emitted", _stats.Emitted), ("speakReqs", _speech?.SpeakRequests ?? 0),
            ("chunks", _speech?.Chunks ?? 0), ("speakFail", _speech?.Failures ?? 0),
            ("speakDrop", _speech?.Dropped ?? 0), ("speakWaitMax", Math.Round(_speech?.MaxQueueWaitMs ?? 0, 1)),
            ("speakMsMax", Math.Round(_speech?.MaxSpeakMs ?? 0, 1)),
            ("chromeSkip", _chromeSkipped), ("ruleSkip", _skipTextHits),
            ("ocrFrames", _ocrFrames), ("ocrSkips", _ocrSkips), ("ocrErrors", _ocrErrors),
            ("ocrMsP50", P50(_ocrRecognizeMs)), ("backend", _speech?.BackendName ?? "none"));
    }

    // ---------------------------------------------------------------- input

    private void HandleInput()
    {
        if (_hotkeys is not null)
        {
            if (_hotkeys.ConsumeQuit()) { _log.Evt("hotkey.f10", "退出"); _quit = true; return; }
            if (_hotkeys.ConsumePause()) TogglePause();
            if (_hotkeys.ConsumeRepeat()) RepeatLast();
        }

        if (Console.IsInputRedirected) return;
        try
        {
            while (Console.KeyAvailable)
            {
                var k = Console.ReadKey(intercept: true).Key;
                switch (k)
                {
                    case ConsoleKey.P: TogglePause(); break;
                    case ConsoleKey.R: RepeatLast(); break;
                    case ConsoleKey.Q: _log.Evt("key.q", "退出"); _quit = true; return;
                    case ConsoleKey.D:
                        if (_uia is not null && _target is not null)
                            _log.Evt("uia.nodes", null, ("nodes", _nodesSeen));
                        break;
                }
            }
        }
        catch
        {
            // 无控制台（比如从资源管理器启动）时 KeyAvailable 会抛，忽略
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _speech?.Cancel();
        if (_paused) _gate.Forget();
        _log.Evt("pause", _paused ? "已暂停（F8 继续）" : "已继续", ("paused", _paused));
    }

    private void RepeatLast()
    {
        if (_lastSpoken.Length == 0) return;
        _log.Evt("repeat", "重读最后一句", ("len", _lastSpoken.Length), ("ageMs", _log.ElapsedMs - _lastSpokenMs));
        _speech?.Speak(_lastSpoken);
    }

    // ---------------------------------------------------------------- summary

    private void WriteSummary()
    {
        var summary = new Dictionary<string, object?>
        {
            ["mode"] = "run-summary",
            ["runId"] = _log.RunId,
            ["qpcFreq"] = Stopwatch.Frequency,
            ["log"] = _log.TextPath,
            ["jsonl"] = _log.JsonPath,
            ["elapsedMs"] = _log.ElapsedMs,
            ["target"] = _target is null ? null : new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{_target.Hwnd.ToInt64():X}",
                ["pid"] = _target.Pid,
                ["exe"] = _target.ProcessName,
                ["class"] = _target.ClassName,
                ["title"] = _target.Title,
                ["bounds"] = _target.Bounds.ToString(),
                ["client"] = _target.ClientBounds.ToString(),
            },
            ["polls"] = _polls,
            ["uia"] = new Dictionary<string, object?>
            {
                ["enabled"] = _uia is not null,
                ["polls"] = _uiaPolls,
                ["nodes"] = _nodesSeen,
                ["errors"] = _uiaErrors,
                ["fatals"] = _uiaFatals,
                ["msP50"] = P50(_uiaMs),
                ["msP95"] = P95(_uiaMs),
                ["msMax"] = Max(_uiaMs),
                ["samples"] = _uiaMs.Count,
            },
            ["win32"] = new Dictionary<string, object?>
            {
                ["enabled"] = _win32 is not null,
                ["polls"] = _win32Polls,
                ["msP50"] = P50(_win32Ms),
                ["msMax"] = Max(_win32Ms),
            },
            ["gate"] = new Dictionary<string, object?>
            {
                ["observed"] = _stats.Observed,
                ["suppressedRepeat"] = _stats.SuppressedRepeat,
                ["suppressedShort"] = _stats.SuppressedShort,
                ["suppressedFilter"] = _stats.SuppressedFilter,
                ["suppressedSameAsPending"] = _stats.SuppressedSameAsPending,
                ["suppressedChrome"] = _chromeSkipped,
                ["suppressedRule"] = _skipTextHits,
                ["suppressedUnchanged"] = _stats.SuppressedUnchanged,
                ["emitted"] = _stats.Emitted,
            },
            ["speech"] = new Dictionary<string, object?>
            {
                ["backend"] = _speech?.BackendName,
                ["initError"] = _speech?.InitError,
                ["requests"] = _speech?.SpeakRequests ?? 0,
                ["chunks"] = _speech?.Chunks ?? 0,
                ["failures"] = _speech?.Failures ?? 0,
                ["dropped"] = _speech?.Dropped ?? 0,
                ["maxQueueWaitMs"] = Math.Round(_speech?.MaxQueueWaitMs ?? 0, 1),
                ["maxSpeakMs"] = Math.Round(_speech?.MaxSpeakMs ?? 0, 1),
                ["fallbacks"] = _speech?.Fallbacks ?? 0,
            },
            ["ocr"] = new Dictionary<string, object?>
            {
                ["enabled"] = _ocr is not null,
                ["frames"] = _ocrFrames,
                ["skips"] = _ocrSkips,
                ["errors"] = _ocrErrors,
                ["chars"] = _ocrChars,
                ["captureMsP50"] = P50(_ocrCaptureMs),
                ["recognizeMsP50"] = P50(_ocrRecognizeMs),
                ["recognizeMsP95"] = P95(_ocrRecognizeMs),
                ["recognizeMsMax"] = Max(_ocrRecognizeMs),
            },
            ["lastSpokenLen"] = _lastSpoken.Length,
        };

        var path = _o.SummaryPath;
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Path.GetDirectoryName(_log.TextPath)!, Path.GetFileNameWithoutExtension(_log.TextPath) + ".summary.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            _log.Evt("summary.written", path);
        }
        catch (Exception ex)
        {
            _log.Evt("summary.error", ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static double P50(List<double> xs) => Percentile(xs, 0.50);
    internal static double P95(List<double> xs) => Percentile(xs, 0.95);
    internal static double Max(List<double> xs) => xs.Count == 0 ? 0 : Math.Round(xs.Max(), 1);

    internal static double Percentile(List<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToArray();
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return Math.Round(sorted[Math.Clamp(idx, 0, sorted.Length - 1)], 1);
    }

    public void Dispose()
    {
        try { _hotkeys?.Dispose(); } catch { }
        try { _speech?.Dispose(); } catch { }
        try { _uia?.Dispose(); } catch { }
        try { _ocr?.Dispose(); } catch { }
    }
}
