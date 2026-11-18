using System.Runtime.InteropServices;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Runtime;

/// <summary>
/// 全局热键：F8 暂停/继续、F9 重读最后一句、F10 退出。
/// RegisterHotKey 绑定到注册它的线程，所以必须有自己的消息循环线程；
/// 主循环只读几个 Interlocked 标志，不跨线程回调。
///
/// 注册失败（被别的程序占用）不致命：引擎会退化成控制台按键 p/r/q。
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    private const int IdPause = 1;
    private const int IdRepeat = 2;
    private const int IdQuit = 3;
    private const uint VkF8 = 0x77, VkF9 = 0x78, VkF10 = 0x79;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    private Thread? _thread;
    private uint _threadId;
    private int _pause, _repeat, _quit;
    private volatile bool _registered;
    private readonly ManualResetEventSlim _started = new(false);

    public bool Registered => _registered;

    public bool ConsumePause() => Interlocked.Exchange(ref _pause, 0) != 0;
    public bool ConsumeRepeat() => Interlocked.Exchange(ref _repeat, 0) != 0;
    public bool ConsumeQuit() => Interlocked.Exchange(ref _quit, 0) != 0;

    public void Start(Log log)
    {
        _thread = new Thread(() =>
        {
            _threadId = Win32.GetCurrentThreadId();
            var okPause = Win32.RegisterHotKey(IntPtr.Zero, IdPause, 0, VkF8);
            var okRepeat = Win32.RegisterHotKey(IntPtr.Zero, IdRepeat, 0, VkF9);
            var okQuit = Win32.RegisterHotKey(IntPtr.Zero, IdQuit, 0, VkF10);
            _registered = okPause || okRepeat || okQuit;
            log.Evt("hotkey.register", null, ("f8", okPause), ("f9", okRepeat), ("f10", okQuit));
            _started.Set();

            while (true)
            {
                var r = Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                if (r == 0 || r == -1) break;         // WM_QUIT / error
                if (msg.message == WmQuit) break;
                if (msg.message != WmHotkey) continue;
                switch (msg.wParam.ToInt32())
                {
                    case IdPause: Interlocked.Exchange(ref _pause, 1); break;
                    case IdRepeat: Interlocked.Exchange(ref _repeat, 1); break;
                    case IdQuit: Interlocked.Exchange(ref _quit, 1); break;
                }
            }

            Win32.UnregisterHotKey(IntPtr.Zero, IdPause);
            Win32.UnregisterHotKey(IntPtr.Zero, IdRepeat);
            Win32.UnregisterHotKey(IntPtr.Zero, IdQuit);
        })
        { IsBackground = true, Name = "forgereader-hotkeys" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_thread is null) return;
        try { Win32.PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero); } catch { }
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        try { _started.Dispose(); } catch { }
    }
}
