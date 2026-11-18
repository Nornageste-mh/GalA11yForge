using GalA11yForge.Detection.Fingerprints;

namespace GalA11yForge.Detection.Tests;

public class FingerprintLoaderTests
{
    [Fact]
    public void LoadDefault_HasResearchSnapshotShape()
    {
        var database = FingerprintLoader.LoadDefault();

        Assert.Equal(FingerprintLoader.ExpectedSchema, database.Schema);
        Assert.Equal(26, database.Engines.Count);
        Assert.Equal(26, database.Rules.Count);
        Assert.Equal(5, database.Disambiguation.Count);
        Assert.Equal(7, database.ConditionTypes.Count);
        Assert.All(database.Rules, rule => Assert.NotNull(database.FindEngine(rule.Engine)));

        var unity = database.FindEngine("unity_mono");
        Assert.NotNull(unity);
        Assert.False(string.IsNullOrWhiteSpace(unity!.Name));
        Assert.Equal("A", unity.Difficulty);
    }

    [Fact]
    public void LoadFromJson_AcceptsMinimalValidDatabase()
    {
        var database = FingerprintLoader.LoadFromJson(TestJson.Minimal, "test://minimal");

        Assert.Single(database.Engines);
        Assert.Single(database.Rules);
        Assert.Equal(95, database.Rules[0].Score);
        Assert.True(database.Rules[0].MatchesAll);
    }

    [Fact]
    public void LoadFromJson_RejectsMalformedJson()
    {
        var exception = Assert.Throws<FingerprintFormatException>(
            () => FingerprintLoader.LoadFromJson("{ not json", "test://broken"));

        Assert.Equal("test://broken", exception.SourcePath);
        Assert.NotEmpty(exception.Problems);
        Assert.Contains("JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromJson_RejectsEmptyInput()
    {
        Assert.Throws<FingerprintFormatException>(() => FingerprintLoader.LoadFromJson("   ", "test://empty"));
    }

    [Fact]
    public void LoadFromJson_RejectsSchemaMismatch()
    {
        var json = TestJson.Minimal.Replace(
            "gala11y.engine-fingerprints/1",
            "gala11y.engine-fingerprints/2",
            StringComparison.Ordinal);

        var exception = Assert.Throws<FingerprintFormatException>(
            () => FingerprintLoader.LoadFromJson(json, "test://schema"));

        Assert.Contains(exception.Problems, problem => problem.Contains("schema", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadFromJson_RejectsRulePointingToUnknownEngine()
    {
        var json = TestJson.Minimal.Replace(
            "\"engine\": \"unity_mono\"",
            "\"engine\": \"ghost_engine\"",
            StringComparison.Ordinal);

        var exception = Assert.Throws<FingerprintFormatException>(
            () => FingerprintLoader.LoadFromJson(json, "test://dangling"));

        Assert.Contains(exception.Problems, problem => problem.Contains("ghost_engine", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadFromJson_RejectsInvalidMagicHex()
    {
        var json = TestJson.Minimal.Replace(
            """{ "type": "file_glob", "glob": "UnityPlayer.dll" }""",
            """{ "type": "magic", "glob": "*.xp3", "offset": 0, "hex": ["ABC"] }""",
            StringComparison.Ordinal);

        var exception = Assert.Throws<FingerprintFormatException>(
            () => FingerprintLoader.LoadFromJson(json, "test://bad-hex"));

        Assert.Contains(exception.Problems, problem => problem.Contains("hex", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadFromJson_RejectsMissingEngineName()
    {
        var json = TestJson.Minimal.Replace(
            "\"name\": \"Unity (Mono)\",",
            string.Empty,
            StringComparison.Ordinal);

        var exception = Assert.Throws<FingerprintFormatException>(
            () => FingerprintLoader.LoadFromJson(json, "test://missing-name"));

        Assert.Contains(exception.Problems, problem => problem.Contains("name", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadFromJson_RejectsDisambiguationResolveOutsideWhen()
    {
        var json = TestJson.Minimal.Replace(
            "\"disambiguation\": [],",
            """
            "disambiguation": [
              { "when": ["unity_mono"], "resolve": "renpy", "why": "测试" }
            ],
            """,
            StringComparison.Ordinal);

        var exception = Assert.Throws<FingerprintFormatException>(
            () => FingerprintLoader.LoadFromJson(json, "test://bad-disambiguation"));

        Assert.Contains(exception.Problems, problem => problem.Contains("resolve", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadFromJson_PreservesUnknownFields()
    {
        var json = TestJson.Minimal
            .Replace(
                "\"generated_at\": \"test\",",
                "\"generated_at\": \"test\",\n  \"future_root_field\": { \"x\": 1 },",
                StringComparison.Ordinal)
            .Replace(
                "\"family\": \"Unity\",",
                "\"family\": \"Unity\",\n      \"x_engine_hint\": \"keep-me\",",
                StringComparison.Ordinal)
            .Replace(
                "\"score\": 95,",
                "\"score\": 95,\n      \"x_rule_hint\": true,",
                StringComparison.Ordinal)
            .Replace(
                """{ "type": "file_glob", "glob": "UnityPlayer.dll" }""",
                """{ "type": "file_glob", "glob": "UnityPlayer.dll", "x_condition_hint": [1, 2] }""",
                StringComparison.Ordinal);

        var database = FingerprintLoader.LoadFromJson(json, "test://extensions");

        Assert.NotNull(database.ExtensionData);
        Assert.True(database.ExtensionData!.ContainsKey("future_root_field"));
        Assert.True(database.Engines[0].ExtensionData!.ContainsKey("x_engine_hint"));
        Assert.True(database.Rules[0].ExtensionData!.ContainsKey("x_rule_hint"));
        Assert.True(database.Rules[0].All[0].ExtensionData!.ContainsKey("x_condition_hint"));
    }

    [Fact]
    public void Load_MissingFile_ThrowsFileNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), "gala11y-detection-tests", "no-such-" + Guid.NewGuid().ToString("N") + ".json");

        Assert.Throws<FileNotFoundException>(() => FingerprintLoader.Load(path));
    }
}

internal static class TestJson
{
    public const string Minimal = """
        {
          "schema": "gala11y.engine-fingerprints/1",
          "generated_at": "test",
          "condition_types": ["file_glob", "file_any", "dir_exists", "magic", "pe_string", "text", "count"],
          "difficulty_legend": { "A": "简单" },
          "disambiguation": [],
          "engines": [
            {
              "id": "unity_mono",
              "name": "Unity (Mono)",
              "family": "Unity",
              "difficulty": "A"
            }
          ],
          "rules": [
            {
              "id": "R01",
              "engine": "unity_mono",
              "score": 95,
              "all": [ { "type": "file_glob", "glob": "UnityPlayer.dll" } ]
            }
          ]
        }
        """;
}
