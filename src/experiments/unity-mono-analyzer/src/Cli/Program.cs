using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityMonoAnalyzer.Analysis;
using UnityMonoAnalyzer.Reporting;

namespace UnityMonoAnalyzer.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* 重定向/无控制台时忽略 */ }

        var cli = CliOptions.Parse(args);
        if (cli.Help)
        {
            Console.WriteLine(CliOptions.Usage);
            return 0;
        }
        if (cli.Error != null)
        {
            Console.Error.WriteLine($"✗ 参数错误：{cli.Error}");
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }

        var req = new AnalyzeRequest
        {
            GameInput = cli.Game!,
            ExeOverride = cli.Exe,
            CommandLine = cli.Raw,
        };

        AnalysisResult result;
        try
        {
            result = Analyzer.Run(req);
        }
        catch (AnalyzeException ex)
        {
            Console.Error.WriteLine($"✗ {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ 内部异常：{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 3;
        }

        // ---- 产物落盘 ----
        var outDir = ResolveOutDir(cli.Out, result);
        string mdPath = Path.Combine(outDir, "REPORT.md");
        string jsonPath = Path.Combine(outDir, "analysis.json");
        var tReport = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(outDir);
            var md = MarkdownReport.Render(result, cli.Top);
            File.WriteAllText(mdPath, md, new UTF8Encoding(false));
            result.Timing.ReportMs = tReport.ElapsedMilliseconds;
            JsonReport.Write(result, jsonPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"✗ 写产物失败：{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"  输出目录：{outDir}");
            return 3;
        }

        if (cli.Json) Console.WriteLine(JsonReport.Serialize(result));
        else PrintSummary(result, mdPath, jsonPath);

        return result.ExitCode;
    }

    private static string ResolveOutDir(string? outArg, AnalysisResult result)
    {
        if (!string.IsNullOrWhiteSpace(outArg))
            return Path.GetFullPath(Path.IsPathRooted(outArg) ? outArg : Path.Combine(Directory.GetCurrentDirectory(), outArg));
        var slug = Slug(result.Game.DataDirName);
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "out", slug));
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        var v = sb.ToString().Trim('-');
        while (v.Contains("--", StringComparison.Ordinal)) v = v.Replace("--", "-", StringComparison.Ordinal);
        if (v.Length == 0) v = "game";
        return v.Length > 60 ? v[..60] : v;
    }

    /// <summary>游戏名取数据目录名去掉 _Data（`我与你共度的七日\game\The seven days..._Data` → 真游戏名，而不是 "game"）。</summary>
    internal static string GameName(AnalysisResult r)
    {
        if (r.Game.Name.Length > 0) return r.Game.Name;
        var n = r.Game.DataDirName;
        if (n.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) n = n[..^5];
        if (n.Length > 0) return n;
        n = Path.GetFileName(r.Game.GameDir);
        return n.Length > 0 ? n : "(未知)";
    }

    private static void PrintSummary(AnalysisResult r, string mdPath, string jsonPath)
    {
        var c = CultureInfo.InvariantCulture;
        var primary = r.TextSinks.Where(s => s.Role == "primary").ToList();

        Console.WriteLine("=== Unity Mono 文本钩子静态分析 ===");
        Console.WriteLine($"游戏      {GameName(r)}");
        Console.WriteLine($"数据目录  {r.Game.DataDirName}");
        Console.WriteLine(r.Game.ExePath.Length > 0
            ? $"启动入口  {Path.GetFileName(r.Game.ExePath)}  （{r.Game.ExeReason}）"
            : "启动入口  未识别");
        Console.WriteLine(r.Unity.Version.Length > 0
            ? $"Unity     {r.Unity.Version}  （{r.Unity.VersionSource}）"
            : "Unity     未识别");
        Console.WriteLine($"程序集    {Path.GetFileName(r.Assembly.Path)}  {r.Assembly.SizeBytes / 1024.0:0.#} KB  ·  {r.Stats.Types} 类型 / {r.Stats.Methods.ToString("N0", c)} 方法 / {r.Stats.LdstrTotal.ToString("N0", c)} ldstr");
        Console.WriteLine($"文本写入  {r.Stats.TextSinkCallSites} 处 / {r.Stats.TextSinkCallerMethods} 个方法" +
                          (primary.Count > 0 ? $"  （{string.Join("、", primary.Select(s => $"{s.Api}×{s.Calls}"))}）" : ""));
        Console.WriteLine($"框架痕迹  {(r.FrameworkTraces.Count == 0 ? "无" : string.Join("、", r.FrameworkTraces.Select(f => $"{f.DisplayName}（{f.TypeCount} 类型）")))}");
        Console.WriteLine($"候选      A {r.Stats.TierA} / B {r.Stats.TierB} / C {r.Stats.TierC}   共打分 {r.Stats.CandidatesScored} 条，入选 {r.Stats.CandidatesIncluded} 条");
        if (r.Stats.UpstreamCandidateMethods > 0)
            Console.WriteLine($"上游候选  {r.Stats.UpstreamCandidateMethods} 个（语义命名的包装方法）");

        var top = r.Candidates.Take(5).ToList();
        if (top.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Top 候选：");
            foreach (var x in top)
                Console.WriteLine($"  #{x.Rank,-2} {x.Tier}  {x.Score,5:0.#}  {x.Kind,-9} {x.Confidence,-6} {x.MethodFullName}" + (x.Flags.Count > 0 ? $"  [{string.Join(",", x.Flags)}]" : ""));
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("（没有候选达到 C 级阈值，详见报告 §覆盖声明）");
        }

        if (r.Warnings.Count > 0)
        {
            Console.WriteLine();
            foreach (var w in r.Warnings) Console.WriteLine($"⚠️  {w}");
        }

        Console.WriteLine();
        Console.WriteLine($"报告  {mdPath}");
        Console.WriteLine($"JSON  {jsonPath}");
        Console.WriteLine($"耗时  {r.Timing.TotalMs + r.Timing.ReportMs} ms（发现 {r.Timing.DiscoveryMs} / 候选扫描 {r.Timing.CandidateScanMs} / 全量扫描 {r.Timing.FullScanMs} / 打分 {r.Timing.ScoringMs} / 落盘 {r.Timing.ReportMs}）");
        Console.WriteLine($"退出码  {r.ExitCode}");
    }
}
