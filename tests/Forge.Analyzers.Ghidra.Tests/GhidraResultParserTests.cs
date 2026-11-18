using System.Text.Json;

namespace GalA11yForge.Analyzers.Ghidra.Tests;

public sealed class GhidraResultParserTests
{
    [Fact]
    public void Parse_SnakeCaseScriptOutput_MapsAllStructuredFields()
    {
        var document = GhidraResultParser.Parse(Fixtures.CandidatesJson(packed: true));

        Assert.Equal("gala11y.ghidra.candidates/1", document.Schema);
        Assert.Equal("12.1.4", document.Tool.Version);
        Assert.Equal("game.exe", document.Binary.Name);
        Assert.Equal(4_583_168, document.Binary.SizeBytes);
        Assert.True(document.Binary.PackedSuspected);
        Assert.False(document.Binary.DynamicApiResolutionSuspected);
        Assert.Equal(32, document.Binary.Bits);
        Assert.Single(document.Binary.Sections);
        Assert.Equal(7.91, document.Binary.Sections[0].Entropy, 2);

        Assert.Equal(3, document.TextApiSurface.Present.Count);
        Assert.Contains("TextOutW", document.TextApiSurface.Present);
        Assert.Single(document.TextApiSurface.ImportSlots);
        Assert.Equal(2, document.TextApiSurface.ImportSlots[0].RefSites);

        var candidate = Assert.Single(document.Candidates);
        Assert.Equal("FUN_00401000", candidate.Function);
        Assert.Equal(2, candidate.Evidence.CallerCount);
        Assert.Equal(1, candidate.Evidence.CjkStringRefs);
        Assert.Equal(2, candidate.Evidence.Strings.Count);
        Assert.Equal("こんにちは、世界。これは十分に長いテスト用の文字列です。", candidate.Evidence.Strings[0].Text);
        Assert.Equal("hook TextOutW and read the string pointer", candidate.Hook.Strategy);

        Assert.Equal(420, document.Stats.Functions);
        Assert.Equal(12, document.StringTable.Utf16ScanCjkStrings);
        Assert.Equal(13, document.Stats.CjkStrings);
        Assert.Single(document.EngineHints);
    }

    [Fact]
    public void Parse_UnsupportedSchema_Throws()
    {
        var json = Fixtures.CandidatesJson().Replace(
            "gala11y.ghidra.candidates/1",
            "someone.else/9",
            StringComparison.Ordinal);

        var ex = Assert.Throws<JsonException>(() => GhidraResultParser.Parse(json));
        Assert.Contains("schema 不受支持", ex.Message);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsWithContext()
    {
        var ex = Assert.Throws<JsonException>(() => GhidraResultParser.Parse("{not json"));
        Assert.Contains("不是合法 JSON", ex.Message);
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsFalseAndError()
    {
        var ok = GhidraResultParser.TryParse("{", out var document, out var error);

        Assert.False(ok);
        Assert.Null(document);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParseWroteLine_ExtractsPathAndCounts()
    {
        var stdout = "INFO  ANALYZING...\nWROTE C:\\ws\\out\\candidates.json bytes=1234 candidates=2 cjk_strings=17 packed=true\n";

        var ok = GhidraResultParser.TryParseWroteLine(stdout, out var path, out var candidates, out var cjk, out var packed);

        Assert.True(ok);
        Assert.Equal(@"C:\ws\out\candidates.json", path);
        Assert.Equal(2, candidates);
        Assert.Equal(17, cjk);
        Assert.True(packed);
    }

    [Fact]
    public void TryParseWroteLine_NoLine_ReturnsFalse()
    {
        var ok = GhidraResultParser.TryParseWroteLine("INFO nothing\n", out var path, out _, out _, out _);

        Assert.False(ok);
        Assert.Null(path);
    }
}
