using System.Text;
using GalA11yForge.Detection.Fingerprints;

namespace GalA11yForge.Detection.Internal;

internal sealed class ConditionOutcome
{
    public ConditionOutcome(bool hit, string evidence)
    {
        Hit = hit;
        Evidence = evidence;
    }

    public bool Hit { get; }

    /// <summary>命中或未命中的人类可读证据行（file:/dir:/measured:/miss: 前缀）。</summary>
    public string Evidence { get; }
}

/// <summary>
/// 7 种条件原语求值，语义对齐 research/16-engines-more/evidence/detect.py：
/// file_glob、file_any、dir_exists、count、magic、pe_string、text。
/// 读文件一律只读 + 共享读，读不到按未命中处理（不抛异常、不编造证据）。
/// 常量上限沿用参考实现：PE 12MB/8 个文件，magic 20 个候选，text 5 个文件/2MB。
/// 每个条件对象只求值一次（短路求值后补证据时复用缓存）。
/// </summary>
internal sealed class ConditionEvaluator
{
    public const int MaxPeBytes = 12 * 1024 * 1024;
    public const int MaxPeFiles = 8;
    public const int MaxMagicFiles = 20;
    public const int MaxTextFiles = 5;
    public const int MaxTextBytes = 2 * 1024 * 1024;

    private readonly GameDirectoryIndex _index;
    private readonly Dictionary<FingerprintCondition, ConditionOutcome> _cache = new();

    public ConditionEvaluator(GameDirectoryIndex index) => _index = index;

    public ConditionOutcome Evaluate(FingerprintCondition condition)
    {
        if (_cache.TryGetValue(condition, out var cached))
        {
            return cached;
        }

        var outcome = EvaluateCore(condition);
        _cache[condition] = outcome;
        return outcome;
    }

    private ConditionOutcome EvaluateCore(FingerprintCondition condition)
    {
        switch ((condition.Type ?? string.Empty).ToLowerInvariant())
        {
            case "file_glob":
                return EvaluateFileGlob(condition);
            case "file_any":
                return EvaluateFileAny(condition);
            case "dir_exists":
                return EvaluateDirExists(condition);
            case "count":
                return EvaluateCount(condition);
            case "magic":
                return EvaluateMagic(condition);
            case "pe_string":
                return EvaluatePeString(condition);
            case "text":
                return EvaluateText(condition);
            default:
                return new ConditionOutcome(false, $"miss:未知条件类型 {condition.Type}");
        }
    }

    private ConditionOutcome EvaluateFileGlob(FingerprintCondition condition)
    {
        var matches = _index.Match(condition.Glob!);
        return matches.Count > 0
            ? new ConditionOutcome(true, $"file:{matches[0].RelativePath}")
            : new ConditionOutcome(false, $"miss:file_glob {condition.Glob}");
    }

    private ConditionOutcome EvaluateFileAny(FingerprintCondition condition)
    {
        var globs = condition.Globs ?? new List<string>();
        foreach (var glob in globs)
        {
            var matches = _index.Match(glob);
            if (matches.Count > 0)
            {
                return new ConditionOutcome(true, $"file:{matches[0].RelativePath}（file_any: {glob}）");
            }
        }

        return new ConditionOutcome(false, $"miss:file_any {string.Join(" | ", globs)}");
    }

    private ConditionOutcome EvaluateDirExists(FingerprintCondition condition)
    {
        var directory = condition.Dir!;
        return _index.DirectoryExists(directory)
            ? new ConditionOutcome(true, $"dir:{directory}")
            : new ConditionOutcome(false, $"miss:dir_exists {directory}");
    }

    private ConditionOutcome EvaluateCount(FingerprintCondition condition)
    {
        var matches = _index.Match(condition.Glob!);
        var min = condition.Min ?? 0;
        return matches.Count >= min
            ? new ConditionOutcome(
                true,
                $"measured:count {condition.Glob} = {matches.Count}（≥{min}），如 file:{matches[0].RelativePath}")
            : new ConditionOutcome(false, $"miss:count {condition.Glob} = {matches.Count}（<{min}）");
    }

