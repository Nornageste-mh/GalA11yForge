using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GalA11yForge.ExternalReader.Native;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace GalA11yForge.ExternalReader.Capture;

public sealed class OcrFrameResult
{
    public string Text { get; set; } = string.Empty;
    public int Lines { get; set; }
    public int Chars { get; set; }
    public double CaptureMs { get; set; }
    public double OcrMs { get; set; }
    public double TotalMs { get; set; }
    public string Region { get; set; } = string.Empty;
    public string? PngPath { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// OCR 兜底（实验性，默认关）：Windows.Media.Ocr + 只截指定区域。
///
/// 硬约束（R9/R12 实测）：
///   * 只截对话框区域。整屏 1920×1080 要 0.8–1.6s，对话框区域只要 25–50ms；
///   * 语言包不通用：本机只有 zh-Hans-CN，ja 会退化成乱码 —— 所以默认不开启，
///     开启前必须显式检查 AvailableRecognizerLanguages；
///   * 中文 OCR 会在汉字间插空格，交给 TextNormalizer 去掉。
/// </summary>
public sealed class OcrSource : IDisposable
{
    private readonly WindowTarget? _target;
    private readonly Options _o;
    private readonly Log _log;
    private OcrEngine? _engine;
    private bool _disposed;

    public OcrFrameResult Last { get; private set; } = new();
    public long Frames;
    public readonly List<double> CaptureMs = new();
    public readonly List<double> OcrMs = new();

    public OcrSource(WindowTarget? target, Options options, Log log)
    {
        _target = target;
        _o = options;
        _log = log;
    }

    /// <summary>返回可用语言；引擎不可用时 available 为空。</summary>
    public IReadOnlyList<string> AvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
        }
        catch (Exception ex)
        {
            _log.Evt("ocr.error", "枚举语言失败: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    public bool TryInit(out string? error)
    {
        error = null;
        try
        {
            var langs = AvailableLanguages();
            _log.Evt("ocr.languages", null, ("available", string.Join(",", langs)), ("requested", _o.OcrLang));
            if (langs.Count == 0)
            {
                error = "本机没有任何 Windows OCR 识别语言包";
                return false;
            }
            if (!langs.Contains(_o.OcrLang, StringComparer.OrdinalIgnoreCase))
            {
                error = $"请求的 OCR 语言 {_o.OcrLang} 未安装（本机只有 {string.Join(",", langs)}）";
                return false;
            }
            _engine = OcrEngine.TryCreateFromLanguage(new Language(_o.OcrLang));
            if (_engine is null)
            {
                error = "OcrEngine.TryCreateFromLanguage 返回 null";
                return false;
            }
            _log.Evt("ocr.init", null, ("lang", _o.OcrLang), ("maxDimension", OcrEngine.MaxImageDimension));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>区域：显式 x,y,w,h（屏幕坐标）或 auto=窗口下方一条带。</summary>
    public Rectangle ResolveRegion()
    {
        var screen = SystemInformation.VirtualScreen;
        Rectangle r;
        if (!string.IsNullOrWhiteSpace(_o.Region) && !_o.Region.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var parts = _o.Region.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4 &&
                int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) &&
                int.TryParse(parts[2], out var w) && int.TryParse(parts[3], out var h))
            {
                r = new Rectangle(x, y, w, h);
            }
            else
            {
                _log.Evt("ocr.region", "region 解析失败，回退 auto", ("raw", _o.Region));
                r = AutoRegion();
            }
        }
        else
        {
            r = AutoRegion();
        }

        var inter = Rectangle.Intersect(r, screen);
        if (inter.Width < 8 || inter.Height < 8)
        {
            _log.Evt("ocr.region", "区域与屏幕无交集，使用屏幕左上角", ("want", r.ToString()), ("screen", screen.ToString()));
            inter = new Rectangle(screen.Left + 8, screen.Top + 8, Math.Min(640, screen.Width - 16), Math.Min(240, screen.Height - 16));
        }
        // MaxImageDimension 保护
        var max = (int)OcrEngine.MaxImageDimension;
        if (inter.Width > max || inter.Height > max)
            inter = new Rectangle(inter.X, inter.Y, Math.Min(inter.Width, max), Math.Min(inter.Height, max));
        return inter;
    }

    private Rectangle AutoRegion()
    {
        if (_target is not null)
        {
            var c = _target.ClientBounds;
            if (c.Width < 64 || c.Height < 64) c = _target.Bounds;
            if (c.Width >= 64 && c.Height >= 64)
            {
                var w = Math.Max(64, (int)(c.Width * 0.92));
                var h = Math.Max(48, (int)(c.Height * 0.40));
                var x = c.Left + (c.Width - w) / 2;
                var y = c.Bottom - h - Math.Max(4, (int)(c.Height * 0.03));
                return new Rectangle(x, y, w, h);
            }
        }

        // 没有目标窗口：取主屏下方居中的一条带（对话框常见位置）
        var screen = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
        var bw = Math.Max(320, (int)(screen.Width * 0.6));
        var bh = Math.Max(120, (int)(screen.Height * 0.2));
        return new Rectangle(screen.X + (screen.Width - bw) / 2, screen.Y + (int)(screen.Height * 0.72), bw, bh);
    }

    public OcrFrameResult Capture()
    {
        var total = Stopwatch.StartNew();
        var result = new OcrFrameResult();
        var region = ResolveRegion();
        result.Region = $"{region.X},{region.Y},{region.Width},{region.Height}";

        Bitmap? bmp = null;
        try
        {
            var sw = Stopwatch.StartNew();
            bmp = CaptureBitmap(region);
            sw.Stop();
            result.CaptureMs = sw.Elapsed.TotalMilliseconds;
            CaptureMs.Add(result.CaptureMs);

            if (!string.IsNullOrWhiteSpace(_o.OcrPngDir))
            {
                try
                {
                    Directory.CreateDirectory(_o.OcrPngDir);
                    var png = Path.Combine(_o.OcrPngDir, $"frame-{Frames:D5}.png");
                    bmp.Save(png, ImageFormat.Png);
                    result.PngPath = png;
                }
                catch (Exception ex)
                {
                    _log.Evt("ocr.png_error", ex.Message);
                }
            }

            if (_engine is null) throw new InvalidOperationException("OCR 引擎未初始化");

            var sw2 = Stopwatch.StartNew();
            var text = Recognize(bmp);
            sw2.Stop();
            result.OcrMs = sw2.Elapsed.TotalMilliseconds;
            OcrMs.Add(result.OcrMs);

            result.Text = TextNormalizer.Normalize(text, _o.MaxLen * 4);
            result.Chars = result.Text.Length;
            result.Lines = result.Text.Length == 0 ? 0 : result.Text.Split('\n').Length;
        }
        catch (Exception ex)
        {
            result.Error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            bmp?.Dispose();
        }

        total.Stop();
        result.TotalMs = total.Elapsed.TotalMilliseconds;
        Frames++;
        Last = result;
        return result;
    }

    private Bitmap CaptureBitmap(Rectangle region)
    {
        if (_o.Capture.Equals("printwindow", StringComparison.OrdinalIgnoreCase) && _target is not null)
        {
            var wb = _target.Bounds;
            if (wb.Width > 0 && wb.Height > 0)
            {
                // PrintWindow 只能整窗渲染：先整窗抓，再按屏幕坐标裁出 region（第一版直接按 region 建位图，画面被拉花）。
                using var full = new Bitmap(wb.Width, wb.Height, PixelFormat.Format32bppArgb);
                var ok = false;
                using (var g0 = Graphics.FromImage(full))
                {
                    var hdc = g0.GetHdc();
                    try
                    {
                        // PW_RENDERFULLCONTENT=2：D3D/合成窗口能否渲染在 R12 未定论，本原型实测见报告。
                        ok = Win32.PrintWindow(_target.Hwnd, hdc, 2);
                    }
                    finally
                    {
                        g0.ReleaseHdc(hdc);
                    }
                }

                if (ok)
                {
                    var rel = Rectangle.Intersect(
                        new Rectangle(region.X - wb.Left, region.Y - wb.Top, region.Width, region.Height),
                        new Rectangle(0, 0, full.Width, full.Height));
                    if (rel.Width > 0 && rel.Height > 0)
                    {
                        var crop = new Bitmap(rel.Width, rel.Height, PixelFormat.Format32bppArgb);
                        using var gc = Graphics.FromImage(crop);
                        gc.DrawImage(full, new Rectangle(0, 0, rel.Width, rel.Height), rel, GraphicsUnit.Pixel);
                        return crop;
                    }
                }
                else
                {
                    _log.Evt("ocr.printwindow_failed", "PrintWindow 返回 false，回退屏幕抓取");
                }
            }
        }

        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }

    /// <summary>Bitmap → SoftwareBitmap(BGRA8) → Windows.Media.Ocr。</summary>
    private string Recognize(Bitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var bytes = new byte[w * h * 4];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < h; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * w * 4, w * 4);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        var buffer = CryptographicBuffer.CreateFromByteArray(bytes);
        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        var task = _engine!.RecognizeAsync(softwareBitmap);
        var ocr = task.AsTask().GetAwaiter().GetResult();
        return ocr?.Text ?? string.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine = null;
    }
}
