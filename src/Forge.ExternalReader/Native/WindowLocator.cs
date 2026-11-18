using System.Diagnostics;
using GalA11yForge.ExternalReader.Capture;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Native;

/// <summary>被附着的顶层窗口。只读快照。</summary>
public sealed record WindowTarget(
    IntPtr Hwnd,
    int Pid,
    string ProcessName,
    string Title,
    string ClassName,
    RECT Bounds,
    RECT ClientBounds)
{
    public string Describe() => $"hwnd=0x{Hwnd.ToInt64():X} pid={Pid} exe={ProcessName} class={ClassName} title=\"{Title}\" bounds={Bounds}";
}

public sealed record WindowCandidate(IntPtr Hwnd, int Pid, string ProcessName, string Title, string ClassName, RECT Bounds, bool Visible, bool Minimized, long Area);

/// <summary>
/// 按 pid / 标题子串 / 进程名找目标窗口。
/// 只枚举顶层窗口，跳过自己的进程、跳过 0 面积的隐藏壳窗口；
/// 同一进程多个窗口时取“可见且面积最大”的那个（游戏的 main window 通常就是它）。
/// </summary>
public static class WindowLocator
{
    public static List<WindowCandidate> Enumerate(bool includeInvisible = false, bool includeOwnProcess = false)
    {
        var list = new List<WindowCandidate>();
        var ownPid = Environment.ProcessId;
        Win32.EnumWindows((hwnd, _) =>
        {
            try
            {
                var visible = Win32.IsWindowVisible(hwnd);
                if (!visible && !includeInvisible) return true;

                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return true;
                if (!includeOwnProcess && pid == (uint)ownPid) return true;

                var rect = Win32.GetVisibleBounds(hwnd);
                long area = (long)Math.Max(0, rect.Width) * Math.Max(0, rect.Height);
                if (area <= 0) return true;

                var title = Win32.GetWindowTitle(hwnd);
                var cls = Win32.GetClassName(hwnd);
                string exe;
                try { exe = Process.GetProcessById((int)pid).ProcessName; }
                catch { exe = "?"; }

                list.Add(new WindowCandidate(hwnd, (int)pid, exe, title, cls, rect, visible, Win32.IsIconic(hwnd), area));
            }
            catch
            {
                // 枚举期间窗口消失/拒绝访问：跳过，不打断整体枚举
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>按句柄直接构造（用于渲染器子窗口：Chromium 的正文常常只在 Chrome_RenderWidgetHostHWND 上）。</summary>
    public static WindowTarget? FromHwnd(IntPtr hwnd, Log log)
    {
        if (!Win32.IsWindow(hwnd))
        {
            log.Evt("hwnd.invalid", null, ("hwnd", $"0x{hwnd.ToInt64():X}"));
            return null;
        }
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        string exe;
        try { exe = Process.GetProcessById((int)pid).ProcessName; }
        catch { exe = "?"; }
        var title = Win32.GetWindowTitle(hwnd);
        var cls = Win32.GetClassName(hwnd);
        var bounds = Win32.GetVisibleBounds(hwnd);
        var client = Win32.GetClientScreenRect(hwnd);
        log.Evt("window.by_hwnd", null, ("hwnd", $"0x{hwnd.ToInt64():X}"), ("pid", pid), ("exe", exe),
            ("class", cls), ("title", title), ("bounds", bounds.ToString()));
        return new WindowTarget(hwnd, (int)pid, exe, title, cls, bounds, client);
    }

    public static List<Win32ChildInfo> Children(IntPtr parent)
    {
        var list = new List<Win32ChildInfo>();
        Win32.EnumChildWindows(parent, (hwnd, _) =>
        {
            try
            {
                var text = Win32.GetWindowTextViaMessage(hwnd);
                if (string.IsNullOrWhiteSpace(text)) text = Win32.GetWindowTitle(hwnd);
                list.Add(new Win32ChildInfo(hwnd, $"0x{hwnd.ToInt64():X}", Win32.GetClassName(hwnd), text.Trim(),
                    Win32.GetVisibleBounds(hwnd), Win32.IsWindowVisible(hwnd)));
                if (list.Count >= 500) return false;
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowTarget? Find(IntPtr? hwnd, int? pid, string? titleSubstring, string? exeFilter, bool includeInvisible, Log log)
    {
        if (hwnd.HasValue) return FromHwnd(hwnd.Value, log);

        var all = Enumerate(includeInvisible);
        log.Evt("window.enumerate", null, ("count", all.Count));

        IEnumerable<WindowCandidate> q = all;
        if (pid.HasValue)
        {
            q = q.Where(c => c.Pid == pid.Value);
        }
        if (!string.IsNullOrWhiteSpace(titleSubstring))
        {
            q = q.Where(c => c.Title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(exeFilter))
        {
            var want = exeFilter.Trim();
            if (want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) want = want[..^4];
            q = q.Where(c => c.ProcessName.Equals(want, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = q.Where(c => !c.Minimized).OrderByDescending(c => c.Area).ToList();
        if (ordered.Count == 0) ordered = q.OrderByDescending(c => c.Area).ToList();

        foreach (var c in ordered.Take(5))
            log.Evt("window.candidate", null, ("hwnd", $"0x{c.Hwnd.ToInt64():X}"), ("pid", c.Pid), ("exe", c.ProcessName),
                ("class", c.ClassName), ("title", c.Title), ("area", c.Area), ("visible", c.Visible), ("minimized", c.Minimized));

        var best = ordered.FirstOrDefault();
        if (best is null) return null;

        var client = Win32.GetClientScreenRect(best.Hwnd);
        return new WindowTarget(best.Hwnd, best.Pid, best.ProcessName, best.Title, best.ClassName, best.Bounds, client);
    }

    public static bool StillAlive(WindowTarget t) => Win32.IsWindow(t.Hwnd);

    /// <summary>
    /// 窗口存活二次复核。只信 IsWindow 会在两种情况下误判：
    ///   1) 目标进程在别的线程上重建了窗口（句柄作废但游戏还在跑）；
    ///   2) 读屏器自身 UIA/COM 调用受伤后拿到错误返回。
    /// 因此 IsWindow 为假时，再看“进程还在 + 同 pid 仍有可见顶层窗口（标题相同或面积接近）”。
    /// </summary>
    public static (bool Alive, string Reason) Recheck(WindowTarget t)
    {
        if (Win32.IsWindow(t.Hwnd)) return (true, "iswindow");

        bool procAlive;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(t.Pid);
            procAlive = !p.HasExited;
        }
        catch
        {
            procAlive = false;
        }
        if (!procAlive) return (false, "process-exited");

        // 进程还在：找同 pid 的可见顶层窗口；标题一致或面积接近即认为窗口仍在（可能换了句柄）
        WindowCandidate? match = null;
        foreach (var c in Enumerate(includeInvisible: true))
        {
            if (c.Pid != t.Pid) continue;
            var sameTitle = string.Equals(c.Title, t.Title, StringComparison.Ordinal);
            var sameClass = string.Equals(c.ClassName, t.ClassName, StringComparison.Ordinal);
            if (sameTitle || sameClass) { match = c; break; }
        }
        return match is null ? (false, "no-window-for-pid") : (true, "rehomed:0x" + match.Hwnd.ToInt64().ToString("X"));
    }
}
