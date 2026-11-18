using System.Text.Json;
using GalA11yForge.Core.Models;
using GalA11yForge.Verify;
using GalA11yForge.Verify.Baseline;
using GalA11yForge.Verify.Models;

namespace GalA11yForge.Build.Tests;

/// <summary>
/// P1-3：安装前基线自动生成，回滚后 Verify(Rollback) 能逐文件证明“当前状态 == 安装前状态”。
/// 全部在 %TEMP% 下运行，不触碰真实游戏目录。
/// </summary>
public sealed class DeploymentBaselineTests
{
    private const string Slug = "sample-game";

    [Fact]
    public async Task Deploy_WritesBaselineForTouchedAndBepInExRuntimeFiles()
    {
        using var area = new TempArea();
        var package = PrepareGame(area, out var build);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());

        var outcome = deployer.DeployWithPlan(context, build, plan.Value!);
        Assert.True(outcome.Ok, outcome.Error?.ToString());

        var baselinePath = BaselinePath(area);
        Assert.True(File.Exists(baselinePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(baselinePath)!, ".baseline.json.*.tmp"));

        var bytes = await File.ReadAllBytesAsync(baselinePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal("baseline", root.GetProperty("artifact").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(area.GameDirectory, root.GetProperty("gamePath").GetString());

        var loaded = await GameBaselineStore.LoadAsync(baselinePath);
        Assert.True(loaded.Ok, loaded.Error?.Message);
        var baseline = loaded.Value!;

        var backupDoorstop = Path.Combine(plan.Value!.BackupDirectory, "doorstop_config.ini");
        var doorstop = Assert.Single(baseline.Files.Where(file => file.RelativePath == "doorstop_config.ini"));
        Assert.True(doorstop.Exists);
        Assert.Equal(FileHashes.Sha256File(backupDoorstop).ToLowerInvariant(), doorstop.Sha256);

        var winhttp = Assert.Single(baseline.Files.Where(file => file.RelativePath == "winhttp.dll"));
        Assert.False(winhttp.Exists);

        Assert.Contains(baseline.Files, file => file.RelativePath == "BepInEx/config/user.cfg" && file.Exists);

        // 运行期必变文件不能进基线，否则 m-07 会把正常启动误判成篡改（R21/R22 真机踩过）。
        Assert.DoesNotContain(baseline.Files, file => file.RelativePath == "BepInEx/LogOutput.log");
        Assert.DoesNotContain(baseline.Files, file => file.RelativePath == "BepInEx/core/old.dat");
    }

    [Fact]
    public async Task Install_Rollback_ThenVerifyRollback_Passes()
    {
        using var area = new TempArea();
        var package = PrepareGame(area, out var build);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, build, plan.Value!).Ok);

