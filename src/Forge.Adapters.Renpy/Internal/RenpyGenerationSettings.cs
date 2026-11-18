using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.Renpy.Internal;

/// <summary>生成参数。来自 profile.json 的 options，缺省值与实验原型保持一致。</summary>
internal sealed class RenpyGenerationSettings
{
    public const string DefaultPatchVersion = "0.2.0.1";

    private static readonly Regex PatchVersionPattern = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$",
        RegexOptions.CultureInvariant);

    private static readonly string[] TtsValues = { "auto", "nvda", "sapi", "log" };

    public string PatchVersion { get; init; } = DefaultPatchVersion;

    public string TtsPreference { get; init; } = "auto";

    public int SapiRate { get; init; }

    public static RenpyGenerationSettings From(ProfileDocument? profile)
    {
        var patchVersion = GetString(profile, "renpy.patchVersion", "patchVersion") ?? DefaultPatchVersion;
        patchVersion = patchVersion.Trim();
        if (!PatchVersionPattern.IsMatch(patchVersion))
        {
            throw new RenpyOptionException(
                $"补丁版本 “{patchVersion}” 不是四段数字（示例 0.2.0.1）。",
                "在 profile.json 的 options 里设置 renpy.patchVersion=0.2.0.1，或删除该项使用默认值。");
        }

        var tts = GetString(profile, "renpy.tts", "tts");
        if (string.IsNullOrWhiteSpace(tts) && !string.IsNullOrWhiteSpace(profile?.Voice))
        {
            tts = profile.Voice;
        }

        tts = (tts ?? "auto").Trim().ToLowerInvariant();
        if (!TtsValues.Contains(tts, StringComparer.Ordinal))
        {
            throw new RenpyOptionException(
                $"语音后端 “{tts}” 不支持。",
                "可选 auto / nvda / sapi / log；auto 会按 NVDA→SAPI→日志顺序探测。");
        }

        var rateText = GetString(profile, "renpy.sapiRate", "sapiRate");
        var rate = 0;
        if (!string.IsNullOrWhiteSpace(rateText))
        {
            if (!int.TryParse(rateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out rate)
                || rate < -10
                || rate > 10)
            {
                throw new RenpyOptionException(
                    $"SAPI 速率 “{rateText}” 超出范围。",
                    "renpy.sapiRate 取 -10..10 的整数，0 为正常语速。");
            }
        }

        return new RenpyGenerationSettings
        {
            PatchVersion = patchVersion,
            TtsPreference = tts,
            SapiRate = rate,
        };
    }

    private static string? GetString(ProfileDocument? profile, params string[] keys)
    {
        if (profile?.Options is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (!profile.Options.TryGetValue(key, out var element))
            {
                continue;
            }

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
        }

        return null;
    }
}

/// <summary>profile 选项不合法；生成器转成 E_ARG_INVALID / E_PROFILE_INVALID。</summary>
internal sealed class RenpyOptionException : Exception
{
    public RenpyOptionException(string message, string suggestion)
        : base(message)
    {
        Suggestion = suggestion;
    }

    public string Suggestion { get; }
}
