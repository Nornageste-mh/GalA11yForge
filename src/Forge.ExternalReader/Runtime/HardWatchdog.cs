using System.Diagnostics;

namespace GalA11yForge.ExternalReader.Runtime;

/// <summary>
/// UIA/COM 硬超时。独立后台线程只看“心跳时间”，不碰 UIA；
/// 主线程（或事件订阅线程）在每次进入可能挂起的 UIA 调用前 Beat()。
/// 超时后写日志并以退出码 90 强制结束进程——COM 挂起无法用 Thread.Abort 救，
/// 只能把整个进程当作可丢弃的探针。
/// </summary>
public sealed class HardWatchdog : IDisposable
{
    public const int ExitCode = 90;

    private readonly Log _log;
    private readonly int _timeoutMs;
    private readonly Thread _thread;
    private long _lastBeat;
    private volatile bool _stop;
    private volatile string _phase = "start";
    private int _fired;

    public HardWatchdog(Log log, int timeoutMs, string phase)
    {
        _log = log;
        _timeoutMs = timeoutMs;
        _phase = phase;
        _lastBeat = Stopwatch.GetTimestamp();
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "forge-uia-watchdog",
        };
        _thread.Start();
    }

    public void Beat(string phase)
    {
        _phase = phase;
        Volatile.Write(ref _lastBeat, Stopwatch.GetTimestamp());
    }

    private void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(100);
            if (_stop) return;
            if (_timeoutMs <= 0) continue;

            var last = Volatile.Read(ref _lastBeat);
            var stuckMs = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;
            if (stuckMs < _timeoutMs) continue;
            if (Interlocked.Exchange(ref _fired, 1) != 0) return;

            try
            {
                _log.Evt("watchdog.fired", "UIA 调用超过硬超时，强制退出（防 COM 挂起）",
                    ("phase", _phase), ("timeoutMs", _timeoutMs), ("stuckMs", Math.Round(stuckMs, 1)),
                    ("exitCode", ExitCode));
            }
            catch { }

            // 不走正常返回路径：主线程此刻可能正卡在 COM 调用里。
            try { _log.Dispose(); } catch { }
            Environment.Exit(ExitCode);
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _thread.Join(500); } catch { }
    }
}
