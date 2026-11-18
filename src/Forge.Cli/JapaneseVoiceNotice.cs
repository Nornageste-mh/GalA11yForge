namespace GalA11yForge.Cli;

/// <summary>
/// 0.2.76：日语文本 + 本机没有日语语音时的提醒。
/// 依据分析阶段写进候选证据的语言标记（例如 language=ja），只做保守提示，不阻断安装。
/// </summary>
public static class JapaneseVoiceNotice
{
    private static readonly string[] JapaneseTokens = ["language=ja", "language:ja", "language=japanese"];

    /// <summary>
    /// true = 应该提醒：证据显示是日语文本，且本机没有日语语音。
    /// </summary>
    public static bool ShouldWarn(bool hasJapaneseVoice, IEnumerable<string?> evidenceLines)
    {
        if (hasJapaneseVoice)
        {
            return false;
        }

        foreach (var line in evidenceLines)
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            foreach (var token in JapaneseTokens)
            {
                if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public const string Text =
        "这个游戏的文本看起来是日语，但本机没有安装日语语音：读出来可能按中文/英文发音，或者干脆没声。可在 Windows 设置 → 时间和语言 → 语音里安装日语语音后重试。";
}
