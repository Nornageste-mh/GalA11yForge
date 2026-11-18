using System.Text.Json.Serialization;

namespace GalA11yForge.Verify.Models;

/// <summary>
/// 一个语音后端的只读事实。Available=null 表示无法判断。
/// 这里只记录“组件是否存在/进程是否在跑”，不代表实际能出声。
/// </summary>
public sealed class VoiceBackendFact
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool? Available { get; set; }

    public string Fact { get; set; } = string.Empty;

    public List<string> Evidence { get; set; } = new();
}

/// <summary>
/// 一次只读环境探测的结果。不得包含游戏台词、录音或语音内容。
/// </summary>
public sealed class EnvironmentSnapshot
{
    public string CapturedAt { get; set; } = string.Empty;

    public List<VoiceBackendFact> Backends { get; set; } = new();

    public List<string> Notes { get; set; } = new();

    public Dictionary<string, double> Metrics { get; set; } = new();

    [JsonIgnore]
    public int AvailableCount => Backends.Count(backend => backend.Available == true);

    [JsonIgnore]
    public bool AnyAvailable => AvailableCount > 0;
}
