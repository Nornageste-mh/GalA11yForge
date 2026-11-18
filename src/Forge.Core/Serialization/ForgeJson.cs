using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalA11yForge.Core.Serialization;

/// <summary>
/// 全项目统一的 JSON 约定：
/// 属性 camelCase；枚举写 snake_case 字符串（dialogue、semi_automatic）；
/// null 属性不写盘；文件用缩进格式，JSONL/CLI 用压缩格式。
/// </summary>
public static class ForgeJson
{
    /// <summary>面向磁盘的 JSON：缩进、可读、保留中文原文。</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions(indented: true);

    /// <summary>面向 JSONL 与 CLI stdout 的 JSON：单行、无多余空白。</summary>
    public static JsonSerializerOptions Compact { get; } = CreateOptions(indented: false);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, Compact);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
