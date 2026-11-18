using System.Text.Json;
using System.Text.Json.Serialization;
using GalA11yForge.Core.Abstractions;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Core.Logging;

/// <summary>flow.jsonl 的一行。Data 只放命令/路径/阶段等机器信息，不放游戏台词。</summary>
public sealed class FlowLogEntry
{
    public string Timestamp { get; set; } = string.Empty;

    public FlowLogLevel Level { get; set; } = FlowLogLevel.Info;

    public string Stage { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public Dictionary<string, string>? Data { get; set; }
}

/// <summary>追加写 reports/flow.jsonl；进程内加锁，保证一行一个完整 JSON 对象。</summary>
public sealed class JsonlFlowLogger : IFlowLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private readonly bool _ownsWriter;

    public JsonlFlowLogger(string path)
    {
        Path = path;
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _ownsWriter = true;
    }

    public string Path { get; }

    public void Log(FlowLogLevel level, string stage, string message, IReadOnlyDictionary<string, string>? data = null)
    {
        var entry = new FlowLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToString("O"),
            Level = level,
            Stage = stage,
            Message = message,
            Data = data is null ? null : new Dictionary<string, string>(data),
        };

        var line = JsonSerializer.Serialize(entry, ForgeJson.Compact);
        lock (_gate)
        {
            if (_writer is null)
            {
                throw new ObjectDisposedException(nameof(JsonlFlowLogger));
            }

            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_ownsWriter)
            {
                _writer?.Dispose();
            }
        }
    }
}

/// <summary>不写盘的空日志，供默认构造与单元测试使用。</summary>
public sealed class NullFlowLogger : IFlowLogger
{
    public static NullFlowLogger Instance { get; } = new();

    public void Log(FlowLogLevel level, string stage, string message, IReadOnlyDictionary<string, string>? data = null)
    {
    }
}
