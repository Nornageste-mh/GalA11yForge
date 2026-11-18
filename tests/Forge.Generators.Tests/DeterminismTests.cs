using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Generators.Tests;

/// <summary>确定性、幂等、旧文件清理、manifest 可信度。</summary>
public sealed class DeterminismTests : GeneratorTestBase
{
    [Fact]
    public async Task Same_input_in_two_workspaces_is_byte_identical()
    {
        var first = await GenerateSampleInto("determinism-a");
        var second = await GenerateSampleInto("determinism-b");

        AssertTreesEqual(first.Tree, second.Tree, "determinism");
    }

    [Fact]
    public async Task Same_slug_yields_same_guid_and_namespace_across_workspaces()
    {
        var first = await GenerateSampleInto("guid-a");
        var second = await GenerateSampleInto("guid-b");

        var firstPlugin = ReadText(Path.Combine(first.Output, "Plugin.cs"));
        var secondPlugin = ReadText(Path.Combine(second.Output, "Plugin.cs"));
        Assert.Equal(firstPlugin, secondPlugin);

        var firstProfile = ReadProfile(first.Output);
        var secondProfile = ReadProfile(second.Output);
        Assert.Equal("GalA11y.ForgeGolden", firstProfile.GetProperty("namespace").GetString());
        Assert.Equal(firstProfile.GetProperty("pluginGuid").GetString(), secondProfile.GetProperty("pluginGuid").GetString());
    }

    [Fact]
    public async Task Different_slug_changes_guid_and_namespace()
    {
        var first = await GenerateSampleInto("slug-a");
        var context = GameContext.Create(SamplePlans.GamePath, Path.Combine(Root, "slug-b"), "second-game");
        var plan = SamplePlans.Create();
        plan.GameSlug = "second-game";
        var output = Path.Combine(context.WorkspaceRoot, "project");

        var result = await CreateGenerator().GenerateAsync(context, plan, output);

        Assert.True(result.Ok, result.Error?.Message);
        var firstProfile = ReadProfile(first.Output);
        var secondProfile = ReadProfile(output);
        Assert.Equal("GalA11y.SecondGame", secondProfile.GetProperty("namespace").GetString());
        Assert.NotEqual(firstProfile.GetProperty("pluginGuid").GetString(), secondProfile.GetProperty("pluginGuid").GetString());
        Assert.Equal("SecondGame.A11y", secondProfile.GetProperty("assemblyName").GetString());
    }

    [Fact]
    public async Task Regeneration_is_idempotent()
    {
        var output = Path.Combine(Root, "idempotent-workspace", "project");
        var context = GameContext.Create(SamplePlans.GamePath, Path.Combine(Root, "idempotent-workspace"), "forge-golden");

        var first = await CreateGenerator().GenerateAsync(context, SamplePlans.Create(), output);
        Assert.True(first.Ok, first.Error?.Message);
        var firstTree = ReadTree(output);

        var second = await CreateGenerator().GenerateAsync(context, SamplePlans.Create(), output);
        Assert.True(second.Ok, second.Error?.Message);

        AssertTreesEqual(firstTree, ReadTree(output), "idempotent");
    }

    [Fact]
    public async Task Regeneration_removes_stale_hook_files_only()
    {
        var workspace = Path.Combine(Root, "stale-workspace");
        var output = Path.Combine(workspace, "project");
        var context = GameContext.Create(SamplePlans.GamePath, workspace, "forge-golden");

        var first = await CreateGenerator().GenerateAsync(
            context,
            Plan(Hook("hook-a", "GameNS.A:A"), Hook("hook-b", "GameNS.A:B")),
            output);
        Assert.True(first.Ok, first.Error?.Message);
        Assert.True(File.Exists(Path.Combine(output, "Hooks", "Hook_hook_b.cs")));

        var second = await CreateGenerator().GenerateAsync(
            context,
            Plan(Hook("hook-a", "GameNS.A:A")),
            output);

        Assert.True(second.Ok, second.Error?.Message);
        Assert.True(File.Exists(Path.Combine(output, "Hooks", "Hook_hook_a.cs")));
        Assert.False(File.Exists(Path.Combine(output, "Hooks", "Hook_hook_b.cs")));
        var manifest = ReadManifest(output);
        var paths = manifest.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        Assert.DoesNotContain("Hooks/Hook_hook_b.cs", paths);
    }

    [Fact]
    public async Task Regeneration_with_another_slug_is_rejected()
    {
        var workspace = Path.Combine(Root, "slug-mismatch-workspace");
        var output = Path.Combine(workspace, "project");
        var context = GameContext.Create(SamplePlans.GamePath, workspace, "forge-golden");

        var first = await CreateGenerator().GenerateAsync(context, Plan(Hook("hook-a", "GameNS.A:A")), output);
        Assert.True(first.Ok, first.Error?.Message);

        var other = Plan(Hook("hook-a", "GameNS.A:A"));
        other.GameSlug = "another-game";
        var second = await CreateGenerator().GenerateAsync(context, other, output);

        AssertError(second, ForgeErrorCodes.ArtifactInvalid, "slug");
    }

    [Fact]
    public async Task Foreign_manifest_is_rejected()
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(
            Path.Combine(OutDir, "forge.manifest.json"),
            """{"schemaVersion":1,"generator":"someone-else","files":[]}""");

        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArtifactInvalid, "其它生成器");
    }

    [Fact]
    public async Task Corrupt_manifest_is_rejected()
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "forge.manifest.json"), "{ 这不是合法 JSON ");

        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArtifactInvalid);
    }

    [Fact]
    public async Task Manifest_lists_every_file_except_itself_with_matching_sha256()
    {
        var generated = await GenerateSampleInto("manifest-workspace");
        var manifest = ReadManifest(generated.Output);
        var entries = manifest.GetProperty("files").EnumerateArray()
            .Select(f => (Path: f.GetProperty("path").GetString()!, Sha: f.GetProperty("sha256").GetString()!))
            .ToList();

        Assert.Equal(generated.Tree.Count - 1, entries.Count);
        Assert.DoesNotContain(entries, e => e.Path == "forge.manifest.json");
        Assert.Equal(entries.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal), entries.Select(e => e.Path));

        foreach (var (path, sha) in entries)
        {
            Assert.True(generated.Tree.ContainsKey(path), $"manifest 列出的文件不存在：{path}");
            Assert.Equal(Sha256(generated.Tree[path]), sha);
        }
    }

    private async Task<(string Output, SortedDictionary<string, byte[]> Tree)> GenerateSampleInto(string workspaceName)
    {
        var workspace = Path.Combine(Root, workspaceName);
        var output = Path.Combine(workspace, "project");
        var context = GameContext.Create(SamplePlans.GamePath, workspace, "forge-golden");

        var result = await CreateGenerator().GenerateAsync(context, SamplePlans.Create(), output);

        Assert.True(result.Ok, result.Error?.Message);
        return (output, ReadTree(output));
    }

    private static JsonElement ReadProfile(string output) =>
        JsonDocument.Parse(ReadTextStatic(Path.Combine(output, "profile.json"))).RootElement;

    private static JsonElement ReadManifest(string output) =>
        JsonDocument.Parse(ReadTextStatic(Path.Combine(output, "forge.manifest.json"))).RootElement;

    private static string ReadTextStatic(string path) => File.ReadAllText(path);
}
