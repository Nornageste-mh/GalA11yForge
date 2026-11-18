using System.Runtime.InteropServices;
using System.Text;

namespace GalA11yForge.ExternalReader.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool IsEmpty => Width <= 0 || Height <= 0;
    public override readonly string ToString() => $"{Left},{Top},{Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
}

/// <summary>
/// 只读的 Win32 边界：枚举、读标题/类名/矩形/文本，注册热键，截屏用的 DC 操作。
/// 这里没有任何 WriteProcessMemory / SetWindowsHookEx / CreateRemoteThread —— 原型不注入。
/// </summary>
public static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public const uint SMTO_BLOCK = 0x0001;

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>PER_MONITOR_AWARE_V2 = -4。必须在创建任何窗口/位图前调用，否则 OCR 区域会偏。</summary>
    public static void EnableDpiAwareness()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 2);
        var n = GetWindowTextW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : string.Empty;
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var n = GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : string.Empty;
    }

    /// <summary>WM_GETTEXT 跨进程可被 USER32 编组；带超时，挂起的窗口不会卡死调用方。</summary>
    public static string GetWindowTextViaMessage(IntPtr hwnd, uint timeoutMs = 60)
    {
        var len = SendMessageInt(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, timeoutMs);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(Math.Min((int)len + 1, 64 * 1024));
        var ok = SendMessageTimeoutW(hwnd, WM_GETTEXT, new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG | SMTO_BLOCK, timeoutMs, out _);
        if (ok == IntPtr.Zero) return string.Empty;
        return sb.ToString();
    }

    public static int SendMessageInt(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint timeoutMs)
    {
        var r = SendMessageTimeoutW(hwnd, msg, wParam, null!, SMTO_ABORTIFHUNG, timeoutMs, out var result);
        return r == IntPtr.Zero ? 0 : result.ToInt32();
    }

    /// <summary>真实可见边界（不含 DWM 阴影）。失败时退回 GetWindowRect。</summary>
    public static RECT GetVisibleBounds(IntPtr hwnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && !r.IsEmpty)
                return r;
        }
        catch { }
        GetWindowRect(hwnd, out var wr);
        return wr;
    }

    public static RECT GetClientScreenRect(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var cr);
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);
        return new RECT { Left = origin.X, Top = origin.Y, Right = origin.X + cr.Width, Bottom = origin.Y + cr.Height };
    }
}
