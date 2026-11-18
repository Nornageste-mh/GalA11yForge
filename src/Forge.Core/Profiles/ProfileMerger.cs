using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Core.Profiles;

/// <summary>
/// profile.json 与 overrides.json 的合并规则（v1）：
/// 1) 标量（slug/gamePath/engine/minConfidence/advanced/universalCapture/voice/lastResult）：
///    overrides 非 null/非空白时覆盖；null 表示继承。
/// 2) keymap：按键合并，同名键由 overrides 覆盖。
/// 3) options：按顶层键合并；两边都是 JSON 对象时递归合并，其他类型（数组/标量）整体替换。
/// 4) 合并结果深拷贝，不修改任何输入对象。
/// </summary>
public static class ProfileMerger
{
    public static ProfileDocument Merge(ProfileDocument profile, ProfileDocument? overrides)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = Clone(profile);
        if (overrides is null)
        {
            return result;
        }

        if (!string.IsNullOrWhiteSpace(overrides.Slug))
        {
            result.Slug = overrides.Slug;
        }

        if (!string.IsNullOrWhiteSpace(overrides.GamePath))
        {
            result.GamePath = overrides.GamePath;
        }

        if (!string.IsNullOrWhiteSpace(overrides.Engine))
        {
            result.Engine = overrides.Engine;
        }

        if (overrides.MinConfidence.HasValue)
        {
            result.MinConfidence = overrides.MinConfidence;
        }

        if (overrides.Advanced.HasValue)
        {
            result.Advanced = overrides.Advanced;
        }

        if (overrides.UniversalCapture.HasValue)
        {
            result.UniversalCapture = overrides.UniversalCapture;
        }

        if (!string.IsNullOrWhiteSpace(overrides.Voice))
        {
            result.Voice = overrides.Voice;
        }

        if (!string.IsNullOrWhiteSpace(overrides.LastResult))
        {
            result.LastResult = overrides.LastResult;
        }

        foreach (var pair in overrides.Keymap)
        {
            result.Keymap[pair.Key] = pair.Value;
        }

        foreach (var pair in overrides.Options)
        {
            if (result.Options.TryGetValue(pair.Key, out var existing)
                && existing.ValueKind == JsonValueKind.Object
                && pair.Value.ValueKind == JsonValueKind.Object)
            {
                result.Options[pair.Key] = MergeObjects(existing, pair.Value);
            }
            else
            {
                result.Options[pair.Key] = pair.Value.Clone();
            }
        }

        return result;
    }

    public static ForgeResult<ProfileDocument> Validate(ProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != 1)
        {
            return ForgeResult<ProfileDocument>.Failure(ForgeError.Create(
                ForgeErrorCodes.ProfileInvalid,
                $"不支持的 profile schemaVersion：{document.SchemaVersion}。",
                "当前只支持 schemaVersion=1；升级工具或手工改回 1。"));
        }

        if (document.MinConfidence is < 0 or > 1)
        {
            return ForgeResult<ProfileDocument>.Failure(ForgeError.Create(
                ForgeErrorCodes.ProfileInvalid,
                $"minConfidence 必须在 0..1 之间，当前为 {document.MinConfidence}。",
                "例如 0.7 表示置信度低于 70% 时要求人工确认。"));
        }

        if (document.Engine is { Length: > 0 } engine && !PathSafetyLikeEngineId(engine))
        {
            return ForgeResult<ProfileDocument>.Failure(ForgeError.Create(
                ForgeErrorCodes.ProfileInvalid,
                $"非法引擎 id “{engine}”。",
                "引擎 id 使用 snake_case，例如 unity_mono、renpy、kirikiriz。"));
        }

        return ForgeResult<ProfileDocument>.Success(document);
    }

    public static ProfileDocument Clone(ProfileDocument source)
    {
        var clone = new ProfileDocument
        {
            SchemaVersion = source.SchemaVersion,
            Slug = source.Slug,
            GamePath = source.GamePath,
            Engine = source.Engine,
            MinConfidence = source.MinConfidence,
            Advanced = source.Advanced,
            UniversalCapture = source.UniversalCapture,
            Voice = source.Voice,
            LastResult = source.LastResult,
        };

        foreach (var pair in source.Keymap)
        {
            clone.Keymap[pair.Key] = pair.Value;
        }

        foreach (var pair in source.Options)
        {
            clone.Options[pair.Key] = pair.Value.Clone();
        }

        return clone;
    }

    private static JsonElement MergeObjects(JsonElement baseObject, JsonElement overrideObject)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in baseObject.EnumerateObject())
        {
            merged[property.Name] = property.Value.Clone();
        }

        foreach (var property in overrideObject.EnumerateObject())
        {
            if (merged.TryGetValue(property.Name, out var existing)
                && existing.ValueKind == JsonValueKind.Object
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                merged[property.Name] = MergeObjects(existing, property.Value);
            }
            else
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        return JsonSerializer.SerializeToElement(merged);
    }

    private static bool PathSafetyLikeEngineId(string engine)
    {
        foreach (var ch in engine)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.'))
            {
                return false;
            }
        }

        return engine.Length <= 64 && !engine.Contains("..", StringComparison.Ordinal);
    }
}
