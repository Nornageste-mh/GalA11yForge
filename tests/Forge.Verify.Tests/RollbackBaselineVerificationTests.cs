using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

/// <summary>P1-3：回滚模式下基线路径/存在性语义的安全边界。</summary>
public sealed class RollbackBaselineVerificationTests
{
    [Fact]
    public async Task Rollback_rejects_escaping_and_absolute_baseline_paths_without_crash()
    {
        using var fixture = new VerifyFixture();
        fixture.SetBaseline(new GameBaseline
        {
            SchemaVersion = 1,
            GamePath = fixture.GameDir,
            CreatedAt = "2026-11-14T00:00:00+08:00",
            Files = new List<BaselineFileEntry>
            {
                Entry("../outside.txt"),
                Entry(@"C:\Windows\win.ini"),
                Entry("BepInEx/../../escape.txt"),
            },
        });
        fixture.SaveBaseline();

        var result = await fixture.RunAsync(fixture.Options(mode: VerifyMode.Rollback));

        Assert.True(result.Ok, result.Error?.Message);
        var check = Assert.Single(result.Value!.Checks.Where(item => item.Id == "m-07"));
        Assert.Equal(VerifyCheckStatus.Fail, check.Status);
        Assert.Contains("基线路径非法", check.Detail, StringComparison.Ordinal);
        Assert.Equal(VerifyOverall.Fail, result.Value.Overall);
    }

    [Fact]
    public async Task Rollback_fails_when_baseline_recorded_absence_but_file_now_exists()
    {
        using var fixture = new VerifyFixture();
        fixture.SetBaseline(new GameBaseline
        {
            SchemaVersion = 1,
            GamePath = fixture.GameDir,
            CreatedAt = "2026-11-14T00:00:00+08:00",
            Files = new List<BaselineFileEntry>
            {
                new() { RelativePath = "BepInEx/plugins/A11yPlugin.dll", Exists = false },
            },
        });
        fixture.SaveBaseline();

        var result = await fixture.RunAsync(fixture.Options(mode: VerifyMode.Rollback));

        Assert.True(result.Ok, result.Error?.Message);
        var check = Assert.Single(result.Value!.Checks.Where(item => item.Id == "m-07"));
        Assert.Equal(VerifyCheckStatus.Fail, check.Status);
        Assert.Contains("基线记录为不存在，但当前存在", check.Detail, StringComparison.Ordinal);
        Assert.Contains("BepInEx/plugins/A11yPlugin.dll", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_passes_when_baseline_recorded_absence_still_absent()
    {
        using var fixture = new VerifyFixture();
        fixture.ApplyRollbackState();
        fixture.SetBaseline(new GameBaseline
        {
            SchemaVersion = 1,
            GamePath = fixture.GameDir,
            CreatedAt = "2026-11-14T00:00:00+08:00",
            Files = new List<BaselineFileEntry>
            {
                new() { RelativePath = "generated/runtime-cache.tmp", Exists = false },
            },
        });
        fixture.SaveBaseline();

        var result = await fixture.RunAsync(fixture.Options(mode: VerifyMode.Rollback));

        Assert.True(result.Ok, result.Error?.Message);
        var check = Assert.Single(result.Value!.Checks.Where(item => item.Id == "m-07"));
        Assert.Equal(VerifyCheckStatus.Pass, check.Status);
        Assert.NotEqual(VerifyOverall.Fail, result.Value.Overall);
    }

    [Fact]
    public async Task Explicit_baseline_path_outside_workspace_is_rejected()
    {
        using var fixture = new VerifyFixture();
        var options = fixture.Options(mode: VerifyMode.Rollback);
        options.BaselinePath = Path.Combine(fixture.Temp.Root, "outside", "baseline.json");

        var result = await fixture.RunAsync(options);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }

    private static BaselineFileEntry Entry(string relativePath)
        => new() { RelativePath = relativePath, Size = 1, Sha256 = new string('a', 64) };
}
