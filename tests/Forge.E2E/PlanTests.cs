using System.Text.Json;
using Xunit.Sdk;

namespace Forge.E2E;

public sealed class PlanTests
{
    private static readonly HashSet<string> LegalRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        // docs/06（Core PlanRoute 枚举，序列化为 snake_case）
        "automatic",
        "automatic_with_confirmation",
        "semi_automatic",
        "unsupported",
        // docs/09 旧 schema 的 route 取值；两版兼容期内也接受。
        "adapter",
        "generic",
        "ghidra",
        "ocr-fallback",
        "manual",
    };

    [E2EFact]
    public void Plan_FakeGame_WritesAutomaticPlan()
    {
        var sandbox = E2ESandbox.Create("plan");
        var game = E2ESandbox.FixtureRoot;
        Pipeline.Detect(sandbox, game);
        Pipeline.Analyze(sandbox, game);

        var result = ForgeCli.Run("plan", game, sandbox.Workspace, sandbox.OutRoot);
        E2EAssert.Succeeded(result, "plan", allowPartial: true, hint: $"sandbox：{sandbox.Root}");

        var roots = new[] { sandbox.Workspace, sandbox.OutRoot };
        var path = ArtifactLocator.RequireNewest("plan.json", roots, "plan.json");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException exception)
        {
            throw new XunitException($"plan.json 不是合法 JSON：{path}{Environment.NewLine}{exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            Assert.True(root.ValueKind == JsonValueKind.Object, $"plan.json 根节点应为对象：{path}");

            if (!JsonHelpers.TryGetString(root, out var route, "route"))
            {
                throw new XunitException(
                    $"A4：plan.json 缺少 route 字段：{path}{Environment.NewLine}{JsonHelpers.Format(root)}");
            }

            Assert.True(
                LegalRoutes.Contains(route),
                $"A4：plan route=\"{route}\" 不在合法集合内（{string.Join("、", LegalRoutes)}）：{path}");

            var hasPatchPoints = JsonHelpers.TryGetProperty(root, "patchPoints", out var patchPoints)
                && patchPoints.ValueKind == JsonValueKind.Array
                && patchPoints.GetArrayLength() >= 1;
            Assert.True(
                hasPatchPoints,
                $"A4：plan.json 的 patchPoints 应为非空数组：{path}{Environment.NewLine}{JsonHelpers.Format(root)}");
        }
    }
}
