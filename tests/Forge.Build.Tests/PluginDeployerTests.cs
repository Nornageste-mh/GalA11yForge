using System.IO.Compression;
using System.Text;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Build.Tests;

public sealed class PluginDeployerTests
{
    private static PluginDeployer CreateDeployer(TempArea area, string packagePath, params string[] removals)
    {
        var options = new DeploymentOptions
        {
            DeploymentId = "dep-test-123",
            BepInExPackagePath = packagePath,
            BepInExVersion = "5.4.23.5",
            BepInExSha256 = null,
            VerifyPackageHash = false,
            PluginName = "A11yPlugin",
            PluginVersion = "0.1.0.0",
            AdditionalRemovals = removals.ToList(),
            AllowRedeploy = false,
        };
        return new PluginDeployer(area.CreateWorkspace(), options);
    }

    private static void PrepareGame(TempArea area)
    {
        area.GameFile("doorstop_config.ini", "old-doorstop");
        area.GameFile("BepInEx/plugins/OldA11y.dll", "old-plugin");
        area.GameFile("BepInEx/core/old.dat", "old-core");
    }

    [Fact]
    public void Plan_DryRun_DoesNotWriteBackupOrManifest()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext(dryRun: true);

        var plan = deployer.Plan(context, build);

        Assert.True(plan.Ok, plan.Error?.ToString());
        var value = plan.Value!;
        Assert.True(value.DryRun);
        Assert.Contains(value.Operations, operation => operation.RelativePath == "winhttp.dll" && operation.Action == DeployFileAction.Add);
        Assert.Contains(value.Operations, operation => operation.RelativePath == "doorstop_config.ini" && operation.Action == DeployFileAction.Replace);
        Assert.False(Directory.Exists(value.BackupDirectory));
        Assert.False(File.Exists(value.ManifestPath));