        var rollback = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);
        Assert.True(rollback.Ok, rollback.Error?.ToString());
        Assert.Equal("old-doorstop", area.GameText("doorstop_config.ini"));

        var report = await VerifyRollbackAsync(area, plan.Value.ManifestPath);

        var check = Assert.Single(report.Checks.Where(item => item.Id == "m-07"));
        Assert.True(check.Status == VerifyCheckStatus.Pass, check.Detail);
        Assert.True(
            report.Overall == VerifyOverall.Pass,
            string.Join(" | ", report.Checks
                .Where(item => item.Status != VerifyCheckStatus.Pass)
                .Select(item => $"{item.Id}:{item.Status}:{item.Detail}")));
        Assert.Contains("安装前状态一致", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tampered_Preexisting_File_Survives_Rollback_And_Fails_RollbackVerify()
    {
        using var area = new TempArea();
        var package = PrepareGame(area, out var build);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, build, plan.Value!).Ok);

        // 场景文件不在本次账本里，回滚不会碰它；基线负责证明它被改过。
        area.GameFile("BepInEx/config/user.cfg", "tampered-after-install");

        var rollback = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);
        Assert.True(rollback.Ok, rollback.Error?.ToString());

        var report = await VerifyRollbackAsync(area, plan.Value.ManifestPath);

        var check = Assert.Single(report.Checks.Where(item => item.Id == "m-07"));
        Assert.Equal(VerifyCheckStatus.Fail, check.Status);
        Assert.Contains("BepInEx/config/user.cfg", check.Detail, StringComparison.Ordinal);
        Assert.Equal(VerifyOverall.Fail, report.Overall);
    }

    [Fact]
    public async Task Missing_Baseline_After_Rollback_Skips_Instead_of_Passing()
    {
        using var area = new TempArea();
        var package = PrepareGame(area, out var build);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, build, plan.Value!).Ok);
        Assert.True(deployer.RollbackFromFile(context, plan.Value!.ManifestPath).Ok);

        File.Delete(BaselinePath(area));

        var report = await VerifyRollbackAsync(area, plan.Value.ManifestPath);

        var check = Assert.Single(report.Checks.Where(item => item.Id == "m-07"));
        Assert.Equal(VerifyCheckStatus.Skip, check.Status);
        Assert.NotEqual(VerifyOverall.Pass, report.Overall);
        Assert.Contains(report.Unverified, item => item.Contains("基线", StringComparison.Ordinal));
    }

    [Fact]
    public void Failed_Deploy_Does_Not_Leave_Baseline()
    {
        using var area = new TempArea();
        var package = PrepareGame(area, out var build);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());

        // 让计划中的写入内容哈希与真实产物不一致：部署在写入阶段失败并自动回滚。
        var tampered = plan.Value!.Operations.First(operation =>
            operation.Action == DeployFileAction.Add
            && string.Equals(operation.Source, "bepinex", StringComparison.OrdinalIgnoreCase));
        tampered.Sha256After = new string('0', 64);

        var outcome = deployer.DeployWithPlan(context, build, plan.Value);

        Assert.False(outcome.Ok);
        Assert.False(File.Exists(BaselinePath(area)));
        Assert.False(File.Exists(plan.Value.ManifestPath));
        Assert.False(area.GameExists("winhttp.dll"));
    }

    private static string BaselinePath(TempArea area)
        => Path.Combine(area.WorkspaceRoot, "games", Slug, "05-deploy", "baseline.json");

    private static async Task<VerifyReport> VerifyRollbackAsync(TempArea area, string manifestPath)
    {
        var result = await new VerificationService().VerifyAsync(new VerifyOptions
        {
            GameDirectory = area.GameDirectory,
            WorkspaceRoot = area.WorkspaceRoot,
            Slug = Slug,
            ManifestPath = manifestPath,
            Mode = VerifyMode.Rollback,
            ProbeEnvironment = false,
        });

        Assert.True(result.Ok, result.Error?.Message);
        return result.Value!;
    }

    private static PluginDeployer CreateDeployer(TempArea area, string packagePath)
        => new(area.CreateWorkspace(), new DeploymentOptions
        {
            DeploymentId = "dep-test-123",
            BepInExPackagePath = packagePath,
            BepInExVersion = "5.4.23.5",
            BepInExSha256 = null,
            VerifyPackageHash = false,
            PluginName = "A11yPlugin",
            PluginVersion = "0.1.0.0",
            AdditionalRemovals = new List<string> { "BepInEx/plugins/OldA11y.dll" },
            AllowRedeploy = false,
        });

    private static string PrepareGame(TempArea area, out BuildResult build)
    {
        area.GameFile("doorstop_config.ini", "old-doorstop");
        area.GameFile("BepInEx/plugins/OldA11y.dll", "old-plugin");
        area.GameFile("BepInEx/core/old.dat", "old-core");
        area.GameFile("BepInEx/config/user.cfg", "user-config");
        area.GameFile("BepInEx/LogOutput.log", "old-log");

        var package = TestArtifacts.CreateBepInExPackage(Path.Combine(area.Root, "downloads"));
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        build = TestArtifacts.CreateBuildResult(plugin);
        return package;
    }
}
