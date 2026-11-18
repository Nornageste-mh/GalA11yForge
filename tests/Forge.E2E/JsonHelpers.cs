using System.Text.Json;

namespace Forge.E2E;

/// <summary>
/// 对 System.Text.Json 的小工具封装。E2E 需要兼容 docs/06 与 docs/09 两套字段形态，
/// 所以这里全部按“大小写不敏感、允许多个候选名”读取。
/// </summary>
internal static class JsonHelpers
{
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    public static bool TryGetString(JsonElement element, out string value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var candidate) && candidate.ValueKind == JsonValueKind.String)
            {
                value = candidate.GetString() ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public static bool TryGetDouble(JsonElement element, out double value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var candidate) && candidate.ValueKind == JsonValueKind.Number
                && candidate.TryGetDouble(out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    public static bool TryGetBool(JsonElement element, out bool value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var candidate)
                && (candidate.ValueKind == JsonValueKind.True || candidate.ValueKind == JsonValueKind.False))
            {
                value = candidate.GetBoolean();
                return true;
            }
        }

        value = false;
        return false;
    }

    /// <summary>属性存在且不是 null/undefined。</summary>
    public static bool HasValue(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    public static JsonElement? CloneProperty(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) ? value.Clone() : null;
    }

    /// <summary>把 JSON 截断成一行，用于失败信息。</summary>
    public static string Format(JsonElement element, int maxLength = 600)
    {
        var raw = element.GetRawText();
        raw = raw.Replace("\r", " ").Replace("\n", " ");
        return raw.Length <= maxLength ? raw : raw[..maxLength] + "…";
    }

    public static string ReadObjectText(string path, int maxLength = 2000)
    {
        var text = File.ReadAllText(path);
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
