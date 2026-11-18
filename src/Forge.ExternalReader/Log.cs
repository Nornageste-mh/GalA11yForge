using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GalA11yForge.ExternalReader;

/// <summary>
/// 双写日志：人类可读的 .log + 机器可读的 .jsonl（同目录、同 basename）。
/// 线程安全；热键线程 / 朗读线程 / 主循环都会写。
/// 默认落在 %TEMP%\forgereader\&lt;time&gt;-&lt;mode&gt;[.log|.jsonl]，
/// 绝不写目标进程所在目录（游戏目录只读）。
/// </summary>
public sealed class Log : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _text;
    private readonly StreamWriter _json;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;

    public string TextPath { get; }
    public string JsonPath { get; }
    public string RunId { get; }
    public Stopwatch Clock => _clock;

    private Log(string textPath, string jsonPath, string runId)
    {
        TextPath = textPath;
        JsonPath = jsonPath;
        RunId = runId;
        Directory.CreateDirectory(Path.GetDirectoryName(textPath)!);
        _text = new StreamWriter(new FileStream(textPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _json = new StreamWriter(new FileStream(jsonPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public static Log Create(string? explicitPath, string mode, string? runId = null)
    {
        string basePath;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            basePath = Path.GetFullPath(explicitPath);
        }
        else
        {
            var dir = Path.Combine(Path.GetTempPath(), "forgereader");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            basePath = Path.Combine(dir, $"{stamp}-{mode}.log");
        }

        var dirName = Path.GetDirectoryName(basePath)!;
        var stem = Path.GetFileNameWithoutExtension(basePath);
        if (stem.Length == 0) stem = "forgereader";
        var ext = Path.GetExtension(basePath);
        if (!ext.Equals(".log", StringComparison.OrdinalIgnoreCase)) ext = ".log";
        var textPath = Path.Combine(dirName, stem + ext);
        var jsonPath = Path.Combine(dirName, stem + ".jsonl");
        return new Log(textPath, jsonPath, runId ?? Guid.NewGuid().ToString("N")[..8]);
    }

    public long ElapsedMs => _clock.ElapsedMilliseconds;

    /// <summary>写一条结构化事件。msg 可为空；fields 会平铺进 JSON 顶层。</summary>
    public void Evt(string evt, string? msg = null, params (string Key, object? Value)[] fields)
    {
        var ms = _clock.ElapsedMilliseconds;
        var line = new StringBuilder();
        line.Append('[').Append(ms.ToString().PadLeft(6)).Append("ms] ").Append(evt);
        if (!string.IsNullOrEmpty(msg)) line.Append(' ').Append(msg);
        foreach (var (k, v) in fields)
        {
            if (v is null) continue;
            line.Append(' ').Append(k).Append('=').Append(Format(v));
        }

        Dictionary<string, object?> payload = new()
        {
            ["t"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            ["ms"] = ms,
            ["evt"] = evt,
        };
        if (!string.IsNullOrEmpty(msg)) payload["msg"] = msg;
        foreach (var (k, v) in fields)
        {
            if (v is null) continue;
            payload[k] = v;
        }

        lock (_gate)
        {
            if (_disposed) return;
            _text.WriteLine(line.ToString());
            _json.WriteLine(JsonSerializer.Serialize(payload));
        }
    }

    public void Json(string evt, object payload)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _json.WriteLine(JsonSerializer.Serialize(new
            {
                t = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                ms = _clock.ElapsedMilliseconds,
                evt,
                payload,
            }));
        }
    }

    /// <summary>把任意 JSON 可序列化对象直接写进 jsonl（用于 UIA dump 之类的整块证据）。</summary>
    public void JsonRaw(object payload)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _json.WriteLine(JsonSerializer.Serialize(payload));
        }
    }

    private static string Format(object v) => v switch
    {
        string s => s.Contains(' ') || s.Contains('"') ? "\"" + s.Replace("\"", "'") + "\"" : s,
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _text.Flush(); _text.Dispose(); } catch { }
            try { _json.Flush(); _json.Dispose(); } catch { }
        }
    }
}
