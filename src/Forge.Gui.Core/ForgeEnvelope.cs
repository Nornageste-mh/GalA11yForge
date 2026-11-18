using System.Text.Json;

namespace GalA11yForge.Gui.Core;

/// <summary>
/// CLI --json 信封：{ ok, command, code, message, suggestion, data }。
/// 字段全部可空，解析失败不会抛异常。
/// </summary>
public sealed class ForgeEnvelope
{
    public bool Ok { get; init; }

    public string? Command { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public string? Suggestion { get; init; }

    /// <summary>结构化补充信息（CLI 0.2.9 起输出 details）；没有时为 null。</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    /// <summary>原始 data 节点；未提供时为 <see cref="JsonValueKind.Undefined"/>。</summary>
    public JsonElement Data { get; init; }

    public bool HasData => Data.ValueKind is JsonValueKind.Object or JsonValueKind.Array;

    /// <summary>供测试与日志的往返构造。</summary>
    public static ForgeEnvelope FromJson(string json)
    {
        var result = ForgeEnvelopeParser.Parse(json);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Error);
        }

        return result.Envelope!;
    }
}

public sealed class ForgeEnvelopeParseResult
{
    public ForgeEnvelope? Envelope { get; init; }

    /// <summary>命中的那一行原文（未 trim）。</summary>
    public string? JsonLine { get; init; }

    /// <summary>非空表示没解析出信封。</summary>
    public string? Error { get; init; }

    public bool Succeeded => Envelope is not null;

    public static ForgeEnvelopeParseResult Ok(ForgeEnvelope envelope, string line)
        => new() { Envelope = envelope, JsonLine = line };

    public static ForgeEnvelopeParseResult Fail(string error)
        => new() { Error = error };
}

/// <summary>
/// 从 stdout 里取「最后一行 JSON 信封」。CLI 契约：--json 时信封是 stdout 的最后一行，
/// 前面可能还有人类可读提示（未来版本），因此从后往前找第一条能解析成对象的行。
/// </summary>
public static class ForgeEnvelopeParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static ForgeEnvelopeParseResult Parse(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return ForgeEnvelopeParseResult.Fail("命令行没有任何输出（stdout 为空），拿不到 JSON 信封。");
        }

        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? firstFailure = null;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim().TrimStart('\uFEFF');
            if (line.Length < 2 || line[0] != '{')
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line, DocumentOptions);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                return ForgeEnvelopeParseResult.Ok(Map(document.RootElement), lines[i]);
            }
            catch (JsonException ex)
            {
                firstFailure ??= $"最后一行 JSON 无法解析：{ex.Message}";
            }
        }

        return ForgeEnvelopeParseResult.Fail(
            firstFailure ?? $"输出里没有找到 JSON 信封（共 {lines.Length} 行）。");
    }

    private static ForgeEnvelope Map(JsonElement root)
    {
        JsonElement? data = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "data", StringComparison.OrdinalIgnoreCase))
            {
                data = property.Value.Clone();
            }
        }

        return new ForgeEnvelope
        {
            Ok = ReadBool(root, "ok"),
            Command = ReadString(root, "command"),
            Code = ReadString(root, "code"),
            Message = ReadString(root, "message"),
            Suggestion = ReadString(root, "suggestion"),
            Details = ReadDetails(root),
            Data = data ?? default,
        };
    }

    private static IReadOnlyDictionary<string, string>? ReadDetails(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, "details", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in property.Value.EnumerateObject())
            {
                details[item.Name] = item.Value.ValueKind == JsonValueKind.String
                    ? item.Value.GetString() ?? string.Empty
                    : item.Value.ToString();
            }

            return details.Count == 0 ? null : details;
        }

        return null;
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(property.Value.GetString(), out var parsed) && parsed,
                JsonValueKind.Number => property.Value.TryGetDouble(out var number) && number != 0,
                _ => false,
            };
        }

        return false;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        return null;
    }
}
