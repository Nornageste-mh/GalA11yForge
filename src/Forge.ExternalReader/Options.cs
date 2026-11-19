namespace GalA11yForge.ExternalReader;

/// <summary>命令行参数。原型阶段只做扁平解析，不做子命令框架。</summary>
public sealed class Options
{
    public string Mode { get; set; } = "run";     // run | events | probe | dump | ocr-shot | demo | help
    public int? Pid { get; set; }
    public string? Title { get; set; }
    public string? Exe { get; set; }
    public IntPtr? Hwnd { get; set; }
    public bool Children { get; set; }

    public int PollMs { get; set; } = 100;
    public int SettleMs { get; set; } = 220;
    public int RepeatWindowMs { get; set; } = 3000;
    public int MaxLen { get; set; } = 200;
    public int MinLen { get; set; } = 2;
    public int HeartbeatMs { get; set; } = 5000;

    public bool UseUia { get; set; } = true;
    public bool UseWin32 { get; set; }
    public int MaxNodes { get; set; } = 2500;
    public int DumpBudgetMs { get; set; } = 20000;

    public bool Ocr { get; set; }
    public string? Region { get; set; }          // "x,y,w,h" 屏幕坐标 或 "auto"（窗口下方区域）
    public string OcrLang { get; set; } = "zh-Hans-CN";
    public int OcrIntervalMs { get; set; } = 800;

    /// <summary>取证用：即使本轮 UIA 有文本也照样跑 OCR（产品路径不该开）。</summary>
    public bool OcrForce { get; set; }
    public string Capture { get; set; } = "screen"; // screen | printwindow
    public string? OcrPngDir { get; set; }

    public string Speech { get; set; } = "auto";  // auto | nvda | sapi | null
    public int SapiRate { get; set; }
    public string? NvdaDll { get; set; }

    public bool Hotkeys { get; set; } = true;
    public int Seconds { get; set; }

    /// <summary>附着重试窗口（毫秒）：窗口还没出现时最多等这么久。</summary>
    public int AttachTimeoutMs { get; set; } = 8000;

    public string? LogPath { get; set; }
    public string? SummaryPath { get; set; }
    public string? OutPath { get; set; }
    public string? Filter { get; set; }
    public bool IncludeInvisible { get; set; }
    public bool Verbose { get; set; }

    /// <summary>过滤窗口外壳文本（标题栏/菜单栏/最小化按钮…）。默认开，--keep-chrome 关。</summary>
    public bool SkipChrome { get; set; } = true;

    /// <summary>命中任一子串的文本一律不念（--skip-text a,b，可重复）。</summary>
    public List<string> SkipTexts { get; } = new();

    // events 模式（task-54）：UIA 事件订阅，替代轮询
    public string? EventCsv { get; set; }
    public string? EventRawCsv { get; set; }
    /// <summary>UIA 调用硬超时（毫秒）；超时由 watchdog 线程强制退出，防 COM 挂起。0=关闭。</summary>
    public int WatchdogMs { get; set; } = 15000;
    public bool FocusEvents { get; set; } = true;
    public bool StructureEvents { get; set; } = true;

