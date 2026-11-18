using System.Collections.Concurrent;

namespace GalA11yForge.ExternalReader.Speech;

/// <summary>
/// 朗读路由：一个后台 STA 线程拥有后端（SAPI 的 COM 对象必须固定在同一套间），
/// 主循环只投递文本，绝不阻塞在 Speak 上。
///
/// 降级链（R6 的结论）：auto = NVDA 直连 → SAPI5 → 静默+日志。
/// 每次降级都写日志，禁止静默失败 —— 玩家“什么都没听见”时日志必须能解释原因。
/// </summary>
public sealed class SpeechRouter : IDisposable
{
    private sealed record Cmd(string Kind, string? Text = null, long T0 = 0);

    /// <summary>积压超过这个条数就丢弃最旧的一句（读屏宁可“跳句”，也不能落后几秒）。</summary>
    private const int MaxBacklog = 4;

    private readonly BlockingCollection<Cmd> _queue = new();
    private readonly Thread _thread;
    private readonly Options _o;
    private readonly Log _log;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile ISpeechBackend? _backend;
    private volatile string _initError = "";
    private bool _stopping;

    public long SpeakRequests;
    public long Chunks;
    public long Failures;
    public long Dropped;
    public double MaxQueueWaitMs;
    public double MaxSpeakMs;
    public int Fallbacks;
    public string InitError => _initError;

