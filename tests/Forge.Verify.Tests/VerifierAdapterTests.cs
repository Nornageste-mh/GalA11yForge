using GalA11yForge.Core.Models;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Verify.Tests;

public sealed class VerifierAdapterTests
{
    [Fact]
    public async Task Clean_install_maps_to_passed_contract()
    {
        using var fixture = new VerifyFixture();
        var verifier = CreateVerifier();

        var result = await verifier.VerifyAsync(
            GameContext.Create(fixture.GameDir, fixture.Workspace, fixture.Slug),
            deployment: null);

        Assert.True(result.Ok, result.Error?.Message);
        var contract = result.Value!;
        Assert.True(contract.Passed);
        Assert.Equal(18, contract.Checks.Count);
        Assert.All(contract.Checks, check => Assert.True(check.Passed));
        Assert.Empty(contract.ManualTodo);
        Assert.Contains(contract.Unverified, item => item.Contains("未启动游戏", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skip_only_result_is_not_mapped_to_passed()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.LogPath);

        var result = await CreateVerifier().VerifyAsync(
            GameContext.Create(fixture.GameDir, fixture.Workspace, fixture.Slug),
            deployment: null);

        Assert.True(result.Ok, result.Error?.Message);
        var contract = result.Value!;
        Assert.False(contract.Passed);
        var failed = Assert.Single(contract.Checks.Where(check => check.Name.Contains("BepInEx 日志关键行", StringComparison.Ordinal)));
        Assert.False(failed.Passed);
        Assert.StartsWith("fail:", failed.Detail, StringComparison.Ordinal);
        Assert.Contains(contract.ManualTodo, item => item.Blocking);
    }

    [Fact]
    public async Task P0_failure_produces_blocking_manual_todo()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.PathOfGame(VerifyFixture.PluginRelativePath));

        var result = await CreateVerifier().VerifyAsync(
            GameContext.Create(fixture.GameDir, fixture.Workspace, fixture.Slug),
            deployment: null);

        Assert.True(result.Ok, result.Error?.Message);
        var contract = result.Value!;
        Assert.False(contract.Passed);
        Assert.Contains(contract.ManualTodo, item => item.Id == "verify-m-04" && item.Blocking);
        Assert.Contains(contract.Checks, check =>
            check.Name.Contains("安装态文件在位", StringComparison.Ordinal)
            && !check.Passed
            && check.Detail!.StartsWith("fail:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deployment_manifest_is_used_when_disk_manifest_is_absent()
    {
        using var fixture = new VerifyFixture();
        File.Delete(fixture.ManifestPath);
        var deployment = new DeployResult
        {
            Manifest = fixture.Manifest,
            BackupDirectory = fixture.BackupRoot,
        };

        var result = await CreateVerifier().VerifyAsync(
            GameContext.Create(fixture.GameDir, fixture.Workspace, fixture.Slug),
            deployment);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.True(result.Value!.Passed);
    }

    [Fact]
    public async Task Invalid_context_returns_argument_error()
    {
        var result = await CreateVerifier().VerifyAsync(
            GameContext.Create(string.Empty, string.Empty),
            deployment: null);

        Assert.False(result.Ok);
        Assert.Equal("E_ARG_INVALID", result.Error!.Code);
    }

    private static Verifier CreateVerifier()
        => new(new VerificationService(new FakeEnvironmentProbe(FakeEnvironmentProbe.AllAvailable())));
}
