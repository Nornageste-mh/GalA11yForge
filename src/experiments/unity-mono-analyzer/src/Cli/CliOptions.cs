namespace UnityMonoAnalyzer.Cli;

public sealed class CliOptions
{
    public string? Game { get; private set; }
    public string? Exe { get; private set; }
    public string? Out { get; private set; }
    public bool Json { get; private set; }
    public int Top { get; private set; } = 20;
    public bool Help { get; private set; }
    public string? Error { get; private set; }
    public string Raw { get; private set; } = "";

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions { Raw = "unity-mono-analyzer " + string.Join(" ", args.Select(Quote)) };

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? inline = null;
            var eq = a.IndexOf('=');
            if (a.StartsWith("--", StringComparison.Ordinal) && eq > 2)
            {
                inline = a[(eq + 1)..];
                a = a[..eq];
            }

            string? Next()
            {
                if (inline != null) return inline;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                return null;
            }

            switch (a.ToLowerInvariant())
            {
                case "--game":
                case "-g":
                    o.Game = Next();
                    if (string.IsNullOrWhiteSpace(o.Game)) o.Error ??= "--game 缺少路径参数";
                    break;
                case "--exe":
                case "-e":
                    o.Exe = Next();
                    if (string.IsNullOrWhiteSpace(o.Exe)) o.Error ??= "--exe 缺少路径参数";
                    break;
                case "--out":
                case "-o":
                    o.Out = Next();
                    if (string.IsNullOrWhiteSpace(o.Out)) o.Error ??= "--out 缺少目录参数";
                    break;
                case "--top":
                case "-t":
                    var t = Next();
                    if (!int.TryParse(t, out var n) || n < 1 || n > 500) o.Error ??= $"--top 需要 1..500 的整数，收到：{t ?? "(空)"}";
                    else o.Top = n;
                    break;
                case "--json":
                    o.Json = true;
                    break;
                case "--help":
                case "-h":
                case "-?":
                    o.Help = true;
                    break;
                default:
                    o.Error ??= $"未知参数：{a}";
                    break;
            }
        }

        if (!o.Help && string.IsNullOrWhiteSpace(o.Game)) o.Error ??= "必须提供 --game <游戏目录|启动exe|Assembly-CSharp.dll>";
        return o;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    public static string Usage => """
        unity-mono-analyzer —— Unity Mono 游戏文本钩子点静态分析器（W2-B 原型）

        用法:
          dotnet run -- --game <目录|exe|dll> [--exe <启动exe>] [--out <输出目录>] [--top N] [--json]

        参数:
          --game, -g   游戏安装目录 / 启动 exe / Assembly-CSharp.dll（必填）
          --exe,  -e   显式指定启动 exe（会由 <exe名>_Data 反查数据目录）
          --out,  -o   输出目录（写 analysis.json 与 REPORT.md），默认 .\out\<数据目录名>
          --top,  -t   报告中展示的候选数量，默认 20（1..500）
          --json       只把完整 JSON 打到 stdout（不打印人读摘要）
          --help, -h   显示本帮助

        退出码:
          0  成功，无告警
          2  参数错误（路径不存在 / --exe 反查失败 / 参数非法）
          3  内部异常（程序集损坏、IO 失败等）
          4  不是 Unity Mono（未找到 *_Data\Managed\Assembly-CSharp.dll，或检出 IL2CPP）
          5  部分成功（有告警：Unity 版本未知、程序集解析失败、无候选、多候选并列等）

        示例:
          dotnet run -- --game "D:\Steam\steamapps\common\NEEDY GIRL OVERDOSE" --out .\out\needy
          dotnet run -- --game "D:\Games\MyGame" --exe "D:\Games\MyGame\Windose.exe" --top 30 --json
        """;
}
