using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

public sealed class OverallTests
{
    [Fact]
    public void No_checks_is_fail()
        => Assert.Equal(VerifyOverall.Fail, VerifyReport.ComputeOverall(Array.Empty<VerifyCheck>()));

    [Fact]
    public void All_pass_is_pass()
    {
        var checks = new[]
        {
            Check(VerifyCheckStatus.Pass, VerifyPriority.P0),
            Check(VerifyCheckStatus.Pass, VerifyPriority.P2),
        };

        Assert.Equal(VerifyOverall.Pass, VerifyReport.ComputeOverall(checks));
    }

    [Fact]
    public void Any_p0_fail_is_fail()
    {
        var checks = new[]
        {
            Check(VerifyCheckStatus.Pass, VerifyPriority.P0),
            Check(VerifyCheckStatus.Fail, VerifyPriority.P0),
            Check(VerifyCheckStatus.Fail, VerifyPriority.P2),
        };

        Assert.Equal(VerifyOverall.Fail, VerifyReport.ComputeOverall(checks));
    }

    [Fact]
    public void Non_p0_fail_is_partial()
    {
        var checks = new[]
        {
            Check(VerifyCheckStatus.Pass, VerifyPriority.P0),
            Check(VerifyCheckStatus.Fail, VerifyPriority.P1),
        };

        Assert.Equal(VerifyOverall.Partial, VerifyReport.ComputeOverall(checks));
    }

    [Fact]
    public void Pass_and_skip_without_fail_is_pass_with_skips()
    {
        var checks = new[]
        {
            Check(VerifyCheckStatus.Pass, VerifyPriority.P0),
            Check(VerifyCheckStatus.Skip, VerifyPriority.P0),
            Check(VerifyCheckStatus.Skip, VerifyPriority.P2),
        };

        Assert.Equal(VerifyOverall.PassWithSkips, VerifyReport.ComputeOverall(checks));
    }

    [Fact]
    public void Skip_only_is_not_pass()
    {
        var checks = new[] { Check(VerifyCheckStatus.Skip, VerifyPriority.P0) };

        Assert.Equal(VerifyOverall.PassWithSkips, VerifyReport.ComputeOverall(checks));
    }

    [Fact]
    public void Lowercase_priority_still_counts_as_p0()
    {
        var checks = new[] { Check(VerifyCheckStatus.Fail, "p0") };

        Assert.Equal(VerifyOverall.Fail, VerifyReport.ComputeOverall(checks));
    }

    private static VerifyCheck Check(VerifyCheckStatus status, string priority) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "test",
        Priority = priority,
        Status = status,
    };
}
