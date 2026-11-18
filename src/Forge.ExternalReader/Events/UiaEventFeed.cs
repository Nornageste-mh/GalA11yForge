using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Automation;
using GalA11yForge.ExternalReader.Native;

namespace GalA11yForge.ExternalReader.Events;

/// <summary>
/// UIA 事件订阅（task-54 原型）。
///
/// 线程模型：
///   1) UIA 事件回调线程：只取 QPC/UTC 时间戳 → 写 events-raw.csv → 入队；
///      **绝不**在回调里读 sender 的任何属性（实测每属性约 30ms，且可能被提供程序挂起）。
///   2) 单个富化线程：出队后用 sender 读 hwnd/pid/name/controlType，写 events.csv。
///      即使这个线程卡死，events-raw.csv 仍然完整，延迟证据不丢。
///   3) 主线程：心跳 + 存活检查；HardWatchdog 在另一个线程上盯 UIA 硬超时。
/// </summary>
public sealed class UiaEventFeed : IDisposable
{
    private const string RawHeader =
        "seq,tUtc,qpc,readerMs,kind,eventId,property,oldValue,newValue,objectId,childId,threadId";

    private const string CsvHeader =
        "seq,tUtc,qpc,readerMs,kind,eventId,property,oldValue,newValue,objectId,childId,threadId," +
        "hwnd,pid,controlType,name,automationId,className,runtimeId,inTarget,resolveMs,error";

    private readonly WindowTarget _target;
    private readonly Options _o;
    private readonly Log _log;
    private readonly string _csvPath;
    private readonly string _rawPath;

    private readonly object _rawGate = new();
    private readonly object _csvGate = new();
    private readonly object _cacheGate = new();
    private readonly Dictionary<string, ElementInfo> _cache = new();
    private readonly ConcurrentQueue<RawEvent> _queue = new();

    private StreamWriter? _raw;
    private StreamWriter? _csv;
    private Thread? _enricher;
    private readonly ManualResetEventSlim _stop = new(false);
    private int _stopping;
    private int _disposed;

    private AutomationElement? _root;
    private AutomationFocusChangedEventHandler? _onFocus;
    private AutomationPropertyChangedEventHandler? _onProperty;
    private StructureChangedEventHandler? _onStructure;

    private long _total, _props, _focus, _struct, _dropped, _rawRows, _enriched, _enrichErrors;
    private long _pending, _firstLogged, _firstEventMs, _lastEventMs, _firstEventQpc, _lastEventQpc;
    private long _enrichTicks, _maxEnrichTicks, _slowEnrich;
    private long _startedReaderMs;
    private long _rootMs, _subscribeMs, _setupMs;

