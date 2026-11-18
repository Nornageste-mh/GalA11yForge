using System.Diagnostics;
using System.Text;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Capture;

public sealed record Win32ChildInfo(
    IntPtr Hwnd, string Handle, string ClassName, string Text, RECT Bounds, bool Visible);

public sealed class Win32PollResult
{
    public List<TextObservation> Observations { get; } = new();
    public List<Win32ChildInfo> Children { get; } = new();
    public int ChildCount;
    public int WithText;
    public double Ms;
}

/// <summary>
/// Win32 文本面：EnumChildWindows + WM_GETTEXT（跨进程由 USER32 编组，带 60ms 超时）。
/// 这条路的覆盖面比 UIA 窄（只有原生控件），但成本极低，且能补 UIA 没暴露的
/// Static/Edit —— R9 的实测里 TextBox 的台词两边都能读到，自绘 Panel 两边都是空。
/// </summary>
public sealed class Win32Source
{
    private readonly WindowTarget _target;
    private readonly Options _o;

    public Win32Source(WindowTarget target, Options options)
    {
        _target = target;
        _o = options;
    }

    public Win32PollResult Poll()
    {
        var sw = Stopwatch.StartNew();
        var result = new Win32PollResult();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Win32.EnumChildWindows(_target.Hwnd, (hwnd, _) =>
        {
            result.ChildCount++;
            try
            {
                if (result.ChildCount > 4000) return false;
                var cls = Win32.GetClassName(hwnd);
                var text = Win32.GetWindowTextViaMessage(hwnd);
                if (string.IsNullOrWhiteSpace(text)) text = Win32.GetWindowTitle(hwnd);
                text = text.Trim();
                var rect = Win32.GetVisibleBounds(hwnd);
                var visible = Win32.IsWindowVisible(hwnd);

                result.Children.Add(new Win32ChildInfo(hwnd, $"0x{hwnd.ToInt64():X}", cls, text, rect, visible));
                if (text.Length == 0) return true;

                result.WithText++;
                var norm = TextNormalizer.Normalize(text, _o.MaxLen);
                if (norm.Length > 0 && seen.Add(norm))
                    result.Observations.Add(new TextObservation("win32", $"0x{hwnd.ToInt64():X}", cls, norm, !visible));
            }
            catch
            {
                // 子窗口在枚举中消失：忽略
            }
            return true;
        }, IntPtr.Zero);

        sw.Stop();
        result.Ms = sw.Elapsed.TotalMilliseconds;
        return result;
    }
}
