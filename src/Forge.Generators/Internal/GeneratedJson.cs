using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalA11yForge.Generators.Internal;

/// <summary>
/// 生成物用的 JSON 选项：与 Core 的 ForgeJson 形状一致（camelCase、缩进、枚举 snake_case、null 省略），
/// 但显式使用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>，让中文按原文写出而不是 \uXXXX。
/// 只影响生成器自己产出的 profile.json / 清单，不改变 Core 的序列化行为。
/// </summary>
internal static class GeneratedJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