    public UiaEventFeed(WindowTarget target, Options o, Log log, string csvPath, string rawPath)
    {
        _target = target;
        _o = o;
        _log = log;
        _csvPath = Path.GetFullPath(csvPath);
        _rawPath = Path.GetFullPath(rawPath);
        if (string.Equals(_csvPath, _rawPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--event-csv 与 --event-raw 不能是同一个文件");

        Directory.CreateDirectory(Path.GetDirectoryName(_csvPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_rawPath)!);
        _raw = OpenCsv(_rawPath, RawHeader);
        _csv = OpenCsv(_csvPath, CsvHeader);
    }

    public bool PropertySubscribed { get; private set; }
    public bool FocusSubscribed { get; private set; }
    public bool StructureSubscribed { get; private set; }
    public long SetupMs => _setupMs;
    public string CsvPath => _csvPath;
    public string RawPath => _rawPath;
    public long Total => Interlocked.Read(ref _total);
    public long PropertyEvents => Interlocked.Read(ref _props);
    public long FocusEvents => Interlocked.Read(ref _focus);
    public long StructureEvents => Interlocked.Read(ref _struct);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long RawRows => Interlocked.Read(ref _rawRows);
    public long Enriched => Interlocked.Read(ref _enriched);
    public long EnrichErrors => Interlocked.Read(ref _enrichErrors);
    public long Pending => Interlocked.Read(ref _pending);
    public long FirstEventMs => Interlocked.Read(ref _firstEventMs);
    public long LastEventMs => Interlocked.Read(ref _lastEventMs);
    public long FirstEventQpc => Interlocked.Read(ref _firstEventQpc);
    public long LastEventQpc => Interlocked.Read(ref _lastEventQpc);
    public long SlowEnrich => Interlocked.Read(ref _slowEnrich);
    public double MaxEnrichMs => Interlocked.Read(ref _maxEnrichTicks) * 1000.0 / Stopwatch.Frequency;
    public double AvgEnrichMs
    {
        get
        {
            var n = Interlocked.Read(ref _enriched);
            return n == 0 ? 0 : Interlocked.Read(ref _enrichTicks) * 1000.0 / Stopwatch.Frequency / n;
        }
    }

    public bool Start()
    {
        _startedReaderMs = _log.ElapsedMs;

        _enricher = new Thread(EnrichLoop) { IsBackground = true, Name = "forge-uia-event-enrich" };
        _enricher.Start();

        var sw = Stopwatch.StartNew();
        try
        {
            _root = AutomationElement.FromHandle(_target.Hwnd);
        }
        catch (Exception ex)
        {
            _rootMs = sw.ElapsedMilliseconds;
            _log.Evt("events.root", null, ("ok", false), ("ms", _rootMs), ("error", Describe(ex)));
            _stop.Set();
            return false;
        }
        _rootMs = sw.ElapsedMilliseconds;
        _log.Evt("events.root", null, ("ok", true), ("ms", _rootMs),
            ("hwnd", $"0x{_target.Hwnd.ToInt64():X}"));

        if (_o.FocusEvents) FocusSubscribed = AddFocus();
        PropertySubscribed = AddProperty();
        if (_o.StructureEvents) StructureSubscribed = AddStructure();

        _setupMs = _rootMs + _subscribeMs;
        if (!PropertySubscribed)
        {
            _log.Evt("events.start", "Name/Value 属性事件订阅失败，无法做 A/B", ("ok", false));
            _stop.Set();
            return false;
        }
        return true;
    }

    private bool AddFocus()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _onFocus = OnFocus;
            Automation.AddAutomationFocusChangedEventHandler(_onFocus);
            _subscribeMs += sw.ElapsedMilliseconds;
            _log.Evt("events.subscribe", null, ("kind", "focus"), ("ok", true), ("ms", sw.ElapsedMilliseconds));
            return true;
        }
        catch (Exception ex)
        {
            _onFocus = null;
            _subscribeMs += sw.ElapsedMilliseconds;
            _log.Evt("events.subscribe", null, ("kind", "focus"), ("ok", false), ("ms", sw.ElapsedMilliseconds),
                ("error", Describe(ex)));
            return false;
        }
    }

