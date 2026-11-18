using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GalA11yForge.Detection.Internal;

internal sealed class ArtifactProbeResult
{
    public string? Version { get; set; }

    public int Bitness { get; set; }

    public List<string> Evidence { get; } = new();
}

/// <summary>
/// 版本号与位数探针（只读）：
/// - Unity 版本：优先 &lt;Product&gt;_Data/globalgamemanagers 偏移 48 处的 ASCII 版本串
///   （本机 5 个 Unity 作品实测均在此偏移），回退 UnityPlayer.dll 的 ProductVersion；
/// - Ren'Py 版本：renpy/__init__.py 的 version_tuple，第 5 项标识符（如 vc_version）
///   去 renpy/&lt;name&gt;.py 解析成数字后拼点分版本；
/// - 位数：根目录最大的 *.exe 的 PE COFF Machine。
/// 读不到就返回 null / 0 并留 unverified 证据，绝不编造。
/// </summary>
internal static class ArtifactProbe
{
    private const int PePrefixBytes = 64 * 1024;
    private const int UnityHeaderBytes = 512;
    private const int RenpyReadBytes = 256 * 1024;

    private static readonly Regex UnityVersionPattern =
        new(@"\d{4}\.\d+\.\d+[abcfp]\d+", RegexOptions.CultureInvariant);

    private static readonly Regex RenpyTuplePattern =
        new(@"version_tuple\s*=\s*\(([^)]*)\)", RegexOptions.CultureInvariant);

    public static ArtifactProbeResult Probe(GameDirectoryIndex index, string engineId, CancellationToken cancellationToken)
    {
        var result = new ArtifactProbeResult();
        result.Bitness = DetectBitness(index, result.Evidence);

        switch ((engineId ?? string.Empty).ToLowerInvariant())
        {
            case "unity_mono":
            case "unity_il2cpp":
            case "unity_custom_draw":
                result.Version = TryUnityVersion(index, result.Evidence, cancellationToken);
                break;

            case "renpy":
                result.Version = TryRenpyVersion(index, result.Evidence, cancellationToken);
                break;
        }

        return result;
    }

    private static int DetectBitness(GameDirectoryIndex index, List<string> evidence)
    {
        IndexedFile? candidate = null;
        foreach (var file in index.Files)
        {
            if (file.LowerRelativePath.Contains('/'))
            {
                continue;
            }

            if (!file.LowerRelativePath.EndsWith(".exe", StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate is null || file.Length > candidate.Length)
            {
                candidate = file;
            }
        }

        if (candidate is null)
        {
            evidence.Add("unverified:bitness 根目录没有 *.exe，未读取（bitness=0）");
            return 0;
        }

        if (!SafeFileReader.TryReadPrefix(candidate.FullPath, PePrefixBytes, out var head))
        {
            evidence.Add($"unverified:bitness 无法读取 {candidate.RelativePath}（bitness=0）");
            return 0;
        }

        var machine = ReadMachine(head);
        if (machine is null)
        {
            evidence.Add($"unverified:bitness {candidate.RelativePath} 不是可解析的 PE 文件（bitness=0）");
            return 0;
        }

        var bitness = machine.Value switch
        {
            0x014c or 0x01c0 or 0x01c2 or 0x01c4 => 32,
            0x8664 or 0x0200 or 0xaa64 => 64,
            _ => 0,
        };

        if (bitness == 0)
        {
            evidence.Add($"unverified:bitness {candidate.RelativePath} 的 PE Machine=0x{machine.Value:X4} 未知（bitness=0）");
            return 0;
        }

        evidence.Add($"measured:bitness {bitness}（{candidate.RelativePath} PE Machine=0x{machine.Value:X4}）");
        return bitness;
    }

    private static ushort? ReadMachine(byte[] head)
    {
        if (head.Length < 0x40)
        {
            return null;
        }

        if (head[0] != (byte)'M' || head[1] != (byte)'Z')
        {
            return null;
        }

        var lfanew = BitConverter.ToInt32(head, 0x3C);
        if (lfanew <= 0 || lfanew + 6 > head.Length)
        {
            return null;
        }

        if (head[lfanew] != (byte)'P' || head[lfanew + 1] != (byte)'E'
            || head[lfanew + 2] != 0 || head[lfanew + 3] != 0)
        {
            return null;
        }

        return BitConverter.ToUInt16(head, lfanew + 4);
    }

    private static string? TryUnityVersion(GameDirectoryIndex index, List<string> evidence, CancellationToken cancellationToken)
    {
        foreach (var file in index.Match("*_data/globalgamemanagers"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeFileReader.TryReadPrefix(file.FullPath, UnityHeaderBytes, out var head) || head.Length == 0)
            {
                continue;
            }

            var text = Encoding.Latin1.GetString(head);
            var match = UnityVersionPattern.Match(text);
            if (match.Success)
            {
                evidence.Add($"measured:version {match.Value}（{file.RelativePath} @{match.Index}）");
                return match.Value;
            }
        }

        foreach (var file in index.Match("unityplayer.dll"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = FileVersionInfo.GetVersionInfo(file.FullPath);
                var raw = info.ProductVersion ?? info.FileVersion;
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var match = UnityVersionPattern.Match(raw);
                if (match.Success)
                {
                    evidence.Add($"measured:version {match.Value}（{file.RelativePath} ProductVersion）");
                    return match.Value;
                }
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or System.Security.SecurityException)
            {
                // 读不到版本信息就继续尝试下一种来源。
            }
        }

        return null;
    }

    private static string? TryRenpyVersion(GameDirectoryIndex index, List<string> evidence, CancellationToken cancellationToken)
    {
        foreach (var file in index.Match("renpy/__init__.py"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeFileReader.TryReadPrefix(file.FullPath, RenpyReadBytes, out var head) || head.Length == 0)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(head);
            var tuple = RenpyTuplePattern.Match(text);
            if (!tuple.Success)
            {
                continue;
            }

            var parts = new List<string>();
            var unresolved = false;
            foreach (var rawPart in tuple.Groups[1].Value.Split(','))
            {
                var part = rawPart.Trim();
                if (part.Length == 0)
                {
                    continue;
                }

                if (part.All(char.IsDigit))
                {
                    parts.Add(part);
                    continue;
                }

                var identifier = part.Trim('"', '\'');
                var resolved = ResolveRenpyIdentifier(index, identifier);
                if (resolved is null)
                {
                    unresolved = true;
                    break;
                }

                parts.Add(resolved);
            }

            if (parts.Count == 0)
            {
                continue;
            }

            var version = string.Join(".", parts);
            var note = unresolved ? "，后续项未能解析" : string.Empty;
            evidence.Add($"measured:version {version}（{file.RelativePath} version_tuple{note}）");
            return version;
        }

        return null;
    }

    private static string? ResolveRenpyIdentifier(GameDirectoryIndex index, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)
            || !identifier.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
        {
            return null;
        }

        foreach (var file in index.Match($"renpy/{identifier}.py"))
        {
            if (!SafeFileReader.TryReadPrefix(file.FullPath, 64 * 1024, out var head) || head.Length == 0)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(head);
            var match = Regex.Match(
                text,
                @"^\s*" + Regex.Escape(identifier) + @"\s*=\s*(\d+)",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }
}
