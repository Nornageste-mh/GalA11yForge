using GalA11yForge.Verify.Baseline;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

public sealed class GameBaselineTests
{
    [Fact]
    public async Task Capture_records_relative_path_size_and_hash()
    {
        using var fixture = new VerifyFixture(standardInstall: false);
        fixture.WriteGame("game.exe", "abc");
        fixture.WriteGame("data/scenario.bin", "scenario");

        var captured = await GameBaselineBuilder.CaptureAsync(fixture.GameDir);

        Assert.True(captured.Ok, captured.Error?.Message);
        var baseline = captured.Value!;
        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.GameDir)), baseline.GamePath);
        Assert.Equal(2, baseline.Files.Count);
        var game = Assert.Single(baseline.Files.Where(file => file.RelativePath == "game.exe"));
        Assert.Equal(3L, game.Size);
        Assert.Equal(Hashes.Sha256("abc"), game.Sha256);
        Assert.Contains(baseline.Files, file => file.RelativePath == "data/scenario.bin");
    }

    [Fact]
    public async Task Capture_honours_excluded_relative_paths()
    {
        using var fixture = new VerifyFixture(standardInstall: false);
        fixture.WriteGame("game.exe", "abc");
        fixture.WriteGame("skip.txt", "skip");

        var captured = await GameBaselineBuilder.CaptureAsync(fixture.GameDir, new[] { "skip.txt" });

        Assert.True(captured.Ok);
        Assert.Single(captured.Value!.Files);
        Assert.Equal("game.exe", captured.Value.Files[0].RelativePath);
    }

    [Fact]
    public async Task Capture_missing_directory_returns_error()
    {
        using var fixture = new VerifyFixture(standardInstall: false);

        var captured = await GameBaselineBuilder.CaptureAsync(Path.Combine(fixture.Temp.Root, "nope"));

        Assert.False(captured.Ok);
        Assert.Equal("E_ARG_INVALID", captured.Error!.Code);
    }

    [Fact]
    public async Task Save_and_load_roundtrip()
    {
        using var fixture = new VerifyFixture(standardInstall: false);
        fixture.WriteGame("game.exe", "abc");
        var baseline = (await GameBaselineBuilder.CaptureAsync(fixture.GameDir)).Value!;
        var path = Path.Combine(fixture.Workspace, "baseline.json");

        var saved = await GameBaselineStore.SaveAsync(baseline, path);
        Assert.True(saved.Ok, saved.Error?.Message);
        Assert.True(File.Exists(path));

        var loaded = await GameBaselineStore.LoadAsync(path);
        Assert.True(loaded.Ok, loaded.Error?.Message);
        Assert.Equal(baseline.GamePath, loaded.Value!.GamePath);
        Assert.Single(loaded.Value.Files);
        Assert.Equal(baseline.Files[0].Sha256, loaded.Value.Files[0].Sha256);
    }

    [Fact]
    public async Task Save_refuses_to_write_into_game_directory()
    {
        using var fixture = new VerifyFixture(standardInstall: false);
        fixture.WriteGame("game.exe", "abc");
        var baseline = (await GameBaselineBuilder.CaptureAsync(fixture.GameDir)).Value!;

        var saved = await GameBaselineStore.SaveAsync(
            baseline,
            Path.Combine(fixture.GameDir, "baseline.json"));

        Assert.False(saved.Ok);
        Assert.Equal("E_GAME_WRITE_DENIED", saved.Error!.Code);
        Assert.False(File.Exists(Path.Combine(fixture.GameDir, "baseline.json")));
    }

    [Fact]
    public async Task Load_invalid_json_returns_invalid_artifact()
    {
        using var fixture = new VerifyFixture(standardInstall: false);
        var path = Path.Combine(fixture.Workspace, "bad-baseline.json");
        File.WriteAllText(path, "{ not-json");

        var loaded = await GameBaselineStore.LoadAsync(path);

        Assert.False(loaded.Ok);
        Assert.Equal("E_ARTIFACT_INVALID", loaded.Error!.Code);
    }
}
