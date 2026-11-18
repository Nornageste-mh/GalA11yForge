using System.Text.Json;
using GalA11yForge.Adapters.UnityMono.Internal;
using GalA11yForge.Core.Models;

namespace GalA11yForge.Adapters.UnityMono.Tests;

public sealed class ScoringTests
{
    [Fact]
    public void S9_StringParameterAndTypewriterSink_Adds20AndDialogue()
    {
        var facts = Facts(
            "AnimateText",
            hasString: true,
            typeLevel: true,
            typeFields: 1,
            textSinks: new[] { Sink("TMPro.TMP_Text::set_text") },
            typewriterSinks: new[] { Sink("DG.Tweening.ShortcutExtensionsTMPText::DOText") });

        var score = UnityMonoScoring.Evaluate(facts);

        Assert.NotNull(score);
        Assert.Equal(20, score!.S9);
        Assert.True(score.Score >= 70, $"score={score.Score}");
        Assert.Equal(TextCandidateKind.Dialogue, score.Kind);
        Assert.Contains(score.Evidence, line => line.Contains("S9+20", StringComparison.Ordinal));
        Assert.Contains(score.Evidence, line => line.Contains("DOText", StringComparison.Ordinal));
    }

    [Fact]
    public void S9_WithoutStringParameter_DoesNotTrigger()
    {
        var facts = Facts(
            "AnimateText",
            typeLevel: true,
            typewriterSinks: new[] { Sink("DG.Tweening.ShortcutExtensionsTMPText::DOText") });

        var score = UnityMonoScoring.Evaluate(facts);

        Assert.NotNull(score);
        Assert.Equal(0, score!.S9);
    }

    [Fact]
    public void TextComponentSetter_IsFilteredAsPlumbing()
    {
        var facts = Facts(
            "set_text",
            typeFullName: "TMPro.TMP_Text",
            typeName: "TMP_Text",
            textSinks: new[] { Sink("TMPro.TMP_Text::set_text") });

        Assert.Null(UnityMonoScoring.Evaluate(facts));
    }

    [Fact]
    public void SystemType_IsFiltered()
    {
        var facts = Facts("ShowText", typeFullName: "System.SomeHelper", typeName: "SomeHelper", hasString: true);

        Assert.Null(UnityMonoScoring.Evaluate(facts));
    }

    [Fact]
    public void NegativeRules_DebugAndShortLiterals_ClampToZero()
    {
        var facts = Facts(
            "LogVersion",
            debug: true,
            ldstr: 2,
            nonDialogue: 2,
            maxLiteral: 1,
            callers: 1,
            allInitLike: true);

        var score = UnityMonoScoring.Evaluate(facts);

        Assert.NotNull(score);
        Assert.Equal(-20, score!.N1);
        Assert.Equal(-15, score.N2);
        Assert.Equal(-10, score.N3);
        Assert.Equal(-10, score.N4);
        Assert.Equal(0, score.Score);
        Assert.Equal("low", score.Confidence);
    }

    [Fact]
    public void ChoiceName_GetsChoiceKind()
    {
        var facts = Facts("Choice", hasString: false, typeLevel: true, typeFields: 1,
            textSinks: new[] { Sink("TMPro.TMP_Text::set_text") });

        var score = UnityMonoScoring.Evaluate(facts);

        Assert.NotNull(score);
        Assert.Equal(TextCandidateKind.Choice, score!.Kind);
        Assert.True(score.Score >= 70);
    }

    [Fact]
    public void Language_DefaultIsSc_AndAliasesMap()
    {
        Assert.Equal("sc", UnityMonoLanguage.Resolve(null).Code);
        Assert.Equal("default", UnityMonoLanguage.Resolve(null).Source);
        Assert.Equal("ja", UnityMonoLanguage.Resolve(Profile("ja")).Code);
        Assert.Equal("tc", UnityMonoLanguage.Resolve(Profile("zh-tw")).Code);
        Assert.Equal("en", UnityMonoLanguage.Resolve(Profile("us")).Code);
        Assert.Equal("sc", UnityMonoLanguage.Resolve(Profile("sc")).Code);
    }

    [Fact]
    public void Language_UnknownValue_FallsBackToScAndMarksUnrecognized()
    {
        var language = UnityMonoLanguage.Resolve(Profile("klingon"));

        Assert.Equal("sc", language.Code);
        Assert.False(language.Recognized);
        Assert.Equal("klingon", language.Requested);
    }