        var outcome = deployer.DeployWithPlan(context, build, value);
        Assert.True(outcome.Ok, outcome.Error?.ToString());
        Assert.False(Directory.Exists(value.BackupDirectory));
        Assert.False(File.Exists(value.ManifestPath));
        Assert.Equal("old-doorstop", area.GameText("doorstop_config.ini"));
        Assert.True(area.GameExists("BepInEx/plugins/OldA11y.dll"));
        Assert.False(area.GameExists("winhttp.dll"));
    }

    [Fact]
    public void Deploy_WritesFilesBacksUpAndSavesManifest()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();

        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        var outcome = deployer.DeployWithPlan(context, build, plan.Value!);

        Assert.True(outcome.Ok, outcome.Error?.ToString());
        Assert.True(File.Exists(plan.Value!.ManifestPath));
        Assert.True(area.GameExists("winhttp.dll"));
        Assert.True(area.GameExists("BepInEx/core/BepInEx.dll"));
        Assert.True(area.GameExists("BepInEx/plugins/A11yPlugin.dll"));
        Assert.False(area.GameExists("BepInEx/plugins/OldA11y.dll"));
        Assert.Equal("fake-doorstop", area.GameText("doorstop_config.ini"));

        var workspace = area.CreateWorkspace();
        var loaded = InstallManifestStore.Load(workspace, plan.Value.ManifestPath);
        Assert.True(loaded.Ok, loaded.Error?.ToString());
        var manifest = loaded.Value!;
        Assert.False(manifest.DryRun);
        Assert.Equal("dep-test-123", manifest.DeploymentId);
        Assert.Equal(area.GameDirectory, manifest.GamePath);
        Assert.Contains(manifest.Files, file => file.RelativePath == "doorstop_config.ini" && file.Action == DeployFileAction.Replace);
        Assert.Contains(manifest.Files, file => file.RelativePath == "BepInEx/plugins/OldA11y.dll" && file.Action == DeployFileAction.Remove);
        Assert.False(InstallManifestStore.HasBom(plan.Value.ManifestPath));

        var backup = Path.Combine(plan.Value.BackupDirectory, "doorstop_config.ini");
        Assert.True(File.Exists(backup));
        Assert.Equal("old-doorstop", File.ReadAllText(backup));
        Assert.True(File.Exists(Path.Combine(plan.Value.BackupDirectory, InstallManifestStore.FileName)));
    }

    [Fact]
    public void Reinstall_AfterRollback_DoesNotRequireAllowRedeploy()
    {
        // 0.2.47：回滚成功后账本标记 rolledBack；补丁已经卸载，第二次安装不该再要求 AllowRedeploy/--force。
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var context = area.CreateContext();

        var firstOptions = new DeploymentOptions
        {
            DeploymentId = "dep-first-123",
            BepInExPackagePath = package,
            BepInExVersion = "5.4.23.5",
            BepInExSha256 = null,
            VerifyPackageHash = false,
            PluginName = "A11yPlugin",
            PluginVersion = "0.1.0.0",
            AdditionalRemovals = new List<string> { "BepInEx/plugins/OldA11y.dll" },
            AllowRedeploy = false,
        };
        var first = new PluginDeployer(area.CreateWorkspace(), firstOptions);
        var plan1 = first.Plan(context, build);
        Assert.True(plan1.Ok, plan1.Error?.ToString());
        var deploy1 = first.DeployWithPlan(context, build, plan1.Value!);
        Assert.True(deploy1.Ok, deploy1.Error?.ToString());
        var manifestPath = plan1.Value!.ManifestPath;

        var rollback = first.RollbackFromFile(context, manifestPath);
        Assert.True(rollback.Ok, rollback.Error?.ToString());
        Assert.True(rollback.Value!.Ok);
        Assert.True(InstallManifestStore.IsRolledBack(area.CreateWorkspace(), manifestPath));

        var secondOptions = new DeploymentOptions
        {
            DeploymentId = "dep-second-456",
            BepInExPackagePath = package,
            BepInExVersion = "5.4.23.5",
            BepInExSha256 = null,
            VerifyPackageHash = false,
            PluginName = "A11yPlugin",
            PluginVersion = "0.1.0.0",
            AdditionalRemovals = new List<string> { "BepInEx/plugins/OldA11y.dll" },
            AllowRedeploy = false,
        };
        var second = new PluginDeployer(area.CreateWorkspace(), secondOptions);
        var plan2 = second.Plan(context, build);
        Assert.True(plan2.Ok, plan2.Error?.ToString());
        Assert.Contains(plan2.Value!.Warnings, warning => warning.Contains("已回滚"));

        var deploy2 = second.DeployWithPlan(context, build, plan2.Value!);
        Assert.True(deploy2.Ok, deploy2.Error?.ToString());
        Assert.True(area.GameExists("BepInEx/plugins/A11yPlugin.dll"));
        Assert.True(File.Exists(Path.Combine(plan2.Value.BackupDirectory, "previous-install-manifest.json")));
    }

    [Fact]
    public void Rollback_RestoresReplaceAndRemoveBeforeDeletingAdds()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var oldPluginHash = FileHashes.Sha256File(Path.Combine(area.GameDirectory, "BepInEx", "plugins", "OldA11y.dll"));
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        var deploy = deployer.DeployWithPlan(context, build, plan.Value!);
        Assert.True(deploy.Ok, deploy.Error?.ToString());

        var rollback = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        var report = rollback.Value!;
        Assert.False(report.AlreadyClean);
        Assert.Contains("doorstop_config.ini", report.RestoredFiles);
        Assert.Contains("BepInEx/plugins/OldA11y.dll", report.RestoredFiles);
        Assert.Contains("winhttp.dll", report.DeletedFiles);
        Assert.Contains("BepInEx/plugins/A11yPlugin.dll", report.DeletedFiles);
        Assert.Empty(report.HashMismatches);
        Assert.Equal("old-doorstop", area.GameText("doorstop_config.ini"));
        Assert.Equal(oldPluginHash, FileHashes.Sha256File(Path.Combine(area.GameDirectory, "BepInEx", "plugins", "OldA11y.dll")));
        Assert.False(area.GameExists("winhttp.dll"));
        Assert.False(area.GameExists("BepInEx/plugins/A11yPlugin.dll"));
        Assert.False(area.GameExists("BepInEx/core/BepInEx.dll"));
        Assert.True(File.Exists(plan.Value.ManifestPath));
    }

    [Fact]
    public void Rollback_DryRun_ReportsPlanWithoutChangingFiles()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, build, plan.Value!).Ok);

        var dryContext = area.CreateContext(dryRun: true);
        var rollback = deployer.RollbackFromFile(dryContext, plan.Value!.ManifestPath);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        Assert.True(rollback.Value!.DryRun);
        Assert.All(rollback.Value.Actions, action => Assert.Equal("planned", action.Outcome));
        Assert.True(area.GameExists("winhttp.dll"));
        Assert.False(area.GameExists("BepInEx/plugins/OldA11y.dll"));
    }

    [Fact]
    public void Plan_MissingPackage_ReturnsDependencyMissingWithDownloadUrl()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = new PluginDeployer(area.CreateWorkspace(), new DeploymentOptions
        {
            DeploymentId = "dep-test-123",
            BepInExPackagePath = null,
            BepInExSha256 = null,
            VerifyPackageHash = false,
        });

        var plan = deployer.Plan(area.CreateContext(dryRun: true), build);

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.DependencyMissing, plan.Error!.Code);
        Assert.Contains("BepInEx", plan.Error.Message);
        Assert.True(plan.Error.Details!.TryGetValue("downloadUrl", out var url));
        Assert.Contains("github.com/BepInEx/BepInEx/releases", url);
    }

    [Fact]
    public void Plan_PackageHashMismatch_IsRejected()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = new PluginDeployer(area.CreateWorkspace(), new DeploymentOptions
        {
            DeploymentId = "dep-test-123",
            BepInExPackagePath = package,
            VerifyPackageHash = true,
        });

        var plan = deployer.Plan(area.CreateContext(dryRun: true), build);

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, plan.Error!.Code);
        Assert.Contains("哈希", plan.Error.Message);
    }

    [Fact]
    public void Package_ZipSlipEntry_IsRejected()
    {
        using var area = new TempArea();
        var package = Path.Combine(area.Root, "evil.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.dll" })
            {
                using var stream = archive.CreateEntry(name).Open();
                var bytes = Encoding.UTF8.GetBytes("x");
                stream.Write(bytes, 0, bytes.Length);
            }

            using var evil = archive.CreateEntry("../outside.dll").Open();
            evil.WriteByte(1);
        }

        var result = BepInExPackage.Open(package, null, verifyHash: false);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, result.Error!.Code);
        Assert.Contains("非法路径", result.Error.Message);
    }

    [Fact]
    public void Package_OpenWithoutExpectedHashStillUsesPinnedHash()
    {
        using var area = new TempArea();
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");

        var verified = BepInExPackage.Open(package, null, verifyHash: true);

        Assert.False(verified.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, verified.Error!.Code);
        Assert.Contains(BepInExPackage.PinnedSha256, verified.Error.Message);

        var unverified = BepInExPackage.Open(package, null, verifyHash: false);
        Assert.True(unverified.Ok, unverified.Error?.ToString());
    }

    [Fact]
    public void Plan_PackageMissingRequiredFile_IsRejected()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackageWithMissingRequiredFile(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package);

        var plan = deployer.Plan(area.CreateContext(dryRun: true), build);

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, plan.Error!.Code);
        Assert.Contains("doorstop_config.ini", plan.Error.Message);
    }

    [Fact]
    public void Plan_RejectsArtifactOutsideWorkspace()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var outside = Path.Combine(area.Root, "outside.dll");
        File.Copy(typeof(GalA11yForge.Core.Workspaces.ForgeWorkspace).Assembly.Location, outside);
        var build = TestArtifacts.CreateBuildResult(outside);
        var deployer = CreateDeployer(area, package);

        var plan = deployer.Plan(area.CreateContext(dryRun: true), build);

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, plan.Error!.Code);
    }

    [Fact]
    public async Task RollbackAsync_CoreManifest_RestoresAndDeletes()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var oldPluginHash = FileHashes.Sha256File(Path.Combine(area.GameDirectory, "BepInEx", "plugins", "OldA11y.dll"));
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();
        var deploy = await deployer.DeployAsync(context, build);
        Assert.True(deploy.Ok, deploy.Error?.ToString());

        var rollback = await deployer.RollbackAsync(context, deploy.Value!.Manifest);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        Assert.Contains("doorstop_config.ini", rollback.Value!.RestoredFiles);
        Assert.Contains("BepInEx/plugins/OldA11y.dll", rollback.Value.RestoredFiles);
        Assert.Contains("winhttp.dll", rollback.Value.DeletedFiles);
        Assert.Equal("old-doorstop", area.GameText("doorstop_config.ini"));
        Assert.Equal(oldPluginHash, FileHashes.Sha256File(Path.Combine(area.GameDirectory, "BepInEx", "plugins", "OldA11y.dll")));
        Assert.False(area.GameExists("winhttp.dll"));
        Assert.False(area.GameExists("BepInEx/plugins/A11yPlugin.dll"));
    }

    [Fact]
    public void Rollback_TamperedBackup_IsRejectedEvenWithForce()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        var deploy = deployer.DeployWithPlan(context, build, plan.Value!);
        Assert.True(deploy.Ok, deploy.Error?.ToString());

        File.WriteAllText(Path.Combine(plan.Value!.BackupDirectory, "doorstop_config.ini"), "tampered");
        var rollback = deployer.RollbackFromFile(context, plan.Value.ManifestPath, new RollbackOptions { Force = true });

        Assert.False(rollback.Ok);
        Assert.Contains("备份", rollback.Error!.Message);
        Assert.Equal("fake-doorstop", area.GameText("doorstop_config.ini"));
    }

    [Fact]
    public void Rollback_TamperedTargetRequiresForce()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, build, plan.Value!).Ok);

        area.GameFile("doorstop_config.ini", "user-changed-this");
        var rejected = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);
        Assert.False(rejected.Ok);
        Assert.Contains("改动", rejected.Error!.Message);
        Assert.Equal("user-changed-this", area.GameText("doorstop_config.ini"));

        var forced = deployer.RollbackFromFile(context, plan.Value.ManifestPath, new RollbackOptions { Force = true });
        Assert.True(forced.Ok, forced.Error?.ToString());
        Assert.Contains(forced.Value!.Warnings, warning => warning.Contains("doorstop_config.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("old-doorstop", area.GameText("doorstop_config.ini"));
    }

    [Fact]
    public void Plan_AdditionalRemovalPathEscape_IsRejected()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "../evil.dll");

        var plan = deployer.Plan(area.CreateContext(dryRun: true), build);

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, plan.Error!.Code);
    }

    [Fact]
    public void Locator_PrefersA11yDirectoryAndMissingExplicitPathIsDependencyMissing()
    {
        using var area = new TempArea();
        var ordinary = TestArtifacts.CreateBepInExPackage(Path.Combine(area.WorkspaceRoot, "downloads"));
        var a11yDirectory = Path.Combine(area.WorkspaceRoot, "SomeGame_a11y", "mod", "dl");
        var preferred = TestArtifacts.CreateBepInExPackage(a11yDirectory);

        var found = BepInExLocator.FindCandidates(area.WorkspaceRoot);
        Assert.True(found.Ok, found.Error?.ToString());
        Assert.NotEmpty(found.Value!);
        var first = found.Value![0];
        Assert.StartsWith(a11yDirectory, first, StringComparison.OrdinalIgnoreCase);

        var missing = BepInExLocator.FindCandidates(area.WorkspaceRoot, Path.Combine(area.Root, "missing.zip"));
        Assert.False(missing.Ok);
        Assert.Equal(ForgeErrorCodes.DependencyMissing, missing.Error!.Code);
        Assert.Contains("github.com/BepInEx/BepInEx/releases", missing.Error.Message);
        Assert.NotEqual(ordinary, preferred);
    }

    [Fact]
    public async Task Deploy_SecondTimeWithoutAllowRedeploy_IsRejected()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();

        var first = await deployer.DeployAsync(context, build);
        Assert.True(first.Ok, first.Error?.ToString());

        var second = deployer.Plan(context, build);

        Assert.False(second.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, second.Error!.Code);
        Assert.Contains("已有部署账本", second.Error.Message);
    }

    [Fact]
    public void Rollback_RemovesBepInExRuntimeArtifacts_WhenBepInExDidNotExistBefore()
    {
        using var area = new TempArea();
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        var deploy = deployer.DeployWithPlan(context, build, plan.Value!);
        Assert.True(deploy.Ok, deploy.Error?.ToString());

        // 模拟启动过一次游戏后 BepInEx 生成的运行期产物（E1）。
        area.GameFile("BepInEx/cache/chainloader_typeloader.dat", "typeloader");
        area.GameFile("BepInEx/cache/harmony_interop_cache.dat", "harmony");
        area.GameFile("BepInEx/config/BepInEx.cfg", "generated-config");
        area.GameFile("BepInEx/LogOutput.log", "runtime-log");
        area.GameFile("BepInEx/LogOutput.old.log", "old-log");
        Directory.CreateDirectory(Path.Combine(area.GameDirectory, "BepInEx", "patchers"));

        var rollback = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        var report = rollback.Value!;
        Assert.Empty(report.HashMismatches);
        Assert.Contains("BepInEx/cache/chainloader_typeloader.dat", report.CleanedRuntimeArtifacts);
        Assert.Contains("BepInEx/cache/harmony_interop_cache.dat", report.CleanedRuntimeArtifacts);
        Assert.Contains("BepInEx/config/BepInEx.cfg", report.CleanedRuntimeArtifacts);
        Assert.Contains("BepInEx/LogOutput.log", report.CleanedRuntimeArtifacts);
        Assert.Contains("BepInEx/LogOutput.old.log", report.CleanedRuntimeArtifacts);
        Assert.Empty(report.SkippedRuntimeArtifacts);
        Assert.False(Directory.Exists(Path.Combine(area.GameDirectory, "BepInEx")));
        Assert.Contains("BepInEx", report.CleanedDirectories);
        Assert.False(area.GameExists("winhttp.dll"));

        var json = report.ToJson();
        Assert.Contains("cleanedRuntimeArtifacts", json);
        Assert.Contains("skippedRuntimeArtifacts", json);
    }

    [Fact]
    public void Rollback_KeepsPreexistingBepInExConfigAndLog()
    {
        using var area = new TempArea();
        area.GameFile("BepInEx/config/BepInEx.cfg", "user-config");
        area.GameFile("BepInEx/LogOutput.log", "user-log");
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        var deploy = deployer.DeployWithPlan(context, build, plan.Value!);
        Assert.True(deploy.Ok, deploy.Error?.ToString());

        // BepInEx 启动后会覆盖根目录日志，并新建 cache；这些都不是安装清单里的文件。
        area.GameFile("BepInEx/LogOutput.log", "runtime-overwrite");
        area.GameFile("BepInEx/cache/chainloader_typeloader.dat", "typeloader");
        Directory.CreateDirectory(Path.Combine(area.GameDirectory, "BepInEx", "patchers"));

        var rollback = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        var report = rollback.Value!;
        Assert.Equal("user-config", area.GameText("BepInEx/config/BepInEx.cfg"));
        Assert.True(area.GameExists("BepInEx/LogOutput.log"));
        Assert.False(area.GameExists("BepInEx/cache/chainloader_typeloader.dat"));
        Assert.False(Directory.Exists(Path.Combine(area.GameDirectory, "BepInEx", "cache")));
        Assert.True(Directory.Exists(Path.Combine(area.GameDirectory, "BepInEx")));
        Assert.Contains("BepInEx/config/BepInEx.cfg", report.SkippedRuntimeArtifacts);
        Assert.Contains("BepInEx/LogOutput.log", report.SkippedRuntimeArtifacts);
        Assert.DoesNotContain("BepInEx/config/BepInEx.cfg", report.CleanedRuntimeArtifacts);
        Assert.DoesNotContain("BepInEx/LogOutput.log", report.CleanedRuntimeArtifacts);
        Assert.Contains("BepInEx/cache", report.CleanedDirectories);
        Assert.Contains("BepInEx/patchers", report.CleanedDirectories);
        Assert.DoesNotContain("BepInEx", report.CleanedDirectories);

        var json = report.ToJson();
        Assert.Contains("skippedRuntimeArtifacts", json);
    }

    [Fact]
    public void Rollback_RuntimeCleanup_KeepsRestoredFileHashesConsistent()
    {
        using var area = new TempArea();
        PrepareGame(area);
        var oldDoorstopHash = FileHashes.Sha256File(Path.Combine(area.GameDirectory, "doorstop_config.ini"));
        var oldPluginHash = FileHashes.Sha256File(Path.Combine(area.GameDirectory, "BepInEx", "plugins", "OldA11y.dll"));
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package, "BepInEx/plugins/OldA11y.dll");
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, build, plan.Value!).Ok);

        area.GameFile("BepInEx/cache/chainloader_typeloader.dat", "typeloader");
        area.GameFile("BepInEx/config/BepInEx.cfg", "generated-config");
        area.GameFile("BepInEx/LogOutput.log", "runtime-log");
        Directory.CreateDirectory(Path.Combine(area.GameDirectory, "BepInEx", "patchers"));

        var rollback = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        var report = rollback.Value!;
        Assert.Empty(report.HashMismatches);
        Assert.Empty(report.Errors);
        Assert.Contains("BepInEx/cache/chainloader_typeloader.dat", report.CleanedRuntimeArtifacts);
        Assert.Equal(oldDoorstopHash, FileHashes.Sha256File(Path.Combine(area.GameDirectory, "doorstop_config.ini")));
        Assert.Equal(oldPluginHash, FileHashes.Sha256File(Path.Combine(area.GameDirectory, "BepInEx", "plugins", "OldA11y.dll")));
        Assert.False(area.GameExists("BepInEx/cache/chainloader_typeloader.dat"));
        Assert.False(area.GameExists("BepInEx/LogOutput.log"));
        Assert.True(area.GameExists("BepInEx/plugins/OldA11y.dll"));
        Assert.True(area.GameExists("BepInEx/core/old.dat"));
        Assert.True(Directory.Exists(Path.Combine(area.GameDirectory, "BepInEx")));
    }

    [Fact]
    public void Rollback_LegacyManifestWithoutBaseline_StillCleansNewBepInExTree()
    {
        using var area = new TempArea();
        var package = TestArtifacts.CreateBepInExPackage(area.Root + "/downloads");
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = CreateDeployer(area, package);
        var context = area.CreateContext();
        var plan = deployer.Plan(context, build);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, build, plan.Value!).Ok);

        // 模拟 v0.2 E2E 的旧账本：没有 BepInExBaseline，只能靠 CreatedDirectories 推断。
        var workspace = area.CreateWorkspace();
        var loaded = InstallManifestStore.Load(workspace, plan.Value!.ManifestPath);
        Assert.True(loaded.Ok, loaded.Error?.ToString());
        var legacy = loaded.Value!;
        legacy.BepInExBaseline = null;
        var saved = InstallManifestStore.Save(workspace, legacy, plan.Value.ManifestPath);
        Assert.True(saved.Ok, saved.Error?.ToString());

        area.GameFile("BepInEx/cache/chainloader_typeloader.dat", "typeloader");
        area.GameFile("BepInEx/config/BepInEx.cfg", "generated-config");
        area.GameFile("BepInEx/LogOutput.log", "runtime-log");
        Directory.CreateDirectory(Path.Combine(area.GameDirectory, "BepInEx", "patchers"));

        var rollback = deployer.RollbackFromFile(context, plan.Value.ManifestPath);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        var report = rollback.Value!;
        Assert.Contains("BepInEx/cache/chainloader_typeloader.dat", report.CleanedRuntimeArtifacts);
        Assert.Contains("BepInEx/config/BepInEx.cfg", report.CleanedRuntimeArtifacts);
        Assert.Contains("BepInEx/LogOutput.log", report.CleanedRuntimeArtifacts);
        Assert.False(Directory.Exists(Path.Combine(area.GameDirectory, "BepInEx")));
        Assert.Contains("BepInEx", report.CleanedDirectories);
    }
}
