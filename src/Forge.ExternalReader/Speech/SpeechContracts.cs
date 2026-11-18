namespace GalA11yForge.ExternalReader.Speech;

public interface ISpeechBackend : IDisposable
{
    string Name { get; }
    bool TryInit(out string? error);
    /// <summary>异步投递（不得阻塞调用线程）。返回 false 表示后端已不可用。</summary>
    bool Speak(string text);
    void Cancel();
}

/// <summary>把长句切成 60–80 字的小段：SAPI 的 PURGE 打断粒度、NVDA 的排队粒度都按这个走。</summary>
public static class SentenceSplitter
{
    private static readonly char[] HardStop = ['。', '！', '？', '!', '?', '；', ';', '…', '\n'];
    private static readonly char[] SoftStop = ['，', ',', '、', '·', ' '];

    public static IReadOnlyList<string> Split(string text, int target = 80)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return parts;

        var start = 0;
        var len = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            len++;
            var isHard = Array.IndexOf(HardStop, c) >= 0;
            var isSoft = Array.IndexOf(SoftStop, c) >= 0;
            var cut = (isHard && len >= 12) || (isSoft && len >= 60) || len >= target;
            if (!cut) continue;

            var chunk = text[start..(i + 1)].Trim();
            if (chunk.Length > 0) parts.Add(chunk);
            start = i + 1;
            len = 0;
        }
        if (start < text.Length)
        {
            var tail = text[start..].Trim();
            if (tail.Length > 0) parts.Add(tail);
        }

        // 极端长句（无标点）再硬切一次，避免单段过长
        var final = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            if (p.Length <= target) { final.Add(p); continue; }
            for (var i = 0; i < p.Length; i += target)
                final.Add(p.Substring(i, Math.Min(target, p.Length - i)));
        }
        return final;
    }
}
