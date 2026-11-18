using System.Diagnostics;

namespace GalA11yForge.Core.Tools;

/// <summary>
/// 0.2.66：探测本机有没有日语语音（SAPI/OneCore）。
/// 目的：日语 Galgame 在没有 ja-JP 语音的机器上，要么被按中文/英文念，要么根本读不出来；
/// 计划/doctor 至少要能提前说清楚，而不是让用户装完补丁才发现“没声”。
/// </summary>
public static class VoiceProbe
{
    public sealed record Result(IReadOnlyList<string> JapaneseVoices, IReadOnlyList<string> AllVoices, string? Note)
    {
        public bool HasJapanese => JapaneseVoices.Count > 0;
    }

    private static readonly string[] JapaneseHints =
    [
        "ja-jp", "ja_jp", "japanese", "haruka", "ayumi", "ichiro", "nanami", "keita",
    ];

    /// <summary>判断语音 token/显示名是不是日语（不区分大小写，按已知关键词）。</summary>
    public static bool IsJapaneseVoiceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var lowered = name.ToLowerInvariant();
        return JapaneseHints.Any(hint => lowered.Contains(hint, StringComparison.Ordinal));
    }

    /// <summary>读取本机语音列表。失败时返回空列表并带 Note（不抛异常）。</summary>
    public static Result Probe()
    {
        var voices = new List<string>();
        string? note = null;
        try
        {
            foreach (var key in new[]
                     {
                         @"HKLM\SOFTWARE\Microsoft\Speech\Voices\Tokens",
                         @"HKLM\SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens",
                     })
            {
                voices.AddRange(QueryRegistryKeys(key));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            note = ex.Message;
        }

        var distinct = voices
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var japanese = distinct.Where(IsJapaneseVoiceName).ToList();
        return new Result(japanese, distinct, note);
    }

    private static List<string> QueryRegistryKeys(string path)
    {
        var result = new List<string>();
        var psi = new ProcessStartInfo
        {
            FileName = "reg.exe",
            Arguments = "query \"" + path + "\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return result;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 忽略：只影响探测，不影响安装。
            }

            return result;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !line.StartsWith(@"HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var index = line.LastIndexOf('\\');
            if (index >= 0 && index + 1 < line.Length)
            {
                result.Add(line[(index + 1)..]);
            }
        }

        return result;
    }
}
