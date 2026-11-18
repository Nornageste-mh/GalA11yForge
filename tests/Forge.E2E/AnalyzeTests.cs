using System.Text.Json;
using Xunit.Sdk;

namespace Forge.E2E;

public sealed class AnalyzeTests
{
    [E2EFact]
    public void Analyze_FakeGame_WritesAnalysisArtifact()
    {
        var sandbox = E2ESandbox.Create("analyze");
        var game = E2ESandbox.FixtureRoot;
        Pipeline.Detect(sandbox, game);

        var result = ForgeCli.Run("analyze", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "analyze", allowPartial: true, hint: $"sandbox：{sandbox.Root}");

        var roots = new[] { sandbox.Workspace, sandbox.OutRoot };
        var path = ArtifactLocator.RequireNewest("analysis.json", roots, "analysis.json");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new XunitException($"analysis.json 不是合法 JSON：{path}{Environment.NewLine}{exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            Assert.True(root.ValueKind == JsonValueKind.Object, $"analysis.json 根节点应为对象：{path}");

            E2EAssert.HasValue(root, "schemaVersion", $"analysis.json（{path}）");

            var hasTarget = JsonHelpers.TryGetProperty(root, "target", out var target)
                && target.ValueKind == JsonValueKind.Object;
            var hasGamePathAndDetection = JsonHelpers.TryGetString(root, out _, "gamePath")
                && JsonHelpers.TryGetProperty(root, "detection", out var detection)
                && detection.ValueKind == JsonValueKind.Object;
            Assert.True(
                hasTarget || hasGamePathAndDetection,
                $"A3：analysis.json 缺少 target 对象（或兼容形态 gamePath+detection）：{path}{Environment.NewLine}{JsonHelpers.Format(root)}");

            var hasCandidates = JsonHelpers.TryGetProperty(root, "candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array
                && candidates.GetArrayLength() >= 1;
            Assert.True(
                hasCandidates,
                $"A3：analysis.json 的 candidates 应为非空数组：{path}{Environment.NewLine}{JsonHelpers.Format(root)}");

            E2EAssert.HasValue(root, "coverage", $"analysis.json（{path}）");

            var hasUnverified = JsonHelpers.TryGetProperty(root, "unverified", out var unverified)
                && unverified.ValueKind == JsonValueKind.Array;
            Assert.True(
                hasUnverified,
                $"A3：analysis.json 的 unverified 应为数组（没有未验证项也要写 []）：{path}{Environment.NewLine}{JsonHelpers.Format(root)}");
        }
    }
}
