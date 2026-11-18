using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

public sealed class RollbackVerificationTests
{
    [Fact]
    public async Task Clean_rollback_passes_and_reports_cleanup()
    {
        using var fixture = new VerifyFixture();
        fixture.ApplyRollbackState();

        var report = await VerifyAsync(fixture);

        Assert.Equal(VerifyOverall.Pass, report.Overall);
        Assert.Equal(VerifyMode.Rollback, report.Mode);
        Assert.True(report.Cleanup.Required);
        Assert.True(report.Cleanup.Uninstalled);
        Assert.True(report.Cleanup.HashMatch);
        Assert.Equal(3, report.Cleanup.Restored.Count);
        Assert.Contains(report.Cleanup.Restored, item => item.Contains("已删除", StringComparison.Ordinal));
        Assert.Contains(report.Cleanup.Restored, item => item.Contains("已还原", StringComparison.Ordinal));
        ReportAssert.HasStatus(report, "r-01", VerifyCheckStatus.Pass);
        ReportAssert.HasStatus(report, "r-02", VerifyCheckStatus.Pass);
        ReportAssert.HasStatus(report, "r-03", VerifyCheckStatus.Pass);
        Assert.Contains(report.Unverified, item => item.Contains("回滚后原版", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rollback_leftover_new_file_fails()
    {
        using var fixture = new VerifyFixture();
        fixture.ApplyRollbackState();
        fixture.WriteGame(VerifyFixture.WinHttpRelativePath, VerifyFixture.WinHttpContent);

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "r-02", VerifyCheckStatus.Fail);
        Assert.False(report.Cleanup.Uninstalled);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
        Assert.Contains(report.Cleanup.Restored, item => item.Contains("仍有残留", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rollback_restored_file_hash_mismatch_fails()
    {
        using var fixture = new VerifyFixture();
        fixture.ApplyRollbackState();
        fixture.WriteGame(VerifyFixture.DoorstopRelativePath, "[General]\nenabled=true\n");

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "r-01", VerifyCheckStatus.Fail);
        Assert.False(report.Cleanup.Uninstalled);
        Assert.False(report.Cleanup.HashMatch);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Rollback_missing_restored_file_fails()
    {
        using var fixture = new VerifyFixture();
        fixture.ApplyRollbackState();
        File.Delete(fixture.PathOfGame(VerifyFixture.DoorstopRelativePath));

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "r-01", VerifyCheckStatus.Fail);
        Assert.False(report.Cleanup.Uninstalled);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Rollback_mode_on_install_state_reports_not_uninstalled()
    {
        using var fixture = new VerifyFixture();

        var report = await VerifyAsync(fixture);

        ReportAssert.HasStatus(report, "r-01", VerifyCheckStatus.Fail);
        ReportAssert.HasStatus(report, "r-02", VerifyCheckStatus.Fail);
        Assert.False(report.Cleanup.Uninstalled);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Rollback_does_not_run_install_or_bepinex_checks()
    {
        using var fixture = new VerifyFixture();
        fixture.ApplyRollbackState();

        var report = await VerifyAsync(fixture);

        Assert.DoesNotContain(report.Checks, check => check.Id.StartsWith("m-04", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Checks, check => check.Id.StartsWith("m-05", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Checks, check => check.Id.StartsWith("b-", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Checks, check => check.Id == "e-01");
    }

    private static async Task<VerifyReport> VerifyAsync(VerifyFixture fixture)
    {
        var result = await fixture.RunAsync(fixture.Options(mode: VerifyMode.Rollback));
        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!;
    }
}