    [Fact]
    public void Language_NestedObjectValue_IsRead()
    {
        using var document = JsonDocument.Parse("""{"language":{"code":"ja"}}""");
        var profile = new ProfileDocument
        {
            Options = new Dictionary<string, JsonElement>
            {
                ["unity_mono.language"] = document.RootElement.GetProperty("language").Clone(),
            },
        };

        Assert.Equal("ja", UnityMonoLanguage.Resolve(profile).Code);
        Assert.Equal("unity_mono.language", UnityMonoLanguage.Resolve(profile).Source);
    }

    [Fact]
    public void Utage_MessageWindowPageTextChange_GetsPropertyChainScore()
    {
        var facts = Facts(
            "PageTextChange",
            typeFullName: "Utage.AdvMessageWindow",
            typeName: "AdvMessageWindow",
            frameworkId: "utage");

        var score = UnityMonoScoring.Evaluate(facts);

        Assert.NotNull(score);
        Assert.Equal(55, score!.S10);
        Assert.True(score.Score >= 70, $"score={score.Score}");
        Assert.Equal(TextCandidateKind.Dialogue, score.Kind);
        Assert.Contains(score.Evidence, line => line.Contains("S10+55", StringComparison.Ordinal));
        Assert.Contains(
            score.Evidence,
            line => line.Contains("文本属性链：Text.OriginalText", StringComparison.Ordinal));
    }

    [Fact]
    public void Utage_PropertyChainOnlyMatchesKnownEntry()
    {
        Assert.True(MonoTextPatterns.IsUtageDialogueType("Utage.AdvMessageWindow"));
        Assert.True(
            MonoTextPatterns.TryUtagePropertyChain("Utage.AdvMessageWindow", "PageTextChange", out var chain));
        Assert.Equal(new[] { "Text", "OriginalText" }, chain);
        Assert.False(
            MonoTextPatterns.TryUtagePropertyChain("Utage.AdvMessageWindow", "set_Text", out _));
        Assert.False(
            MonoTextPatterns.TryUtagePropertyChain("Utage.AdvUguiMessageWindow", "OnTextChanged", out _));
    }

    private static ProfileDocument Profile(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return new ProfileDocument
        {
            Options = new Dictionary<string, JsonElement> { ["language"] = document.RootElement.Clone() },
        };
    }

    private static UnityMonoSinkHit Sink(string api) => new()
    {
        Api = api,
        Offset = 0x10,
        ArgumentSource = "parameter",
    };

    private static UnityMonoMethodFacts Facts(
        string method,
        string typeFullName = "FakeGame.UIText",
        string typeName = "UIText",
        bool hasString = false,
        bool typeLevel = false,
        int typeFields = 0,
        IReadOnlyList<UnityMonoSinkHit>? textSinks = null,
        IReadOnlyList<UnityMonoSinkHit>? typewriterSinks = null,
        bool debug = false,
        int ldstr = 0,
        int nonDialogue = 0,
        int maxLiteral = 0,
        int callers = 0,
        bool allInitLike = true,
        string? frameworkId = null)
    {
        var ns = typeFullName.Contains('.') ? typeFullName[..typeFullName.LastIndexOf('.')] : string.Empty;
        return new UnityMonoMethodFacts
        {
            AssemblyFile = "Assembly-CSharp.dll",
            AssemblyName = "Assembly-CSharp",
            TypeFullName = typeFullName,
            TypeName = typeName,
            Namespace = ns,
            MethodName = method,
            FullName = "System.Void " + typeFullName + "::" + method + "()",
            Signature = "System.Void " + typeFullName + "::" + method + "()",
            HasStringParameter = hasString,
            TypeLevelPipeline = typeLevel,
            TextFieldCount = typeFields,
            TextFieldNames = typeFields > 0 ? new List<string> { "_label" } : new List<string>(),
            TextSinkCalls = textSinks?.ToList() ?? new List<UnityMonoSinkHit>(),
            TypewriterSinkCalls = typewriterSinks?.ToList() ?? new List<UnityMonoSinkHit>(),
            IsSemanticName = MonoTextPatterns.HasSemanticName(method),
            IsUiName = MonoTextPatterns.HasUiName(typeName),
            IsChoiceLike = MonoTextPatterns.IsChoiceName(method),
            IsMenuLike = MonoTextPatterns.IsMenuName(method),
            IsDebugName = debug,
            LdstrCount = ldstr,
            NonDialogueLiteralCount = nonDialogue,
            MaxLiteralLength = maxLiteral,
            CallerCount = callers,
            AllCallersInitLike = allInitLike,
            FrameworkId = frameworkId,
        };
    }
}
