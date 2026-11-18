using System.Text;
using UnityMonoAnalyzer;
using Xunit;

namespace UnityMonoAnalyzer.Tests;

/// <summary>文本启发式：CJK、命名语义、框架痕迹、非台词字面量、Unity 版本串。</summary>
public class TextPatternsTests
{
    [Theory]
    [InlineData("こんにちは", true)]
    [InlineData("你好，世界", true)]
    [InlineData("アイウ", true)]
    [InlineData("Hello", false)]
    [InlineData("Привет", false)]
    [InlineData("", false)]
    [InlineData("12345", false)]
    public void ContainsCjk_MatchesJapaneseAndChineseOnly(string text, bool expected)
    {
        Assert.Equal(expected, TextPatterns.ContainsCjk(text));
    }

    [Fact]
    public void CountCjk_CountsEachCharacter()
    {
        Assert.Equal(2, TextPatterns.CountCjk("あaい"));
        Assert.Equal(0, TextPatterns.CountCjk("abc"));
    }

    [Fact]
    public void Tokenize_SplitsPascalCaseAndDots()
    {
        var tokens = TextPatterns.Tokenize("LoveStoryProject.BacklogItem");

        Assert.Contains("Love", tokens);
        Assert.Contains("Story", tokens);
        Assert.Contains("Project", tokens);
        Assert.Contains("Backlog", tokens);
        Assert.Contains("Item", tokens);
    }

    [Theory]
    [InlineData("ShowDialogue", true)]
    [InlineData("SetText", true)]
    [InlineData("AddChoice", true)]
    [InlineData("OnSubtitlesRequest", true)]
    [InlineData("Outline", false)] // 关键：不能被 Line 误命中
    [InlineData("RefreshLocalizedLayout", false)]
    public void HasSemanticName_UsesTokenBoundaries(string name, bool expected)
    {
        Assert.Equal(expected, TextPatterns.HasSemanticName(name));
    }

    [Theory]
    [InlineData("LogFps", true)]
    [InlineData("DebugPanel", true)]
    [InlineData("ShowDialogue", false)]
    public void HasDebugName_DetectsDebugVocabulary(string name, bool expected)
    {
        Assert.Equal(expected, TextPatterns.HasDebugName(name));
    }

    [Theory]
    [InlineData("DialogueBox", true)]
    [InlineData("LoginUI", true)]
    [InlineData("PlayerController", false)]
    public void HasUiName_DetectsUiVocabulary(string name, bool expected)
    {
        Assert.Equal(expected, TextPatterns.HasUiName(name));
    }

    [Theory]
    [InlineData("C:\\games\\a\\b.png", true)]
    [InlineData("https://example.com/x", true)]
    [InlineData("{0} / {1}", true)]
    [InlineData("%s", true)]
    [InlineData("12345", true)]
    [InlineData("2024-01-01T00:00:00Z", true)]
    [InlineData("savedata.player", true)]
    [InlineData("savedata", true)]
    [InlineData("Hello, world!", false)]
    [InlineData("こんにちは", false)]
    [InlineData("", true)]
    public void IsNonDialogueLiteral_ClassifiesLiterals(string text, bool expected)
    {
        Assert.Equal(expected, TextPatterns.IsNonDialogueLiteral(text));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("ab", false)]
    [InlineData(null, true)]
    public void IsTrivialShortLiteral_FlagsSuspiciouslyShortStrings(string? text, bool expected)
    {
        Assert.Equal(expected, TextPatterns.IsTrivialShortLiteral(text));
    }

    [Theory]
    [InlineData("Naninovel.ScriptPlayer", "naninovel")]
    [InlineData("NodeCanvas.DialogueTrees.DialogueTreeController", "nodecanvas.dialoguetrees")]
    [InlineData("ScenarioForge.TextTyper", "scenarioforge")]
    [InlineData("Fungus.SayDialog", "fungus")]
    [InlineData("UnityEngine.UI.Text", null)]
    public void MatchKnownFramework_ReturnsStableIds(string typeName, string? expected)
    {
        Assert.Equal(expected, TextPatterns.MatchKnownFramework(typeName));
    }

    [Fact]
    public void FrameworkDisplayName_IsHumanReadable()
    {
        Assert.Equal("Naninovel", TextPatterns.FrameworkDisplayName("naninovel"));
        Assert.False(string.IsNullOrWhiteSpace(TextPatterns.FrameworkDisplayName("nodecanvas.dialoguetrees")));
    }

    [Theory]
    [InlineData("DialogueHandle", true)]
    [InlineData("TalkWindow", true)]
    [InlineData("UIWindow", false)]
    public void HasCustomDialogueTrace_IgnoresGenericUiNames(string name, bool expected)
    {
        Assert.Equal(expected, TextPatterns.HasCustomDialogueTrace(name));
    }

    [Theory]
    [InlineData("2021.3.31f1", true)]
    [InlineData("6000.0.59f2", true)]
    [InlineData("2019.4.40c1", true)]
    [InlineData("5.6.7p1", true)]
    public void FindUnityVersion_ExtractsVersionFromBinaryHeader(string version, bool found)
    {
        // 模拟 globalgamemanagers 头部：版本串前后都是二进制噪声
        var payload = "\u0000\u0001build\u0000" + version + "-abcdef0123456789";
        var bytes = Encoding.Latin1.GetBytes(payload);

        var detected = TextPatterns.FindUnityVersion(bytes);

        Assert.Equal(found, detected is not null);
        if (found) Assert.Equal(version, detected);
    }

    [Fact]
    public void FindUnityVersion_ReturnsNullWhenAbsent()
    {
        var bytes = Encoding.Latin1.GetBytes("\u0007\u0007 no version here \u0001");
        Assert.Null(TextPatterns.FindUnityVersion(bytes));
    }

    [Fact]
    public void Shorten_TruncatesWithEllipsis()
    {
        var s = TextPatterns.Shorten(new string('x', 300), 40);

        Assert.True(s.Length <= 41, $"长度 {s.Length} 超出上限");
        Assert.EndsWith("…", s);
    }

    [Fact]
    public void LauncherSuspicion_IsCaseInsensitive()
    {
        Assert.True(TextPatterns.HasLauncherSuspicion("SevenDaysLauncher"));
        Assert.True(TextPatterns.HasLauncherSuspicion("LoginPanel"));
        Assert.False(TextPatterns.HasLauncherSuspicion("GameProject.DialogueHandle"));
    }

    [Fact]
    public void DialogueNamedType_RecognizesScenarioAndStoryTypes()
    {
        Assert.True(TextPatterns.IsDialogueNamedType("ScenarioForge.TextTyper"));
        Assert.True(TextPatterns.IsDialogueNamedType("GameProject.ScenarioRunner"));
        Assert.False(TextPatterns.IsDialogueNamedType("UnityEngine.UI.Image"));
    }
}