    public SpeechRouter(Options options, Log log)
    {
        _o = options;
        _log = log;
        _thread = new Thread(Worker) { IsBackground = true, Name = "forgereader-speech" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public string BackendName => _backend?.Name ?? "none";

    private void Worker()
    {
        try
        {
            _backend = CreateBackend();
            _log.Evt("speech.backend", null, ("backend", _backend?.Name ?? "none"), ("requested", _o.Speech), ("initError", _initError));
        }
        catch (Exception ex)
        {
            _initError = ex.GetType().Name + ": " + ex.Message;
            _backend = new NullBackend(_log);
            _log.Evt("speech.backend_failed", _initError);
        }
        finally
        {
            _ready.Set();
        }

        foreach (var cmd in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (cmd.Kind)
                {
                    case "speak":
                        if (_queue.Count > MaxBacklog)
                        {
                            // 朗读已经落后：丢掉这句，保证新台词优先（和商业读屏器的“打断”一致）
                            Interlocked.Increment(ref Dropped);
                            _log.Evt("speech.drop", null, ("backlog", _queue.Count), ("dropped", Dropped),
                                ("len", (cmd.Text ?? "").Length));
                            break;
                        }
                        SpeakNow(cmd.Text ?? string.Empty, cmd.T0);
                        break;
                    case "cancel":
                        _backend?.Cancel();
                        break;
                    case "stop":
                        _stopping = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Failures);
                _log.Evt("speech.error", ex.GetType().Name + ": " + ex.Message);
            }
            if (_stopping) break;
        }

        // 后端必须由拥有它的 STA 线程自己释放：跨套间直接 Release SAPI 的 COM 对象
        // 会让进程在退出时崩在 0xC0000005（本原型第一版就是这么崩的，见 evidence）。
        try
        {
            _backend?.Dispose();
            _backend = null;
            _log.Evt("speech.stopped", null, ("requests", SpeakRequests), ("chunks", Chunks),
                ("dropped", Dropped), ("failures", Failures));
        }
        catch (Exception ex)
        {
            _log.Evt("speech.dispose_error", ex.GetType().Name + ": " + ex.Message);
        }
    }

    private ISpeechBackend CreateBackend()
    {
        switch (_o.Speech)
        {
            case "null":
                return new NullBackend(_log);

            case "nvda":
            {
                var nvda = new NvdaBackend();
                if (nvda.TryInit(_o.NvdaDll, out var err))
                {
                    _log.Evt("speech.nvda_ready", null, ("dll", nvda.DllPath));
                    return nvda;
                }
                nvda.Dispose();
                _initError = err ?? "NVDA 不可用";
                _log.Evt("speech.fallback", "NVDA 不可用，降级到 SAPI", ("reason", _initError));
                Fallbacks++;
                return CreateSapiOrNull();
            }

            case "sapi":
                return CreateSapiOrNull();

            default: // auto
            {
                var nvda = new NvdaBackend();
                if (nvda.TryInit(_o.NvdaDll, out var err))
                {
                    _log.Evt("speech.nvda_ready", null, ("dll", nvda.DllPath));
                    return nvda;
                }
                nvda.Dispose();
                _log.Evt("speech.nvda_unavailable", err, ("fallback", "sapi"));
                Fallbacks++;
                return CreateSapiOrNull();
            }
        }
    }

    private ISpeechBackend CreateSapiOrNull()
    {
        var sapi = new SapiBackend();
        if (sapi.TryInit(out var err))
        {
            if (_o.SapiRate != 0) sapi.SetRate(_o.SapiRate);
            _log.Evt("speech.sapi_ready", null, ("rate", sapi.Rate), ("requestedRate", _o.SapiRate));
            return sapi;
        }
        _initError = err ?? "SAPI 初始化失败";
        _log.Evt("speech.sapi_failed", _initError);
        Fallbacks++;
        return new NullBackend(_log);
    }

    private void SpeakNow(string text, long t0)
    {
        var backend = _backend;
        if (backend is null || text.Length == 0) return;

        var waitMs = t0 > 0 ? Environment.TickCount64 - t0 : 0;
        if (waitMs > MaxQueueWaitMs) MaxQueueWaitMs = waitMs;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        backend.Cancel();  // 新句打断旧句（SAPI: PURGE；NVDA: cancelSpeech）
        var parts = SentenceSplitter.Split(text);
        foreach (var part in parts)
        {
            var ok = backend.Speak(part);
            Interlocked.Increment(ref Chunks);
            if (!ok)
            {
                Interlocked.Increment(ref Failures);
                _log.Evt("speech.speak_failed", null, ("backend", backend.Name), ("len", part.Length),
                    ("error", (backend as SapiBackend)?.LastError ?? (backend as NvdaBackend)?.LastError));
                // 运行时降级：NVDA 挂了就切 SAPI（只切一次）
                if (backend is NvdaBackend && _o.Speech is "auto" or "nvda")
                {
                    _log.Evt("speech.fallback", "NVDA 运行中失败，切换到 SAPI");
                    var sapi = new SapiBackend();
                    if (sapi.TryInit(out var err))
                    {
                        try { backend.Dispose(); } catch { }
                        _backend = sapi;
                        Fallbacks++;
                        if (_o.SapiRate != 0) sapi.SetRate(_o.SapiRate);
                        sapi.Speak(part);
                    }
                    else
                    {
                        _log.Evt("speech.sapi_failed", err);
                        _backend = new NullBackend(_log);
                    }
                }
                return;
            }
        }

        var ms = sw.Elapsed.TotalMilliseconds;
        if (ms > MaxSpeakMs) MaxSpeakMs = ms;
        if (_o.Verbose)
            _log.Evt("speech.spoke", null, ("backend", backend.Name), ("parts", parts.Count),
                ("waitMs", waitMs), ("ms", Math.Round(ms, 1)));
    }

    public void Speak(string text)
    {
        Interlocked.Increment(ref SpeakRequests);
        _queue.Add(new Cmd("speak", text, Environment.TickCount64));
    }

    public void Cancel() => _queue.Add(new Cmd("cancel", null, Environment.TickCount64));

    public void Dispose()
    {
        // 先清空积压再投 stop：读屏器要退出了，没必要把排队的句子念完
        try { while (_queue.TryTake(out _)) { } } catch { }
        try { _queue.Add(new Cmd("stop")); } catch { }
        try { _queue.CompleteAdding(); } catch { }
        _thread.Join(TimeSpan.FromSeconds(3));
        // 注意：不在这里 Dispose 后端 —— 后端由 speech 线程自己释放（跨套间 Release 会崩）
        _ready.Dispose();
    }
}