    private bool AddProperty()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _onProperty = OnProperty;
            Automation.AddAutomationPropertyChangedEventHandler(
                _root!, TreeScope.Subtree, _onProperty,
                AutomationElement.NameProperty, ValuePattern.ValueProperty);
            _subscribeMs += sw.ElapsedMilliseconds;
            _log.Evt("events.subscribe", null, ("kind", "property"), ("ok", true), ("ms", sw.ElapsedMilliseconds),
                ("properties", "Name,Value"), ("scope", "Subtree"));
            return true;
        }
        catch (Exception ex)
        {
            _onProperty = null;
            _subscribeMs += sw.ElapsedMilliseconds;
            _log.Evt("events.subscribe", null, ("kind", "property"), ("ok", false), ("ms", sw.ElapsedMilliseconds),
                ("error", Describe(ex)));
            return false;
        }
    }

    private bool AddStructure()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _onStructure = OnStructure;
            Automation.AddStructureChangedEventHandler(_root!, TreeScope.Subtree, _onStructure);
            _subscribeMs += sw.ElapsedMilliseconds;
            _log.Evt("events.subscribe", null, ("kind", "structure"), ("ok", true), ("ms", sw.ElapsedMilliseconds),
                ("scope", "Subtree"));
            return true;
        }
        catch (Exception ex)
        {
            _onStructure = null;
            _subscribeMs += sw.ElapsedMilliseconds;
            _log.Evt("events.subscribe", null, ("kind", "structure"), ("ok", false), ("ms", sw.ElapsedMilliseconds),
                ("error", Describe(ex)));
            return false;
        }
    }

    // ------------------------------------------------------------ callbacks

    private void OnProperty(object sender, AutomationPropertyChangedEventArgs e)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        var qpc = Stopwatch.GetTimestamp();
        var utc = DateTime.UtcNow;
        if (sender is not AutomationElement el) return;

        var ev = new RawEvent(
            Interlocked.Increment(ref _total), qpc, utc, "property",
            e.EventId?.ProgrammaticName ?? "", e.Property?.ProgrammaticName ?? "",
            ValueText(e.OldValue), ValueText(e.NewValue), -1, -1, Environment.CurrentManagedThreadId, el);
        Interlocked.Increment(ref _props);
        EmitRaw(ev);
    }

    private void OnFocus(object sender, AutomationFocusChangedEventArgs e)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        var qpc = Stopwatch.GetTimestamp();
        var utc = DateTime.UtcNow;
        if (sender is not AutomationElement el) return;

        var ev = new RawEvent(
            Interlocked.Increment(ref _total), qpc, utc, "focus",
            e.EventId?.ProgrammaticName ?? "", "", "", "", e.ObjectId, e.ChildId,
            Environment.CurrentManagedThreadId, el);
        Interlocked.Increment(ref _focus);
        EmitRaw(ev);
    }

    private void OnStructure(object sender, StructureChangedEventArgs e)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        var qpc = Stopwatch.GetTimestamp();
        var utc = DateTime.UtcNow;
        if (sender is not AutomationElement el) return;

        var ev = new RawEvent(
            Interlocked.Increment(ref _total), qpc, utc, "structure",
            e.EventId?.ProgrammaticName ?? "", "", "", e.StructureChangeType.ToString(),
            -1, -1, Environment.CurrentManagedThreadId, el);
        Interlocked.Increment(ref _struct);
        EmitRaw(ev);
    }

    private void EmitRaw(RawEvent ev)
    {
        var readerMs = _log.ElapsedMs - _startedReaderMs;
        Interlocked.Exchange(ref _lastEventMs, readerMs);
        Interlocked.Exchange(ref _lastEventQpc, ev.Qpc);

        var line = Row(
            ev.Seq, ev.Utc.ToString("O", CultureInfo.InvariantCulture), ev.Qpc, readerMs,
            ev.Kind, ev.EventId, ev.Property, ev.OldValue, ev.NewValue, ev.ObjectId, ev.ChildId, ev.ThreadId);
        lock (_rawGate)
        {
            if (_raw is null) return;
            try
            {
                _raw.WriteLine(line);
                Interlocked.Increment(ref _rawRows);
            }
            catch { }
        }

        if (Interlocked.CompareExchange(ref _firstLogged, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _firstEventMs, readerMs);
            Interlocked.Exchange(ref _firstEventQpc, ev.Qpc);
            _log.Evt("events.first", null, ("kind", ev.Kind), ("sinceStartMs", readerMs),
                ("qpc", ev.Qpc), ("threadId", ev.ThreadId));
        }

        if (Interlocked.Increment(ref _pending) > 20000)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(ev);
    }

    // ------------------------------------------------------------- enricher

    private void EnrichLoop()
    {
        while (true)
        {
            if (_queue.TryDequeue(out var ev))
            {
                Interlocked.Decrement(ref _pending);
                EnrichOne(ev);
                continue;
            }
            if (_stop.IsSet) break;
            _stop.Wait(20);
        }
    }

    private void EnrichOne(RawEvent ev)
    {
        var sw = Stopwatch.StartNew();
        ElementInfo info;
        try
        {
            info = Resolve(ev.Element);
        }
        catch (Exception ex)
        {
            info = new ElementInfo { Error = Describe(ex) };
            Interlocked.Increment(ref _enrichErrors);
        }
        sw.Stop();

        var ticks = sw.ElapsedTicks;
        Interlocked.Add(ref _enrichTicks, ticks);
        if (ticks > Interlocked.Read(ref _maxEnrichTicks))
            Interlocked.Exchange(ref _maxEnrichTicks, ticks);
        if (sw.ElapsedMilliseconds >= 1000 && Interlocked.Increment(ref _slowEnrich) <= 10)
            _log.Evt("events.enrich_slow", null, ("seq", ev.Seq), ("ms", sw.ElapsedMilliseconds),
                ("kind", ev.Kind), ("error", info.Error));

        var name = info.Name;
        // 缓存按 RuntimeId 命中时 Name 是首次解析的旧值；NameProperty 事件的新值就是
        // 元素当前名字，直接采用，避免 CSV 里 name 与 newValue 对不上。
        if (ev.Kind == "property" && ev.Property.EndsWith("NameProperty", StringComparison.Ordinal)
            && ev.NewValue.Length > 0)
            name = ev.NewValue;

        var line = Row(
            ev.Seq, ev.Utc.ToString("O", CultureInfo.InvariantCulture), ev.Qpc,
            _log.ElapsedMs - _startedReaderMs, ev.Kind, ev.EventId, ev.Property, ev.OldValue, ev.NewValue,
            ev.ObjectId, ev.ChildId, ev.ThreadId,
            info.Hwnd, info.Pid, info.ControlType, name, info.AutomationId, info.ClassName,
            info.RuntimeId, info.InTarget, Math.Round(sw.Elapsed.TotalMilliseconds, 3), info.Error);

        lock (_csvGate)
        {
            if (_csv is null) return;
            try
            {
                _csv.WriteLine(line);
                Interlocked.Increment(ref _enriched);
            }
            catch { }
        }
    }

    private ElementInfo Resolve(AutomationElement el)
    {
        string rid = "";
        try { rid = string.Join(".", el.GetRuntimeId()); } catch { }

        if (rid.Length > 0)
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(rid, out var hit)) return hit;
            }
        }

        var info = new ElementInfo { RuntimeId = rid };
        try
        {
            var cur = el.Current;
            info.ControlType = Prop(() => cur.ControlType.ProgrammaticName);
            info.Name = Prop(() => cur.Name);
            info.AutomationId = Prop(() => cur.AutomationId);
            info.ClassName = Prop(() => cur.ClassName);
            info.Hwnd = Prop(() => cur.NativeWindowHandle == 0 ? "" : "0x" + cur.NativeWindowHandle.ToString("X"));
            info.Pid = Prop(() => cur.ProcessId.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            info.Error = Describe(ex);
        }

        if (info.Pid.Length == 0 || info.Pid.StartsWith('!')) info.InTarget = "?";
        else info.InTarget = info.Pid == _target.Pid.ToString(CultureInfo.InvariantCulture) ? "1" : "0";

        if (rid.Length > 0)
        {
            lock (_cacheGate)
            {
                if (_cache.Count < 512) _cache[rid] = info;
            }
        }
        return info;
    }

    private static string Prop(Func<string?> get)
    {
        try { return get() ?? ""; }
        catch (Exception ex) { return "!" + ex.GetType().Name; }
    }

    // ---------------------------------------------------------------- stop

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        var sw = Stopwatch.StartNew();
        if (_root is not null)
        {
            if (_onFocus is not null)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(_onFocus); }
                catch (Exception ex) { _log.Evt("events.unsubscribe", null, ("kind", "focus"), ("ok", false), ("error", Describe(ex))); }
            }
            if (_onProperty is not null)
            {
                try { Automation.RemoveAutomationPropertyChangedEventHandler(_root, _onProperty); }
                catch (Exception ex) { _log.Evt("events.unsubscribe", null, ("kind", "property"), ("ok", false), ("error", Describe(ex))); }
            }
            if (_onStructure is not null)
            {
                try { Automation.RemoveStructureChangedEventHandler(_root, _onStructure); }
                catch (Exception ex) { _log.Evt("events.unsubscribe", null, ("kind", "structure"), ("ok", false), ("error", Describe(ex))); }
            }
        }
        _log.Evt("events.unsubscribe", null, ("ok", true), ("ms", sw.ElapsedMilliseconds));

        _stop.Set();
        if (_enricher is not null && !_enricher.Join(3000))
            _log.Evt("events.enrich_stuck", "富化线程 3 秒未退出（可能卡在 UIA）；raw CSV 不受影响");

        Interlocked.Exchange(ref _disposed, 1);
        lock (_rawGate)
        {
            try { _raw?.Flush(); _raw?.Dispose(); } catch { }
            _raw = null;
        }
        lock (_csvGate)
        {
            try { _csv?.Flush(); _csv?.Dispose(); } catch { }
            _csv = null;
        }
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- utils

    private static StreamWriter OpenCsv(string path, string header)
    {
        var w = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)) { AutoFlush = true };
        w.WriteLine(header);
        return w;
    }

    private static string Row(params object?[] cells) => string.Join(",", cells.Select(Csv));

    private static string Csv(object? v)
    {
        string s = v switch
        {
            null => "",
            string str => str,
            double d => d.ToString("0.###", CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        if (s.Length > 500) s = s[..500];
        if (s.Contains(',') || s.Contains('"')) s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static string ValueText(object? v)
    {
        if (v is null) return "";
        try { return v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""; }
        catch { return "<" + v.GetType().Name + ">"; }
    }

    private static string SafeShort(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= 80 ? s : s[..80];
    }

    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private sealed record RawEvent(
        long Seq, long Qpc, DateTime Utc, string Kind, string EventId, string Property,
        string OldValue, string NewValue, int ObjectId, int ChildId, int ThreadId, AutomationElement Element);

    private sealed class ElementInfo
    {
        public string RuntimeId = "";
        public string ControlType = "";
        public string Name = "";
        public string AutomationId = "";
        public string ClassName = "";
        public string Hwnd = "";
        public string Pid = "";
        public string InTarget = "?";
        public string Error = "";
    }
}
