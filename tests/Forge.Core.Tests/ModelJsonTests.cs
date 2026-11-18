using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Serialization;

namespace GalA11yForge.Core.Tests;

public sealed class ModelJsonTests
{
    [Fact]
    public void EngineDetection_RoundTrips_And_UsesCamelCase()
    {
        var detection = new EngineDetection
        {
            Family = "renpy",
            Version = "8.2.1",
            Bitness = 64,
            Confidence = 0.8,
            Evidence = ["file:game/script.rpyc"],
        };

        var json = ForgeJson.Serialize(detection);
        var restored = ForgeJson.Deserialize<EngineDetection>(json);

        Assert.NotNull(restored);
        Assert.Equal("renpy", restored!.Family);
        Assert.Equal(64, restored.Bitness);
        Assert.Equal(0.8, restored.Confidence, precision: 10);
        Assert.Contains("\"schemaVersion\"", json);
        Assert.DoesNotContain("SchemaVersion", json);
    }

    [Fact]
    public void EngineDetection_OmitsNullVersion()
    {
        var json = ForgeJson.Serialize(new EngineDetection { Family = "unknown" });
        Assert.DoesNotContain("\"version\"", json);
    }

    [Fact]
    public void TextCandidate_Enum_IsSnakeCase_AndRoundTrips()
    {
        var candidate = new TextCandidate
        {
            Id = "tc-001",
            Kind = TextCandidateKind.Dialogue,
            Source = "assembly:Game.dll!/Dialog::Show",
            Score = 70,
            Evidence = ["string parameter"],
        };

        var json = ForgeJson.Serialize(candidate);
        Assert.Contains("\"kind\": \"dialogue\"", json);

        var restored = ForgeJson.Deserialize<TextCandidate>(json);
        Assert.Equal(TextCandidateKind.Dialogue, restored!.Kind);
        Assert.Equal(70, restored.Score);
    }

    [Fact]
    public void PatchPlan_RoundTrips_AllEnums()
    {
        var plan = TestData.Plan();
        var json = ForgeJson.Serialize(plan);

        Assert.Contains("\"route\": \"semi_automatic\"", json);
        Assert.Contains("\"kind\": \"hook\"", json);
        Assert.Contains("\"severity\": \"high\"", json);

        var restored = ForgeJson.Deserialize<PatchPlan>(json);
        Assert.NotNull(restored);
        Assert.Equal(PlanRoute.SemiAutomatic, restored!.Route);
        Assert.Equal("unity_mono", restored.Target.Engine);
        Assert.Single(restored.PatchPoints);
        Assert.Equal(PatchPointKind.Hook, restored.PatchPoints[0].Kind);
        Assert.Equal(ArtifactKind.Project, restored.Artifacts[0].Kind);
        Assert.True(restored.ManualTodo[0].Blocking);
    }

    [Fact]
    public void AnalysisReport_RoundTrips_CoverageAndUnverified()
    {
        var analysis = TestData.Analysis();
        var json = ForgeJson.Serialize(analysis);

        Assert.Contains("\"notCovered\"", json);
        Assert.Contains("\"manualTodo\"", json);
        Assert.Contains("\"unverified\"", json);

        var restored = ForgeJson.Deserialize<AnalysisReport>(json);
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Candidates.Count);
        Assert.Equal(TextCandidateKind.Choice, restored.Candidates[1].Kind);
        Assert.Contains("选择支 UI", restored.Coverage.NotCovered);
        Assert.NotEmpty(restored.Unverified);
    }

    [Fact]
    public void ProfileDocument_RoundTrips_KeymapAndOptions()
    {
        var profile = TestData.Profile();
        var restored = ForgeJson.Deserialize<ProfileDocument>(ForgeJson.Serialize(profile));

        Assert.NotNull(restored);
        Assert.Equal("unity_mono", restored!.Engine);
        Assert.Equal("Space", restored.Keymap["advance"]);
        Assert.Equal("safe", restored.Options["plugin"].GetProperty("mode").GetString());
    }

    [Fact]
    public void DeployManifest_RoundTrips_HashesAndActions()
    {
        var manifest = TestData.DeployManifest("D:\\Work\\backup\\dep-test-001");
        var json = ForgeJson.Serialize(manifest);

        Assert.Contains("\"action\": \"replace\"", json);
        Assert.Contains("\"sha256Before\"", json);

        var restored = ForgeJson.Deserialize<DeployManifest>(json);
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Files.Count);
        Assert.Equal(DeployFileAction.Add, restored.Files[0].Action);
        Assert.Equal(DeployFileAction.Replace, restored.Files[1].Action);
        Assert.Equal(64, restored.Files[1].Sha256After!.Length);
    }

    [Fact]
    public void ReportDocument_AlwaysSerializesCoverageAndUnverified()
    {
        var report = new ReportDocument { GameSlug = "empty-game" };
        var json = ForgeJson.Serialize(report);

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("coverage", out var coverage));
        Assert.Equal(JsonValueKind.Object, coverage.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("unverified", out var unverified));
        Assert.Equal(JsonValueKind.Array, unverified.ValueKind);
    }

    [Fact]
    public void ForgeResult_Failure_KeepsCodeAndSuggestion()
    {
        var result = GalA11yForge.Core.Results.ForgeResult<int>.Failure("E_TEST", "坏了", "修一下");
        Assert.False(result.Ok);
        Assert.Equal("E_TEST", result.Error!.Code);
        Assert.Contains("修一下", result.Error.ToString());
    }
}
