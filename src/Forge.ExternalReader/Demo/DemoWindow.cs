using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GalA11yForge.ExternalReader.Demo;

/// <summary>
/// 自控“台词窗口”：一个 WinForms 窗口，每 N 毫秒换一句台词，
/// 同时把真值（seq/文本/时间）写进 JSONL —— 这样“读到的文本”和“屏幕上的文本”
/// 可以按时间戳精确对齐，去重/延迟/漏句都能算出来。
///
/// 故意的三种控件：
///   * 原生 TextBox / Label —— UIA 与 WM_GETTEXT 都能读到；
///   * 自绘 Panel（GDI 画字）—— UIA 只有空 Pane，模拟 Galgame 自绘文本；
///   * 全角空格、单字、超长行、中英混排 —— 覆盖归一化/截断/最短长度过滤。
/// </summary>
public static class DemoWindow
{
    public static int Run(Options o, Log log)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new DemoForm(o, log));
        return 0;
    }
}

internal sealed class DemoForm : Form
{
    private static readonly string[] Lines =
    [
        "「早上好，今天也一起加油吧。」",
        "「……你又在发呆了。昨晚没睡好吗？」",
        "「呐，如果我说我想离开这个小镇，你会跟我走吗？」",
        "「别开玩笑了。我们不是约好了吗——要一起去看海。」",
        "「おはよう、今日もいい天気だね。」",                       // 日文：UIA 可读，OCR（zh 引擎）会崩
        "「OK, let me think about it. 这句话是中英混排，用来测试分词与截断。」",
        "　",                                                        // 全角空格：归一化后应被丢弃
        "「嗯」",                                                    // 单字：应被 min-len 过滤
        "「这是一段很长的独白，用来测试截断行为。" +
        "如果读屏器把整段 DOM 都念出来，玩家会等到睡着，所以超长必须截断到一句左右。" +
        "顺便看看中文标点会不会被 SAPI 正确停顿，以及日志里的长度统计是否对得上。" +
        "最后再补一句废话凑长度，让这段文字明显超过两百个字符的上限，验证截断逻辑真的生效。」",
        "「早上好，今天也一起加油吧。」",                          // 与第 1 句完全相同：3 秒窗口内应被去重
    ];

    private readonly Options _o;
    private readonly Log _log;
    private readonly string _truthPath;
    private readonly System.Windows.Forms.Timer _tick;
    private readonly Label _status;
    private readonly Label _label;
    private readonly TextBox _textBox;
    private readonly Panel _canvas;
    private int _seq;
    private string _current = string.Empty;
    private long _lastTickMs;

    public DemoForm(Options o, Log log)
    {
        _o = o;
        _log = log;

        _truthPath = o.TruthPath ?? Path.Combine(
            Path.GetDirectoryName(log.TextPath)!,
            Path.GetFileNameWithoutExtension(log.TextPath) + "-truth.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_truthPath))!);

        Text = "ForgeReaderDemo 台词窗口";
        ClientSize = new Size(940, 640);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(80, 60);
        BackColor = Color.FromArgb(18, 22, 28);
        ForeColor = Color.Gainsboro;
        Font = new Font("Microsoft YaHei UI", 10f);
        KeyPreview = true;

        _status = new Label
        {
            Location = new Point(12, 8),
            Size = new Size(916, 26),
            ForeColor = Color.FromArgb(140, 200, 160),
            Text = "seq=0 | 等待第一句…",
        };

        _label = new Label
        {
            Location = new Point(60, 40),
            Size = new Size(820, 150),
            Font = new Font("Microsoft YaHei UI", 18f),
            ForeColor = Color.FromArgb(235, 235, 235),
            BackColor = Color.FromArgb(28, 34, 42),
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "",
        };

        _canvas = new Panel
        {
            Location = new Point(60, 200),
            Size = new Size(820, 165),
            BackColor = Color.FromArgb(24, 30, 38),
            AccessibleName = "自绘画布(无文本)",
        };
        _canvas.Paint += (_, e) =>
        {
            e.Graphics.Clear(_canvas.BackColor);
            TextRenderer.DrawText(e.Graphics, _current, new Font("Microsoft YaHei UI", 17f),
                new Rectangle(12, 12, _canvas.Width - 24, _canvas.Height - 24),
                Color.FromArgb(230, 230, 230),
                TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
            TextRenderer.DrawText(e.Graphics, "↑ 这块是自绘 Panel：UIA 里只有空 Pane", Font,
                new Rectangle(12, _canvas.Height - 26, _canvas.Width - 24, 20), Color.FromArgb(120, 130, 140));
        };

        _textBox = new TextBox
        {
            Location = new Point(60, 400),
            Size = new Size(820, 180),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Microsoft YaHei UI", 20f),
            BackColor = Color.FromArgb(12, 16, 20),
            ForeColor = Color.FromArgb(240, 240, 240),
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = "台词-原生TextBox",
            Text = "",
        };

        Controls.AddRange([_status, _label, _canvas, _textBox]);

        _tick = new System.Windows.Forms.Timer { Interval = Math.Max(200, o.DemoTimerMs) };
        _tick.Tick += (_, _) => NextLine();

        Shown += (_, _) =>
        {
            Activate();
            NextLine();
            _tick.Start();
            if (o.Seconds > 0)
            {
                var close = new System.Windows.Forms.Timer { Interval = o.Seconds * 1000 };
                close.Tick += (_, _) => { close.Stop(); Close(); };
                close.Start();
            }
        };

        _log.Evt("demo.start", null, ("truth", _truthPath), ("timerMs", _tick.Interval),
            ("client", ClientRectangle.ToString()), ("screenBounds", Bounds.ToString()), ("lines", Lines.Length));
    }

    private void NextLine()
    {
        var text = Lines[_seq % Lines.Length];
        var seq = _seq++;
        _current = text;
        _lastTickMs = _log.ElapsedMs;

        _textBox.Text = text;
        _label.Text = text;
        _canvas.Invalidate();
        _status.Text = $"seq={seq} | len={text.Length} | tick={_o.DemoTimerMs}ms | truth: {Path.GetFileName(_truthPath)}";

        var now = DateTimeOffset.Now;
        var record = new
        {
            seq,
            text,
            len = text.Length,
            tUtc = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            tLocal = now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            elapsedMs = _lastTickMs,
            window = Bounds.ToString(),
            textBoxScreen = RectangleToScreen(_textBox.Bounds).ToString(),
        };
        try
        {
            File.AppendAllText(_truthPath, JsonSerializer.Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _log.Evt("demo.truth_error", ex.Message);
        }

        var (len, head, hash) = TextNormalizer.Fingerprint(text);
        _log.Evt("demo.line", null, ("seq", seq), ("len", len), ("head", head), ("hash", hash),
            ("elapsedMs", _lastTickMs), ("text", _o.Verbose ? text : null));
    }
}
