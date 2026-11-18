using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Capture;

public sealed record UiaNodeRecord(
    string Path,
    int Depth,
    string ControlType,
    string? Name,
    string? Value,
    string? ValueSource,
    string? ClassName,
    string? AutomationId,
    bool Offscreen,
    int ChildCount);

public sealed class UiaPollResult
{
    public List<TextObservation> Observations { get; } = new();
    public int Nodes;
    public int Errors;
    public bool Truncated;
    public double Ms;
    public double BudgetMs;
    public string? Fatal;
}

/// <summary>
/// UIA 文本面：FlaUI.UIA3（MIT）读 ControlType/Name/Value/Text/LegacyIAccessible。
///
/// 为什么不用 System.Windows.Automation：.NET 8 的 WindowsDesktop 共享框架虽然带
/// UIAutomationClient.dll，但它是 .NET Framework 的 WPF 绑定；FlaUI 在 net8 下是
/// 一等公民，且能直接拿 LegacyIAccessible（Chromium/MSAA 桥）——对 NW.js 这类
/// Chromium 应用是唯一可能读到 DOM 文本的通道。
///
/// 遍历是“控制视图 + 手写 DFS”，带节点数/时间预算：真实 Chromium 窗口的
/// FindAllDescendants 可能一次拉几万个节点，卡住朗读循环。
/// </summary>
public sealed class UiaSource : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly AutomationElement _root;
    private readonly ITreeWalker _walker;
    private readonly Options _o;
    private readonly Log _log;
    private readonly Stopwatch _pollClock = new();
    private bool _disposed;

    /// <summary>
    /// 每个节点“哪种 pattern 能取到文本”的缓存。
    /// 一次 to 探测 Value/Text/Legacy/Name 是 4 次跨进程 UIA 调用；对 14 个节点的
    /// WinForms 窗口实测单轮 725ms（P50）。缓存后第二轮起每节点只剩 1~2 次调用。
    /// 键用节点路径（控制视图里相对稳定），条目 15 秒过期，"没有文本" 5 秒过期。
    /// </summary>
    private readonly Dictionary<string, (string Source, long ExpiresMs)> _caps = new(StringComparer.Ordinal);

    public UiaSource(WindowTarget target, Options options, Log log)
    {
        _o = options;
        _log = log;
        _automation = new UIA3Automation
        {
            ConnectionTimeout = TimeSpan.FromSeconds(2),
            TransactionTimeout = TimeSpan.FromMilliseconds(600),
        };
        _walker = _automation.TreeWalkerFactory.GetControlViewWalker();
        _root = _automation.FromHandle(target.Hwnd);
    }

    public UiaPollResult Poll()
    {
        var sw = Stopwatch.StartNew();
        var result = new UiaPollResult();
        // 单轮预算：PollMs 的 6 倍（下限 300ms）。Chromium 的每个 property 都是一次
        // 跨进程 UIA 调用（实测单次可到 600ms 超时），不设预算会把 100ms 轮询拖成秒级。
        var budget = TimeSpan.FromMilliseconds(Math.Max(300, _o.PollMs * 6));
        var seenText = new HashSet<string>(StringComparer.Ordinal);

        _pollClock.Restart();
        var nowMs = Environment.TickCount64;
        try
        {
            Visit(_root, 0, "0", result, budget, seenText, nowMs);
        }
        catch (Exception ex)
        {
            result.Fatal = ex.GetType().Name + ": " + ex.Message;
        }
        _pollClock.Stop();
        sw.Stop();
        result.Ms = sw.Elapsed.TotalMilliseconds;
        result.BudgetMs = budget.TotalMilliseconds;
        return result;
    }

    private void Visit(AutomationElement el, int depth, string path, UiaPollResult acc, TimeSpan budget, HashSet<string> seenText, long nowMs)
    {
        if (acc.Truncated) return;
        if (acc.Nodes >= _o.MaxNodes)
        {
            acc.Truncated = true;
            return;
        }
        // 每 16 个节点查一次预算：Stopwatch.Elapsed 本身很便宜，但没必要每节点都查
        if ((acc.Nodes & 15) == 0 && _pollClock.Elapsed > budget)
        {
            acc.Truncated = true;
            return;
        }
        if (depth > 48)
        {
            acc.Truncated = true;
            return;
        }

        acc.Nodes++;
        ControlType ct = ControlType.Custom;
        string name = string.Empty;
        bool offscreen = false;
        try
        {
            // 只读轮询真正需要的三项；ClassName/AutomationId 是每次额外的跨进程调用，
            // 只在 --dump 时读（--probe 也走 Dump 的简化路径）。
            ct = el.ControlType;
            name = el.Name ?? string.Empty;
            offscreen = el.IsOffscreen;
        }
        catch
        {
            acc.Errors++;
        }

        if (depth > 0 && !offscreen)
        {
            var (text, source) = ExtractText(el, ct, name, path, nowMs);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var norm = TextNormalizer.Normalize(text, _o.MaxLen);
                if (norm.Length > 0 && seenText.Add(norm))
                {
                    _ = source;
                    acc.Observations.Add(new TextObservation("uia", path, ct.ToString(), norm, offscreen));
                }
            }
        }

        AutomationElement? child = null;
        try { child = _walker.GetFirstChild(el); }
        catch { acc.Errors++; }

        var index = 0;
        while (child is not null)
        {
            if (acc.Truncated || acc.Nodes >= _o.MaxNodes) { acc.Truncated = true; break; }
            Visit(child, depth + 1, path + "." + index, acc, budget, seenText, nowMs);
            index++;
            try { child = _walker.GetNextSibling(child); }
            catch { acc.Errors++; break; }
        }
    }

    /// <summary>
    /// 取文本：先按缓存里已成功的通道取（1~2 次调用），首次访问才走完整探测链
    /// Value → TextPattern → LegacyIAccessible → Name。
    /// 顺序理由：Value 是控件值（Edit/ComboBox）；Text 是文档正文（Electron/CEF 的
    /// Document 节点）；LegacyIA 是 Chromium 老版本唯一能过的桥；Name 兜底。
    /// 每一步单独 try：某些元素的 pattern 会抛 COMException（元素已销毁）。
    /// </summary>
    private (string? Text, string Source) ExtractText(AutomationElement el, ControlType ct, string name, string path, long nowMs)
    {
        string? cached = null;
        if (_caps.TryGetValue(path, out var cap) && cap.ExpiresMs > nowMs) cached = cap.Source;

        if (cached == "none") return (null, "none");

        if (cached is not null)
        {
            var hit = TrySource(el, ct, name, cached);
            if (hit.Text is not null)
            {
                _caps[path] = (hit.Source, nowMs + 15000);
                return hit;
            }
        }

        foreach (var src in ProbeOrder(ct))
        {
            if (src == cached) continue;
            var hit = TrySource(el, ct, name, src);
            if (hit.Text is not null)
            {
                if (_caps.Count > 3000) _caps.Clear();
                _caps[path] = (hit.Source, nowMs + 15000);
                return hit;
            }
        }

        if (_caps.Count > 3000) _caps.Clear();
        _caps[path] = ("none", nowMs + 5000);
        return (null, "none");
    }

    private static IEnumerable<string> ProbeOrder(ControlType ct) => ct switch
    {
        ControlType.Edit or ControlType.ComboBox or ControlType.Document =>
            new[] { "value", "textpattern", "legacy", "name" },
        ControlType.Text or ControlType.ListItem or ControlType.Pane or ControlType.Custom or ControlType.DataItem =>
            new[] { "textpattern", "legacy", "name" },
        _ => new[] { "legacy", "name" },
    };

    private static (string? Text, string Source) TrySource(AutomationElement el, ControlType ct, string name, string src)
    {
        try
        {
            switch (src)
            {
                case "value":
                {
                    var vp = el.Patterns.Value.PatternOrDefault;
                    var v = vp?.Value.ValueOrDefault;
                    if (!string.IsNullOrWhiteSpace(v)) return (v, "value");
                    break;
                }
                case "textpattern":
                {
                    if (ct is not (ControlType.Document or ControlType.Edit or ControlType.Text or ControlType.ListItem or ControlType.Pane or ControlType.Custom or ControlType.DataItem))
                        break;
                    var tp = el.Patterns.Text.PatternOrDefault;
                    var t = tp?.DocumentRange.GetText(2000);
                    if (!string.IsNullOrWhiteSpace(t)) return (t, "textpattern");
                    break;
                }
                case "legacy":
                {
                    var legacy = el.Patterns.LegacyIAccessible.PatternOrDefault;
                    var v = legacy?.Value.ValueOrDefault;
                    if (!string.IsNullOrWhiteSpace(v)) return (v, "legacy");
                    break;
                }
                case "name":
                {
                    if (!string.IsNullOrWhiteSpace(name)) return (name, "name");
                    break;
                }
            }
        }
        catch { }
        return (null, "none");
    }

    // ---------------------------------------------------------------- dump

    /// <summary>把整棵控制视图树 dump 出来（证据用），返回 (tree, summary)。</summary>
    public (Dictionary<string, object?> Tree, Dictionary<string, object?> Summary) Dump(WindowTarget target)
    {
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromMilliseconds(_o.DumpBudgetMs);
        _caps.Clear();
        int nodes = 0, textNodes = 0, errors = 0;
        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var samples = new List<string>();
        bool truncated = false;

        Dictionary<string, object?> VisitDump(AutomationElement el, int depth, string path)
        {
            nodes++;
            var node = new Dictionary<string, object?> { ["path"] = path, ["depth"] = depth };
            ControlType ct = ControlType.Custom;
            string name = "", cls = "", autoId = "";
            bool offscreen = false;
            try
            {
                ct = el.ControlType;
                name = el.Name ?? "";
                cls = el.ClassName ?? "";
                autoId = el.AutomationId ?? "";
                offscreen = el.IsOffscreen;
            }
            catch { errors++; }

            var key = ct.ToString();
            histogram[key] = histogram.TryGetValue(key, out var c) ? c + 1 : 1;

            var (text, source) = (depth == 0) ? (null, "root") : ExtractText(el, ct, name, path, Environment.TickCount64);
            if (!string.IsNullOrWhiteSpace(text))
            {
                textNodes++;
                var norm = TextNormalizer.Normalize(text, _o.MaxLen);
                node["text"] = norm;
                node["textSource"] = source;
                if (samples.Count < 12 && norm.Length >= 2) samples.Add($"{key}:{norm}");
            }

            node["controlType"] = key;
            if (name.Length > 0) node["name"] = name.Length > 200 ? name[..200] : name;
            if (cls.Length > 0) node["className"] = cls;
            if (autoId.Length > 0) node["automationId"] = autoId;
            node["offscreen"] = offscreen;

            var children = new List<Dictionary<string, object?>>();
            AutomationElement? child = null;
            try { child = _walker.GetFirstChild(el); } catch { errors++; }
            var i = 0;
            while (child is not null)
            {
                if (nodes >= _o.MaxNodes || sw.Elapsed > budget) { truncated = true; break; }
                children.Add(VisitDump(child, depth + 1, path + "." + i));
                i++;
                try { child = _walker.GetNextSibling(child); } catch { errors++; break; }
            }
            node["childCount"] = i;
            if (children.Count > 0) node["children"] = children;
            return node;
        }

        Dictionary<string, object?> tree;
        try
        {
            tree = VisitDump(_root, 0, "0");
        }
        catch (Exception ex)
        {
            tree = new Dictionary<string, object?> { ["error"] = ex.GetType().Name + ": " + ex.Message };
        }
        sw.Stop();

        var classification = textNodes >= 1 ? "accessible" : (nodes > 1 ? "canvas-only" : "unknown");
        var summary = new Dictionary<string, object?>
        {
            ["mode"] = "uia-dump",
            ["hwnd"] = $"0x{target.Hwnd.ToInt64():X}",
            ["pid"] = target.Pid,
            ["exe"] = target.ProcessName,
            ["title"] = target.Title,
            ["className"] = target.ClassName,
            ["bounds"] = target.Bounds.ToString(),
            ["nodes"] = nodes,
            ["textNodes"] = textNodes,
            ["errors"] = errors,
            ["truncated"] = truncated,
            ["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            ["classification"] = classification,
            ["controlTypes"] = histogram,
            ["textSamples"] = samples,
        };
        return (tree, summary);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _automation.Dispose(); } catch { }
    }
}
