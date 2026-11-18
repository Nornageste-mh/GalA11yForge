using GalA11yForge.Core.Tools;

namespace GalA11yForge.Core.Tests;

public sealed class VoiceProbeTests
{
    [Theory]
    [InlineData("TTS_MS_JA-JP_HARUKA_11.0", true)]
    [InlineData("MSTTS_V110_jaJP_NanamiM", true)]
    [InlineData("Microsoft Haruka Desktop", true)]
    [InlineData("TTS_MS_ZH-CN_HUIHUI_11.0", false)]
    [InlineData("TTS_MS_EN-US_ZIRA_11.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsJapaneseVoiceName_MatchesKnownJapaneseTokens(string? name, bool expected)
    {
        Assert.Equal(expected, VoiceProbe.IsJapaneseVoiceName(name));
    }

    [Fact]
    public void Probe_DoesNotThrow_AndReturnsList()
    {
        var result = VoiceProbe.Probe();

        Assert.NotNull(result.AllVoices);
        Assert.NotNull(result.JapaneseVoices);
        Assert.Equal(result.HasJapanese, result.JapaneseVoices.Count > 0);
    }
}
