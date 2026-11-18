using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Generators.Tests;

/// <summary>输出目录边界：工作区外、游戏目录、工作区根、非空目录、已有文件、DryRun。</summary>
public sealed class PathOutputTests : GeneratorTestBase
{
    [Fact]
    public async Task Relative_escape_is_rejected()
    {
        var result = await CreateGenerator().GenerateAsync(
            Context(), Plan(Hook("a", "GameNS.A:B")), Path.Combine("..", "escape"));

        AssertError(result, ForgeErrorCodes.PathUnsafe);
        Assert.False(Directory.Exists(Path.Combine(Root, "escape")));
    }

    [Fact]
    public async Task Absolute_path_outside_workspace_is_rejected()
    {
        var outside = Path.Combine(Root, "outside");
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), outside);

        AssertError(result, ForgeErrorCodes.PathUnsafe);
        Assert.False(Directory.Exists(outside));
    }

    [Fact]
    public async Task Output_inside_game_directory_is_rejected_and_game_directory_untouched()
    {
        Directory.CreateDirectory(GameDir);
        var sentinel = Path.Combine(GameDir, "保存确认.txt");
        File.WriteAllText(sentinel, "玩家存档哨兵，不应被改动。");
        var output = Path.Combine(GameDir, "a11y-patch");

        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), output);

        AssertError(result, ForgeErrorCodes.GameWriteDenied);
        Assert.False(Directory.Exists(output));
        Assert.Equal(new[] { sentinel }, Directory.GetFileSystemEntries(GameDir));
        Assert.Equal("玩家存档哨兵，不应被改动。", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task Output_equal_to_workspace_root_is_rejected()
    {
        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), Workspace);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "工作区");
    }

    [Fact]
    public async Task Non_empty_output_without_manifest_is_rejected()
    {
        Directory.CreateDirectory(OutDir);
        File.WriteAllText(Path.Combine(OutDir, "leftover.txt"), "别人的文件");

        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid, "forge.manifest.json");
        Assert.True(File.Exists(Path.Combine(OutDir, "leftover.txt")));
    }

    [Fact]
    public async Task Output_path_that_is_an_existing_file_is_rejected()
    {
        Directory.CreateDirectory(Workspace);
        var occupied = Path.Combine(Workspace, "occupied.txt");
        File.WriteAllText(occupied, "不是目录");

        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), occupied);

        AssertError(result, ForgeErrorCodes.ArgumentInvalid);
        Assert.True(File.Exists(occupied));
    }

    [Fact]
    public async Task Dry_run_validates_but_writes_nothing()
    {
        var result = await CreateGenerator().GenerateAsync(
            Context(dryRun: true), Plan(Hook("a", "GameNS.A:B")), OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.False(Directory.Exists(OutDir));
        Assert.NotEmpty(result.Value!.Artifacts);
        Assert.Equal("true", result.Value.Metadata["dryRun"]);
        Assert.True(result.Value.Metadata.ContainsKey("frameworkFingerprint"));
    }

    [Fact]
    public async Task Successful_generation_leaves_game_directory_untouched()
    {
        Directory.CreateDirectory(GameDir);
        var sentinel = Path.Combine(GameDir, "save.dat");
        File.WriteAllText(sentinel, "save-bytes");

        var result = await CreateGenerator().GenerateAsync(Context(), Plan(Hook("a", "GameNS.A:B")), OutDir);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(new[] { sentinel }, Directory.GetFileSystemEntries(GameDir));
        Assert.Equal("save-bytes", File.ReadAllText(sentinel));
        Assert.True(File.Exists(Path.Combine(OutDir, "Plugin.cs")));
    }
}
