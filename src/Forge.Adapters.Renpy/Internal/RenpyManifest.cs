using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>
/// 生成物 a11y_manifest.json 的数据形状，保持与实验原型
/// renpy_a11y_gen.py 的 gala11y.renpy.overlay/1 兼容（snake_case 字段）。
/// </summary>
internal sealed class RenpyOverlayManifest
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "gala11y.renpy.overlay/1";

    [JsonPropertyName("generator")]
    public string Generator { get; set; } = "gala11y-forge-renpy";

    [JsonPropertyName("generator_version")]
    public string GeneratorVersion { get; set; } = RenpyAdapter.AdapterVersion;

    [JsonPropertyName("patch_version")]
    public string PatchVersion { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("game")]
    public RenpyManifestGame Game { get; set; } = new();

    [JsonPropertyName("files")]
    public List<RenpyManifestFile> Files { get; set; } = new();
}

internal sealed class RenpyManifestGame
{
    [JsonPropertyName("root")]
    public string Root { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>适配器 id，固定 renpy；不写具体 Ren'Py 版本，版本在 deploy_plan 之外由检测结果给出。</summary>
    [JsonPropertyName("engine")]
    public string Engine { get; set; } = RenpyAdapter.EngineId;

    [JsonPropertyName("python_major")]
    public int PythonMajor { get; set; }

    [JsonPropertyName("is_steam")]
    public bool IsSteam { get; set; }
}

internal sealed class RenpyManifestFile
{
    /// <summary>相对游戏根目录，例如 game/a11y_platform/00_contract.rpy。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>deploy_plan.json：只描述“复制哪些文件、到哪里”，不含游戏资源。</summary>
internal sealed class RenpyDeployPlan
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "gala11y.renpy.deploy_plan/1";

    [JsonPropertyName("game_root")]
    public string GameRoot { get; set; } = string.Empty;

    [JsonPropertyName("guard")]
    public string Guard { get; set; } = string.Empty;

    [JsonPropertyName("conflict_overlay")]
    public bool ConflictOverlay { get; set; }

    [JsonPropertyName("steps")]
    public List<RenpyDeployStep> Steps { get; set; } = new();

    [JsonPropertyName("verify")]
    public List<string> Verify { get; set; } = new();
}

internal sealed class RenpyDeployStep
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

/// <summary>清单/计划的 JSON 约定：缩进、保留中文、null 字段不写、文件末尾带换行。</summary>
internal static class RenpyManifestJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
