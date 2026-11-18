using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using GalA11yForge.ExternalReader.Capture;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Runtime;

/// <summary>
/// 一次性取证模式：probe（扫一遍所有窗口的 UIA 文本面）、dump（整棵树）、ocr-shot（单帧）。
/// 这些都是“不改目标进程、不落游戏目录”的只读操作，报告里的矩阵就靠它们出数据。
/// </summary>
public static class ProbeModes
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ probe

    public static int Probe(Options o, Log log)
    {
        if (o.Children) return ProbeChildren(o, log);

        var all = WindowLocator.Enumerate(o.IncludeInvisible);
        IEnumerable<WindowCandidate> q = all;
        if (o.Pid.HasValue) q = q.Where(c => c.Pid == o.Pid.Value);
        if (!string.IsNullOrWhiteSpace(o.Title)) q = q.Where(c => c.Title.Contains(o.Title, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(o.Exe))
        {
            var want = o.Exe!.Trim();
            if (want.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) want = want[..^4];
            q = q.Where(c => c.ProcessName.Equals(want, StringComparison.OrdinalIgnoreCase));
        }

        var cands = q.OrderByDescending(c => c.Area).Take(Math.Max(1, o.MaxNodes > 40 ? 40 : 24)).ToList();
        log.Evt("probe.start", null, ("windows", all.Count), ("selected", cands.Count),
            ("includeInvisible", o.IncludeInvisible));

        var probeOpts = new Options
        {
            MaxNodes = 600,
            PollMs = 400,          // Poll 预算 = PollMs*4 = 1.6s，足够看清 Chromium 窗口
            MaxLen = o.MaxLen,
        };

        var rows = new List<Dictionary<string, object?>>();
        foreach (var c in cands)
        {
            var row = new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{c.Hwnd.ToInt64():X}",
                ["pid"] = c.Pid,
                ["exe"] = c.ProcessName,
                ["title"] = c.Title,
                ["className"] = c.ClassName,
                ["area"] = c.Area,
                ["visible"] = c.Visible,
                ["minimized"] = c.Minimized,
            };

            UiaSource? uia = null;
            try
            {
                uia = new UiaSource(new WindowTarget(c.Hwnd, c.Pid, c.ProcessName, c.Title, c.ClassName, c.Bounds, c.Bounds), probeOpts, log);
                var r = uia.Poll();
                row["uia"] = new Dictionary<string, object?>
                {
                    ["ok"] = r.Fatal is null,
                    ["nodes"] = r.Nodes,
                    ["textNodes"] = r.Observations.Count,
                    ["errors"] = r.Errors,
                    ["truncated"] = r.Truncated,
                    ["ms"] = Math.Round(r.Ms, 1),
                    ["classification"] = r.Observations.Count > 0 ? "accessible" : (r.Nodes > 1 ? "canvas-only" : "unknown"),
                    ["samples"] = r.Observations.Take(3).Select(x => x.Text.Length > 60 ? x.Text[..60] : x.Text).ToList(),
                    ["error"] = r.Fatal,
                };
            }
            catch (Exception ex)
            {
                row["uia"] = new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
            }
            finally
            {
                uia?.Dispose();
            }

            if (o.UseWin32 || true)
            {
                try
                {
                    var w = new Win32Source(new WindowTarget(c.Hwnd, c.Pid, c.ProcessName, c.Title, c.ClassName, c.Bounds, c.Bounds), o).Poll();
                    row["win32"] = new Dictionary<string, object?>
                    {
                        ["children"] = w.ChildCount,
                        ["withText"] = w.WithText,
                        ["texts"] = w.Observations.Count,
                        ["ms"] = Math.Round(w.Ms, 1),
                        ["samples"] = w.Observations.Take(3).Select(x => x.Text.Length > 60 ? x.Text[..60] : x.Text).ToList(),
                    };
                }
                catch (Exception ex)
                {
                    row["win32"] = new Dictionary<string, object?> { ["error"] = ex.GetType().Name + ": " + ex.Message };
                }
            }

            rows.Add(row);
            log.Evt("probe.window", null,
                ("hwnd", row["hwnd"]), ("pid", c.Pid), ("exe", c.ProcessName),
                ("title", c.Title.Length > 48 ? c.Title[..48] : c.Title),
                ("uiaNodes", (row["uia"] as Dictionary<string, object?>)?["nodes"]),
                ("uiaTexts", (row["uia"] as Dictionary<string, object?>)?["textNodes"]),
                ("uiaMs", (row["uia"] as Dictionary<string, object?>)?["ms"]),
                ("win32Texts", (row["win32"] as Dictionary<string, object?>)?["texts"]));
        }

        var outPath = o.OutPath ?? Path.Combine(Path.GetDirectoryName(log.TextPath)!, Path.GetFileNameWithoutExtension(log.TextPath) + "-probe.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["mode"] = "probe",
            ["t"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["windowsTotal"] = all.Count,
            ["rows"] = rows,
        }, JsonOpts));
        log.Evt("probe.done", outPath, ("rows", rows.Count));

        // 人类可读摘要（终端）
        Console.WriteLine($"probe: {rows.Count} 窗口 (共 {all.Count}) → {outPath}");
        Console.WriteLine($"{"hwnd",-10} {"pid",-6} {"exe",-18} {"uiaNodes",8} {"uiaTexts",8} {"uiaMs",7} {"w32Texts",8}  title");
        foreach (var r in rows)
        {
            var uia = r["uia"] as Dictionary<string, object?>;
            var w32 = r["win32"] as Dictionary<string, object?>;
            var title = (r["title"] as string) ?? "";
            if (title.Length > 34) title = title[..34];
            Console.WriteLine($"{r["hwnd"],-10} {r["pid"],-6} {Trunc((r["exe"] as string) ?? "", 18),-18} " +
                              $"{uia?["nodes"],8} {uia?["textNodes"],8} {uia?["ms"],7} {w32?["texts"],8}  {title}");
        }
        return 0;
    }

    // -------------------------------------------------------------- children

    /// <summary>
    /// 列出目标的子窗口，并逐个把子 HWND 当 UIA 根再探一次。
    /// 动机：Chromium 的正文常常挂在 Chrome_RenderWidgetHostHWND 上，
    /// 从顶层窗口的 UIA 树里看不到（LoveNest 实测 Pane:nwjs 是空的）。
    /// </summary>
    private static int ProbeChildren(Options o, Log log)
    {
        var target = WindowLocator.Find(o.Hwnd, o.Pid, o.Title, o.Exe, o.IncludeInvisible, log);
        if (target is null)
        {
            log.Evt("children.failed", "找不到父窗口");
            return 2;
        }

        var kids = WindowLocator.Children(target.Hwnd);
        log.Evt("children.list", null, ("parent", target.Describe()), ("count", kids.Count));

        var probeOpts = new Options { MaxNodes = 600, PollMs = 400, MaxLen = o.MaxLen };
        var rows = new List<Dictionary<string, object?>>();
        foreach (var k in kids)
        {
            var row = new Dictionary<string, object?>
            {
                ["hwnd"] = k.Handle,
                ["className"] = k.ClassName,
                ["win32Text"] = k.Text.Length > 80 ? k.Text[..80] : k.Text,
                ["visible"] = k.Visible,
                ["bounds"] = k.Bounds.ToString(),
            };

            UiaSource? uia = null;
            try
            {
                var childTarget = new WindowTarget(k.Hwnd, target.Pid, target.ProcessName, k.Text, k.ClassName, k.Bounds, k.Bounds);
                uia = new UiaSource(childTarget, probeOpts, log);
                var r = uia.Poll();
                row["uia"] = new Dictionary<string, object?>
                {
                    ["ok"] = r.Fatal is null,
                    ["nodes"] = r.Nodes,
                    ["textNodes"] = r.Observations.Count,
                    ["truncated"] = r.Truncated,
                    ["ms"] = Math.Round(r.Ms, 1),
                    ["error"] = r.Fatal,
                    ["samples"] = r.Observations.Take(5).Select(x => x.Text.Length > 70 ? x.Text[..70] : x.Text).ToList(),
                };
            }
            catch (Exception ex)
            {
                row["uia"] = new Dictionary<string, object?> { ["ok"] = false, ["error"] = ex.GetType().Name + ": " + ex.Message };
            }
            finally
            {
                uia?.Dispose();
            }

            rows.Add(row);
            var uiaRow = row["uia"] as Dictionary<string, object?>;
            log.Evt("children.window", null,
                ("hwnd", k.Handle), ("class", k.ClassName), ("nodes", uiaRow?["nodes"]),
                ("texts", uiaRow?["textNodes"]), ("ms", uiaRow?["ms"]), ("error", uiaRow?["error"]));
        }

        var outPath = o.OutPath ?? Path.Combine(Path.GetDirectoryName(log.TextPath)!, Path.GetFileNameWithoutExtension(log.TextPath) + "-children.json");
        File.WriteAllText(outPath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["mode"] = "children",
            ["t"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["parent"] = target.Describe(),
            ["rows"] = rows,
        }, JsonOpts));

        Console.WriteLine($"children: {rows.Count} → {outPath}");
        Console.WriteLine($"{"hwnd",-10} {"nodes",6} {"texts",6} {"ms",7}  class / win32Text");
        foreach (var r in rows)
        {
            var u = r["uia"] as Dictionary<string, object?>;
            var line = $"{r["hwnd"],-10} {u?["nodes"],6} {u?["textNodes"],6} {u?["ms"],7}  {r["className"]}";
            var wt = r["win32Text"] as string;
            if (!string.IsNullOrEmpty(wt)) line += " | " + Trunc(wt, 40);
            Console.WriteLine(line);
            if (u?["samples"] is List<string> ss)
                foreach (var s in ss) Console.WriteLine("        - " + Trunc(s, 90));
        }
        return 0;
    }

    // ------------------------------------------------------------------ dump

    public static int Dump(Options o, Log log)
    {
        var target = WindowLocator.Find(o.Hwnd, o.Pid, o.Title, o.Exe, o.IncludeInvisible, log);
        if (target is null)
        {
            log.Evt("dump.failed", "找不到目标窗口");
            return 2;
        }

        using var uia = new UiaSource(target, o, log);
        var (tree, summary) = uia.Dump(target);

        var win32 = new Win32Source(target, o).Poll();
        summary["win32"] = new Dictionary<string, object?>
        {
            ["children"] = win32.ChildCount,
            ["withText"] = win32.WithText,
            ["texts"] = win32.Observations.Take(50).Select(x => new Dictionary<string, object?>
            {
                ["node"] = x.NodeId,
                ["controlType"] = x.ControlType,
                ["len"] = x.Text.Length,
                ["text"] = x.Text.Length > 120 ? x.Text[..120] : x.Text,
            }).ToList(),
        };

        var payload = new Dictionary<string, object?> { ["summary"] = summary, ["tree"] = tree };
        var outPath = o.OutPath ?? Path.Combine(Path.GetDirectoryName(log.TextPath)!, Path.GetFileNameWithoutExtension(log.TextPath) + "-uia.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload, JsonOpts));
        log.Json("uia.dump_summary", summary);
        log.Evt("dump.done", outPath, ("nodes", summary["nodes"]), ("textNodes", summary["textNodes"]),
            ("classification", summary["classification"]), ("ms", summary["ms"]));

        Console.WriteLine($"dump: {target.Describe()}");
        Console.WriteLine($"      nodes={summary["nodes"]} textNodes={summary["textNodes"]} " +
                          $"errors={summary["errors"]} truncated={summary["truncated"]} ms={summary["ms"]} " +
                          $"→ {summary["classification"]}");
        Console.WriteLine($"      win32 children={win32.ChildCount} withText={win32.WithText}");
        var samples = summary["textSamples"] as List<string>;
        if (samples is { Count: > 0 })
        {
            Console.WriteLine("      samples:");
            foreach (var s in samples) Console.WriteLine("        - " + Trunc(s, 100));
        }
        Console.WriteLine($"      file: {outPath}");
        return 0;
    }

    // -------------------------------------------------------------- ocr-shot

    public static int OcrShot(Options o, Log log)
    {
        WindowTarget? target = null;
        if (o.Pid.HasValue || !string.IsNullOrWhiteSpace(o.Title))
            target = WindowLocator.Find(o.Hwnd, o.Pid, o.Title, o.Exe, o.IncludeInvisible, log);

        using var ocr = new OcrSource(target, o, log);
        if (!ocr.TryInit(out var err))
        {
            log.Evt("ocr.unavailable", err);
            Console.Error.WriteLine("OCR 不可用: " + err);
            return 3;
        }

        var f = ocr.Capture();
        var payload = new Dictionary<string, object?>
        {
            ["mode"] = "ocr-shot",
            ["t"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["target"] = target?.Describe(),
            ["region"] = f.Region,
            ["captureMs"] = Math.Round(f.CaptureMs, 1),
            ["ocrMs"] = Math.Round(f.OcrMs, 1),
            ["totalMs"] = Math.Round(f.TotalMs, 1),
            ["lines"] = f.Lines,
            ["chars"] = f.Chars,
            ["png"] = f.PngPath,
            ["error"] = f.Error,
            ["text"] = f.Text,
        };
        var outPath = o.OutPath ?? Path.Combine(Path.GetDirectoryName(log.TextPath)!, Path.GetFileNameWithoutExtension(log.TextPath) + "-ocr.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload, JsonOpts));
        log.Evt("ocr.shot", null, ("region", f.Region), ("captureMs", Math.Round(f.CaptureMs, 1)),
            ("ocrMs", Math.Round(f.OcrMs, 1)), ("totalMs", Math.Round(f.TotalMs, 1)),
            ("chars", f.Chars), ("lines", f.Lines), ("error", f.Error), ("file", outPath));

        Console.WriteLine($"ocr-shot: region={f.Region} capture={f.CaptureMs:F1}ms ocr={f.OcrMs:F1}ms total={f.TotalMs:F1}ms");
        Console.WriteLine($"          chars={f.Chars} lines={f.Lines} png={f.PngPath ?? "-"}");
        if (f.Error is not null) Console.WriteLine("          error=" + f.Error);
        Console.WriteLine("---------- text ----------");
        Console.WriteLine(f.Text);
        Console.WriteLine("--------------------------");
        Console.WriteLine("file: " + outPath);
        return 0;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];
}