    private ConditionOutcome EvaluateMagic(FingerprintCondition condition)
    {
        var matches = _index.Match(condition.Glob!);
        var offset = condition.Offset ?? 0;
        var hexValues = condition.Hex ?? new List<string>();
        var maxHexLength = hexValues.Count == 0 ? 0 : hexValues.Max(hex => hex.Length / 2);
        var probeLength = offset + Math.Max(32, maxHexLength);

        foreach (var file in matches.Take(MaxMagicFiles))
        {
            if (!SafeFileReader.TryReadPrefix(file.FullPath, probeLength, out var head))
            {
                continue;
            }

            foreach (var hex in hexValues)
            {
                var needle = Convert.FromHexString(hex);
                if (head.Length >= offset + needle.Length
                    && head.AsSpan(offset, needle.Length).SequenceEqual(needle))
                {
                    return new ConditionOutcome(true, $"measured:magic {file.RelativePath} @{offset} = {hex}");
                }
            }
        }

        return new ConditionOutcome(false, $"miss:magic {condition.Glob} @{offset}");
    }

    private ConditionOutcome EvaluatePeString(FingerprintCondition condition)
    {
        var matches = _index.Match(condition.Glob!);
        var needle = condition.Contains ?? string.Empty;
        var needles = BuildNeedles(needle, condition.Mode ?? "both");

        var scanned = 0;
        foreach (var file in matches)
        {
            if (scanned >= MaxPeFiles)
            {
                break;
            }

            // 与 detect.py 一致：只扫 <= 12MB 的 PE 候选。
            if (file.Length <= 0 || file.Length > MaxPeBytes)
            {
                continue;
            }

            scanned++;
            if (!SafeFileReader.TryReadPrefix(file.FullPath, MaxPeBytes, out var bytes) || bytes.Length == 0)
            {
                continue;
            }

            var span = bytes.AsSpan();
            foreach (var (label, encoded) in needles)
            {
                if (encoded.Length > 0 && span.IndexOf(encoded) >= 0)
                {
                    return new ConditionOutcome(
                        true,
                        $"measured:pe_string {file.RelativePath} 含 \"{needle}\"（{label}）");
                }
            }
        }

        return new ConditionOutcome(false, $"miss:pe_string {condition.Glob} \"{needle}\"");
    }

    private ConditionOutcome EvaluateText(FingerprintCondition condition)
    {
        var matches = _index.Match(condition.Glob!);
        var needle = condition.Contains ?? string.Empty;

        foreach (var file in matches.Take(MaxTextFiles))
        {
            if (!SafeFileReader.TryReadPrefix(file.FullPath, MaxTextBytes, out var bytes) || bytes.Length == 0)
            {
                continue;
            }

            foreach (var (label, text) in DecodeText(bytes))
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    return new ConditionOutcome(
                        true,
                        $"measured:text {file.RelativePath} 含 \"{needle}\"（{label}）");
                }
            }
        }

        return new ConditionOutcome(false, $"miss:text {condition.Glob} \"{needle}\"");
    }

    /// <summary>按 detect.py 的 ascii / utf16le / both 生成待搜字节。</summary>
    private static List<(string Label, byte[] Needle)> BuildNeedles(string needle, string mode)
    {
        var needles = new List<(string, byte[])>();
        var normalizedMode = mode.ToLowerInvariant();

        if (normalizedMode is "ascii" or "both")
        {
            needles.Add(("ascii", AsciiBytes(needle)));
        }

        if (normalizedMode is "utf16" or "utf16le" or "both")
        {
            needles.Add(("utf16le", Encoding.Unicode.GetBytes(needle)));
        }

        return needles;
    }

    /// <summary>等价于 Python 的 encode("ascii", "ignore")：丢掉非 ASCII 字符。</summary>
    private static byte[] AsciiBytes(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var ch in text)
        {
            if (ch < 0x80)
            {
                bytes.Add((byte)ch);
            }
        }

        return bytes.ToArray();
    }

    /// <summary>依次尝试 utf-8 / utf-16le / latin-1（与 detect.py 一致的顺序）。</summary>
    private static IEnumerable<(string Label, string Text)> DecodeText(byte[] bytes)
    {
        yield return ("utf-8", Encoding.UTF8.GetString(bytes));

        var evenLength = bytes.Length - (bytes.Length % 2);
        yield return ("utf-16le", Encoding.Unicode.GetString(bytes, 0, evenLength));

        yield return ("latin-1", Encoding.Latin1.GetString(bytes));
    }
}