    // demo 模式
    public int DemoTimerMs { get; set; } = 900;
    public string? TruthPath { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) continue;
            var name = a[2..];
            string? inline = null;
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                inline = name[(eq + 1)..];
                name = name[..eq];
            }

            string? Next()
            {
                if (inline != null) return inline;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                return null;
            }

            switch (name.ToLowerInvariant())
            {
                case "pid": if (int.TryParse(Next(), out var pid)) o.Pid = pid; break;
                case "title": o.Title = Next(); break;
                case "exe": o.Exe = Next(); break;
                case "hwnd": o.Hwnd = ParseHwnd(Next()); break;
                case "children": o.Children = true; break;
                case "poll": o.PollMs = Int(Next(), o.PollMs); break;
                case "settle": o.SettleMs = Int(Next(), o.SettleMs); break;
                case "repeat-window": o.RepeatWindowMs = Int(Next(), o.RepeatWindowMs); break;
                case "max-len": o.MaxLen = Int(Next(), o.MaxLen); break;
                case "min-len": o.MinLen = Int(Next(), o.MinLen); break;
                case "heartbeat": o.HeartbeatMs = Int(Next(), o.HeartbeatMs); break;
                case "no-uia": o.UseUia = false; break;
                case "win32": o.UseWin32 = true; break;
                case "max-nodes": o.MaxNodes = Int(Next(), o.MaxNodes); break;
                case "dump-budget": o.DumpBudgetMs = Int(Next(), o.DumpBudgetMs); break;
                case "ocr": o.Ocr = true; break;
                case "ocr-force": o.Ocr = true; o.OcrForce = true; break;
                case "region": o.Region = Next(); break;
                case "ocr-lang": o.OcrLang = Next() ?? o.OcrLang; break;
                case "ocr-interval": o.OcrIntervalMs = Int(Next(), o.OcrIntervalMs); break;
                case "capture": o.Capture = (Next() ?? o.Capture).ToLowerInvariant(); break;
                case "ocr-png": o.OcrPngDir = Next(); break;
                case "speech": o.Speech = (Next() ?? o.Speech).ToLowerInvariant(); break;
                case "sapi-rate": o.SapiRate = Int(Next(), o.SapiRate); break;
                case "nvda-dll": o.NvdaDll = Next(); break;
                case "no-hotkeys": o.Hotkeys = false; break;
                case "seconds": o.Seconds = Int(Next(), o.Seconds); break;
                case "log": o.LogPath = Next(); break;
                case "summary": o.SummaryPath = Next(); break;
                case "out": o.OutPath = Next(); break;
                case "filter": o.Filter = Next(); break;
                case "keep-chrome": o.SkipChrome = false; break;
                case "skip-text":
                {
                    var v = Next();
                    if (!string.IsNullOrWhiteSpace(v))
                        foreach (var part in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            o.SkipTexts.Add(part);
                    break;
                }
                case "include-invisible": o.IncludeInvisible = true; break;
                case "verbose": o.Verbose = true; break;
                case "demo-timer": o.DemoTimerMs = Int(Next(), o.DemoTimerMs); break;
                case "truth": o.TruthPath = Next(); break;
                case "events": o.Mode = "events"; break;
                case "event-csv": o.EventCsv = Next(); break;
                case "event-raw": o.EventRawCsv = Next(); break;
                case "watchdog": o.WatchdogMs = Int(Next(), o.WatchdogMs); break;
                case "no-focus-events": o.FocusEvents = false; break;
                case "no-structure-events": o.StructureEvents = false; break;
                case "mode": o.Mode = (Next() ?? o.Mode).ToLowerInvariant(); break;
                case "probe": o.Mode = "probe"; break;
                case "dump": o.Mode = "dump"; break;
                case "ocr-shot": o.Mode = "ocr-shot"; break;
                case "demo": o.Mode = "demo"; break;
                case "help": o.Mode = "help"; break;
            }
        }
        return o;
    }

    private static int Int(string? s, int fallback) => int.TryParse(s, out var v) ? v : fallback;

    private static IntPtr? ParseHwnd(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return (IntPtr)Convert.ToInt64(s[2..], 16);
            return (IntPtr)long.Parse(s);
        }
        catch
        {
            return null;
        }
    }

    public static string Usage => """
        Forge.ExternalReader — Gal 无障碍辅助 Demo 快速生成工具（GalA11y Demo） 万能层 v1 原型（外部只读读屏）

        附着与探测
          --pid <id>              按进程 id 找主窗口
          --title <substr>        按窗口标题子串找（大小写不敏感，取面积最大者）
          --exe <name>            额外校验进程名（不含 .exe 也可）
          --hwnd <0x...>          直接指定窗口句柄（跳过枚举，用于子窗口/渲染器 HWND）
          --children              列出目标窗口的子窗口 + 每个子窗口的 UIA 文本面
          --probe                 列出所有可见顶层窗口 + 每个窗口的 UIA 文本面摘要
          --dump                  把目标窗口的 UIA 树 dump 成 JSON（--out 指定路径）
          --include-invisible     连不可见窗口一起列（probe 用）

        运行（默认）
          --poll <ms>             UIA 轮询间隔（默认 100）
          --settle <ms>           文本稳定多久才朗读（默认 220）
          --repeat-window <ms>    同一句多久内不重复念（默认 3000）
          --max-len/--min-len     归一化后截断/忽略长度（默认 200 / 2）
          --win32                 同时用 EnumChildWindows+WM_GETTEXT 补文本
          --no-uia                只用 Win32 文本面
          --keep-chrome           连窗口外壳文本（标题栏/菜单栏/最小化按钮）一起念
          --skip-text a,b         命中任一子串的文本不念（可重复，逗号分隔）
          --seconds <n>           跑 n 秒自动退出（0=直到热键/窗口关闭）

        事件订阅（--events；task-54 原型，与轮询 A/B）
          --events                订阅 UIA 事件（焦点/Name/Value/结构），不轮询
          --event-csv <path>      事件富化 CSV（默认与 --log 同目录 *-events.csv）
          --event-raw <path>      事件原始 CSV（回调线程直写，防富化卡死）
          --watchdog <ms>         UIA 调用硬超时，超时强制退出码 90（默认 15000，0=关）
          --no-focus-events       不订阅全局焦点事件
          --no-structure-events   不订阅结构变化事件

        OCR 兜底（实验性，默认关）
          --ocr                   开启
          --region x,y,w,h        屏幕坐标区域；缺省 auto=窗口下方 42% 带
          --region auto
          --ocr-lang <tag>        默认 zh-Hans-CN（本机唯一已装）
          --ocr-interval <ms>     默认 800
          --ocr-force             即使 UIA 有文本也跑 OCR（仅用于取证/实测对比）
          --capture screen|printwindow
          --ocr-png <dir>         每帧 PNG 取证（调试）

        朗读
          --speech auto|nvda|sapi|null   auto=NVDA 可用则 NVDA，否则 SAPI
          --sapi-rate <n>         -10..10
          --nvda-dll <path>       指定 nvdaControllerClient64.dll

        其它
          --demo                  自控 WinForms 文本变化窗口（真值写入 --truth）
          --demo-timer <ms>       演示窗口换句间隔（默认 900）
          --log <path>            日志基名（默认 %TEMP%\forgereader\<time>-<mode>.log）
          --summary <path>        退出时写统计 JSON
          --no-hotkeys            不注册全局热键
          --verbose

        热键：F8 暂停/继续  F9 重读最后一句  F10 退出
        """;
}
