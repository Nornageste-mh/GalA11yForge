using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GalA11yForge.ExternalReader.Events;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Runtime;

/// <summary>
/// --events 模式：附着 → 订阅 UIA 事件 → 主线程只做心跳/存活检查 → 汇总。
/// 与 ReaderEngine 的轮询模式共用 WindowLocator/Log，但完全不调用 UiaSource.Poll。
/// </summary>
public static class EventMode
{
    public static int Run(Options o, Log log)
    {
        var watchdogMs = o.WatchdogMs <= 0 ? 0 : Math.Max(o.WatchdogMs, o.AttachTimeoutMs + 5000);
        using var watchdog = new HardWatchdog(log, watchdogMs, "attach");
        watchdog.Beat("attach");

        var attachSw = Stopwatch.StartNew();
        var attachDeadline = log.ElapsedMs + Math.Max(0, o.AttachTimeoutMs);
        WindowTarget? target = null;
        var attempts = 0;
        while (true)
        {
            attempts++;
            target = WindowLocator.Find(o.Hwnd, o.Pid, o.Title, o.Exe, o.IncludeInvisible, log);
            if (target is not null) break;
            if (log.ElapsedMs >= attachDeadline) break;
            log.Evt("attach.retry", null, ("attempt", attempts), ("waitedMs", log.ElapsedMs));
            watchdog.Beat("attach-retry");
            Thread.Sleep(250);
        }

        if (target is null)
        {
            log.Evt("attach.failed", "找不到匹配的可见窗口（--pid / --title / --exe）", ("attempts", attempts));
            return 2;
        }

        log.Evt("attach.ok", null,
            ("hwnd", $"0x{target.Hwnd.ToInt64():X}"), ("pid", target.Pid), ("exe", target.ProcessName),
            ("class", target.ClassName), ("title", target.Title), ("bounds", target.Bounds.ToString()),
            ("client", target.ClientBounds.ToString()),
            ("foreground", Win32.GetForegroundWindow() == target.Hwnd),
            ("attachMs", attachSw.ElapsedMilliseconds), ("attempts", attempts));

        var csv = string.IsNullOrWhiteSpace(o.EventCsv) ? Derive(log, "-events.csv") : o.EventCsv!;
        var raw = string.IsNullOrWhiteSpace(o.EventRawCsv) ? Derive(log, "-events-raw.csv") : o.EventRawCsv!;

        watchdog.Beat("subscribe");
        using var feed = new UiaEventFeed(target, o, log, csv, raw);
        if (!feed.Start())
        {
            log.Evt("events.start_failed", "属性事件订阅失败；事件模式不能替代轮询", ("csv", csv), ("raw", raw));
            return 4;
        }

        log.Evt("events.started", null,
            ("setupMs", feed.SetupMs), ("csv", csv), ("raw", raw), ("watchdogMs", watchdogMs),
            ("focus", feed.FocusSubscribed), ("property", feed.PropertySubscribed), ("structure", feed.StructureSubscribed),
            ("scope", "Subtree"), ("seconds", o.Seconds));

        watchdog.Beat("run");
        var proc = Process.GetCurrentProcess();
        var cpuStart = proc.TotalProcessorTime;
        var runSw = Stopwatch.StartNew();

        long deadline = o.Seconds > 0 ? log.ElapsedMs + o.Seconds * 1000L : long.MaxValue;
        long nextAlive = 0, nextBeat = 0;
        var stopReason = "deadline";
        var windowNote = "";

        try
        {
            while (true)
            {
                long now = log.ElapsedMs;
                if (now >= deadline) { stopReason = "deadline"; break; }
                watchdog.Beat("run");

                if (now >= nextAlive)
                {
                    nextAlive = now + 500;
                    bool alive = WindowLocator.StillAlive(target);
                    if (!alive)
                    {
                        var rc = WindowLocator.Recheck(target);
                        windowNote = rc.Reason;
                        if (!rc.Alive)
                        {
                            stopReason = "window-closed";
                            log.Evt("window.closed", "目标窗口已关闭，退出", ("why", rc.Reason));
                            break;
                        }
                        windowNote = "rehomed:" + rc.Reason;
                        stopReason = "window-rehomed";
                        log.Evt("window.rehomed", "窗口句柄已重建，事件根失效；退出（本原型不自动重订阅）",
                            ("why", rc.Reason));
                        break;
                    }
                }

                if (now >= nextBeat)
                {
                    nextBeat = now + 1000;
                    log.Evt("events.heartbeat", null,
                        ("events", feed.Total), ("props", feed.PropertyEvents), ("focus", feed.FocusEvents),
                        ("structure", feed.StructureEvents), ("raw", feed.RawRows), ("enriched", feed.Enriched),
                        ("queue", feed.Pending), ("dropped", feed.Dropped),
                        ("firstMs", feed.FirstEventMs), ("lastMs", feed.LastEventMs),
                        ("enrichAvgMs", Math.Round(feed.AvgEnrichMs, 1)), ("enrichMaxMs", Math.Round(feed.MaxEnrichMs, 1)),
                        ("enrichErrors", feed.EnrichErrors));
                }

                Thread.Sleep(50);
            }
        }
        finally
        {
            log.Evt("events.stopping", null, ("reason", stopReason), ("note", windowNote));
            watchdog.Beat("unsubscribe");
            var stopSw = Stopwatch.StartNew();
            feed.Stop();
            log.Evt("events.stopped", null, ("ms", stopSw.ElapsedMilliseconds), ("events", feed.Total),
                ("raw", feed.RawRows), ("enriched", feed.Enriched), ("queue", feed.Pending),
                ("dropped", feed.Dropped));
        }

        runSw.Stop();
        var cpuEnd = proc.TotalProcessorTime;
        var cpuMs = (cpuEnd - cpuStart).TotalMilliseconds;
        var elapsedMs = runSw.ElapsedMilliseconds;
        var cores = Math.Max(1, Environment.ProcessorCount);

        var summary = new Dictionary<string, object?>
        {
            ["mode"] = "events-summary",
            ["runId"] = log.RunId,
            ["qpcFreq"] = Stopwatch.Frequency,
            ["log"] = log.TextPath,
            ["jsonl"] = log.JsonPath,
            ["csv"] = feed.CsvPath,
            ["rawCsv"] = feed.RawPath,
            ["elapsedMs"] = log.ElapsedMs,
            ["runMs"] = elapsedMs,
            ["secondsRequested"] = o.Seconds,
            ["stopReason"] = stopReason,
            ["stopNote"] = windowNote,
            ["watchdogMs"] = watchdogMs,
            ["target"] = new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{target.Hwnd.ToInt64():X}",
                ["pid"] = target.Pid,
                ["exe"] = target.ProcessName,
                ["class"] = target.ClassName,
                ["title"] = target.Title,
                ["bounds"] = target.Bounds.ToString(),
            },
            ["setup"] = new Dictionary<string, object?>
            {
                ["totalMs"] = feed.SetupMs,
                ["focusSubscribed"] = feed.FocusSubscribed,
                ["propertySubscribed"] = feed.PropertySubscribed,
                ["structureSubscribed"] = feed.StructureSubscribed,
                ["scope"] = "Subtree",
            },
            ["events"] = new Dictionary<string, object?>
            {
                ["total"] = feed.Total,
                ["property"] = feed.PropertyEvents,
                ["focus"] = feed.FocusEvents,
                ["structure"] = feed.StructureEvents,
                ["rawRows"] = feed.RawRows,
                ["enriched"] = feed.Enriched,
                ["dropped"] = feed.Dropped,
                ["pendingAtStop"] = feed.Pending,
                ["firstMs"] = feed.FirstEventMs,
                ["lastMs"] = feed.LastEventMs,
                ["firstQpc"] = feed.FirstEventQpc,
                ["lastQpc"] = feed.LastEventQpc,
                ["enrichErrors"] = feed.EnrichErrors,
                ["slowEnrich"] = feed.SlowEnrich,
            },
            ["enrich"] = new Dictionary<string, object?>
            {
                ["avgMs"] = Math.Round(feed.AvgEnrichMs, 3),
                ["maxMs"] = Math.Round(feed.MaxEnrichMs, 3),
            },
            ["cpu"] = new Dictionary<string, object?>
            {
                ["totalMs"] = Math.Round(cpuMs, 1),
                ["pctOfOneCore"] = elapsedMs <= 0 ? 0 : Math.Round(cpuMs / elapsedMs * 100, 2),
                ["pctOfMachine"] = elapsedMs <= 0 ? 0 : Math.Round(cpuMs / elapsedMs / cores * 100, 2),
                ["cores"] = cores,
            },
            ["memory"] = new Dictionary<string, object?>
            {
                ["workingSetMB"] = Math.Round(proc.WorkingSet64 / 1048576.0, 2),
                ["peakWorkingSetMB"] = Math.Round(proc.PeakWorkingSet64 / 1048576.0, 2),
            },
        };

        var path = string.IsNullOrWhiteSpace(o.SummaryPath) ? Derive(log, "-events.summary.json") : o.SummaryPath!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            log.Evt("summary.written", path, ("events", feed.Total), ("cpuMs", Math.Round(cpuMs, 1)),
                ("runMs", elapsedMs));
        }
        catch (Exception ex)
        {
            log.Evt("summary.error", ex.GetType().Name + ": " + ex.Message);
        }

        watchdog.Beat("done");
        return 0;
    }

    private static string Derive(Log log, string suffix)
    {
        var dir = Path.GetDirectoryName(log.TextPath)!;
        var stem = Path.GetFileNameWithoutExtension(log.TextPath);
        return Path.Combine(dir, stem + suffix);
    }
}
