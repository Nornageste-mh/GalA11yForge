using System.Text;
using System.Text.Json;
using GalA11yForge.Core;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Build.Tests;

/// <summary>
/// Ren'Py 覆盖层部署器：安装/替换/回滚/哈希/越界/dry-run/账本冲突。
/// 全部在临时目录里跑，不接触真实游戏。
/// </summary>
public sealed class OverlayDeployerTests
{
    private const string Slug = "sample-game";

    private static string GenerationDirectory(TempArea area)
        => Path.Combine(area.WorkspaceRoot, "games", Slug, "03-generate");

    private static OverlayDeployer NewDeployer(
        TempArea area,
        string? generationDirectory = null,
        bool allowRedeploy = false,
        string? deploymentId = null)
        => new(
            area.CreateWorkspace(),
            new OverlayDeployOptions
            {
                AllowRedeploy = allowRedeploy,
                DeploymentId = deploymentId,
                GenerationDirectory = generationDirectory ?? GenerationDirectory(area),
            });

    private static void WriteGeneration(
        TempArea area,
        (string Path, string Content)[] files,
        string?[]? planTargets = null,
        string? patchVersion = "0.1.0.0",
        bool writePlan = true,
        bool conflictOverlay = false)
    {
        var root = GenerationDirectory(area);
        Directory.CreateDirectory(root);
        var manifestFiles = new List<object>();
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            manifestFiles.Add(new
            {
                path = relative,
                role = "overlay",
                bytes = new FileInfo(path).Length,
                sha256 = FileHashes.Sha256File(path),
            });
        }

        var manifest = new Dictionary<string, object?>
        {
            ["schema"] = OverlayDeployer.OverlaySchema,
            ["generator"] = "gala11y-forge-renpy",
            ["generator_version"] = "0.2.1.0",
            ["patch_version"] = patchVersion,
            ["created_at"] = "2026-11-14T00:00:00+00:00",
            ["game"] = new { root = area.GameDirectory, name = Slug, engine = "renpy", python_major = 2, is_steam = false },
            ["files"] = manifestFiles,
        };
        File.WriteAllText(
            Path.Combine(root, OverlayDeployer.OverlayManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        if (!writePlan)
        {
            return;
        }

        var targets = planTargets ?? files.Select(file => file.Path).ToArray();
        var steps = new List<object>();
        if (files.Any(file => file.Path.StartsWith("game/", StringComparison.Ordinal)))
        {
            steps.Add(new { op = "mkdir", path = "game/a11y_platform" });
        }

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            steps.Add(new { op = "copy", source = target, target, sha256 = "0" });
        }

        File.WriteAllText(
            Path.Combine(root, OverlayDeployer.DeployPlanFileName),
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["schema"] = OverlayDeployer.DeployPlanSchema,
                    ["game_root"] = area.GameDirectory,
                    ["guard"] = "workspace-only",
                    ["conflict_overlay"] = conflictOverlay,
                    ["steps"] = steps,
                    ["verify"] = Array.Empty<string>(),
                },
                new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static (string Path, string Content)[] TwoFiles()
        => new[]
        {
            ("game/a11y_platform/00_contract.rpy", "init python:\n    pass\n"),
            ("a11y_say.vbs", "' generated speech bridge\n"),
        };

    [Fact]
    public void Plan_DryRun_MapsTargetsWithoutWriting()
    {
        using var area = new TempArea();
        area.GameFile("a11y_say.vbs", "' old bridge\n");
        WriteGeneration(area, TwoFiles());

        var context = area.CreateContext(dryRun: true);
        var plan = NewDeployer(area).Plan(context);

        Assert.True(plan.Ok, plan.Error?.ToString());
        var value = plan.Value!;
        Assert.True(value.DryRun);
        Assert.Equal(2, value.Operations.Count);
        Assert.Contains(value.Operations, operation => operation.RelativePath == "a11y_say.vbs" && operation.Action == DeployFileAction.Replace);
        Assert.Contains(value.Operations, operation => operation.RelativePath == "game/a11y_platform/00_contract.rpy" && operation.Action == DeployFileAction.Add);
        Assert.Equal("' old bridge\n", area.GameText("a11y_say.vbs"));
        Assert.False(area.GameExists("game/a11y_platform/00_contract.rpy"));
        Assert.False(Directory.Exists(value.BackupDirectory));
        Assert.False(File.Exists(value.ManifestPath));
        Assert.Empty(Directory.EnumerateFiles(area.WorkspaceRoot, "install-manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Plan_RefusesConflictOverlayWithoutForce_AndAllowsWithForce()
    {
        // 0.2.24 事故：游戏自带旧 a11y_game/a11y_platform 时，混装会让 A11yHost
        // 契约不一致、游戏启动即崩。默认必须拒绝安装，只有 --force 才允许叠加。
        using var area = new TempArea();
        area.GameFile("game/a11y_game/90_plugin.rpy", "init 1300 python:\n    A11yHost.validate()\n");
        WriteGeneration(area, TwoFiles(), conflictOverlay: true);

        var context = area.CreateContext();
        var refused = NewDeployer(area).Plan(context);
        Assert.False(refused.Ok);
        Assert.Equal(ForgeErrorCodes.EngineUnsupported, refused.Error!.Code);
        Assert.Contains("旧的无障碍覆盖层", refused.Error.Message);
        Assert.Contains("uninstall.ps1", refused.Error.Suggestion);
        Assert.True(refused.Error.Details!.TryGetValue("legacyOverlayDirectories", out var legacy));
        Assert.Contains("game/a11y_game", legacy);

        var allowed = NewDeployer(area, allowRedeploy: true).Plan(context);
        Assert.True(allowed.Ok, allowed.Error?.ToString());
        Assert.Contains(allowed.Value!.Warnings, warning => warning.Contains("--force"));
    }

    [Fact]
    public void Deploy_WritesFilesBacksUpReplacementsAndSavesManifest()
    {
        using var area = new TempArea();
        area.GameFile("a11y_say.vbs", "' old bridge\n");
        WriteGeneration(area, TwoFiles());

        var context = area.CreateContext();
        var plan = NewDeployer(area).Plan(context);
        Assert.True(plan.Ok, plan.Error?.ToString());

        var deployer = NewDeployer(area);
        var outcome = deployer.DeployWithPlan(context, plan.Value!);
        Assert.True(outcome.Ok, outcome.Error?.ToString());

        Assert.True(area.GameExists("game/a11y_platform/00_contract.rpy"));
        Assert.Equal("' generated speech bridge\n", area.GameText("a11y_say.vbs"));

        var manifestPath = plan.Value!.ManifestPath;
        Assert.True(File.Exists(manifestPath));
        var manifest = InstallManifestStore.Load(area.CreateWorkspace(), manifestPath);
        Assert.True(manifest.Ok, manifest.Error?.ToString());
        var ledger = manifest.Value!;
        Assert.Equal(2, ledger.Files.Count);
        Assert.Equal(DeployFileAction.Replace, ledger.Files.Single(file => file.RelativePath == "a11y_say.vbs").Action);
        Assert.Equal(DeployFileAction.Add, ledger.Files.Single(file => file.RelativePath == "game/a11y_platform/00_contract.rpy").Action);

        var backup = Path.Combine(ledger.BackupRoot, "a11y_say.vbs".Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(backup));
        Assert.Equal("' old bridge\n", File.ReadAllText(backup));
        Assert.False(File.Exists(Path.Combine(ledger.BackupRoot, "install-manifest.json")));
    }

    [Fact]
    public void Rollback_RestoresReplacementsDeletesAddsAndCleansDirectories()
    {
        using var area = new TempArea();
        area.GameFile("a11y_say.vbs", "' old bridge\n");
        WriteGeneration(area, TwoFiles());

        var context = area.CreateContext();
        var deployer = NewDeployer(area);
        var plan = deployer.Plan(context);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, plan.Value!).Ok);

        // 模拟游戏启动后 Ren'Py 生成的编译缓存，回滚时不应留下。
        area.GameFile("game/a11y_platform/00_contract.rpyc", "compiled");

        var rollback = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);

        Assert.True(rollback.Ok, rollback.Error?.ToString());
        var report = rollback.Value!;
        Assert.True(report.Ok);
        Assert.False(report.AlreadyClean);
        Assert.Single(report.RestoredFiles);
        Assert.Single(report.DeletedFiles);
        Assert.Equal("' old bridge\n", area.GameText("a11y_say.vbs"));
        Assert.False(area.GameExists("game/a11y_platform/00_contract.rpy"));
        Assert.False(area.GameExists("game/a11y_platform/00_contract.rpyc"));
        Assert.False(Directory.Exists(Path.Combine(area.GameDirectory, "game", "a11y_platform")));
        Assert.Contains("game/a11y_platform", report.CleanedDirectories);
    }

    [Fact]
    public void Rollback_MarksLedgerRolledBack_SoReinstallNeedsNoForce()
    {
        // 0.2.47：回滚成功后账本标记 rolledBack；补丁其实已经卸载，再装一次不该再要求 --force。
        using var area = new TempArea();
        area.GameFile("a11y_say.vbs", "' old bridge\n");
        WriteGeneration(area, TwoFiles());

        var context = area.CreateContext();
        var deployer = NewDeployer(area);
        var plan = deployer.Plan(context);
        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.True(deployer.DeployWithPlan(context, plan.Value!).Ok);
        var manifestPath = plan.Value!.ManifestPath;

        var rollback = deployer.RollbackFromFile(context, manifestPath);
        Assert.True(rollback.Ok, rollback.Error?.ToString());
        Assert.True(rollback.Value!.Ok);

        var workspace = area.CreateWorkspace();
        Assert.True(InstallManifestStore.IsRolledBack(workspace, manifestPath));
        var reloaded = InstallManifestStore.Load(workspace, manifestPath);
        Assert.True(reloaded.Ok, reloaded.Error?.ToString());
        Assert.True(reloaded.Value!.RolledBack);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.Value!.RolledBackAt));

        // 第二次安装：不加 --force 也应该放行，并说明“上一批已回滚”。
        var second = NewDeployer(area).Plan(context);
        Assert.True(second.Ok, second.Error?.ToString());
        Assert.Contains(second.Value!.Warnings, warning => warning.Contains("已回滚"));

        var secondDeploy = NewDeployer(area).DeployWithPlan(context, second.Value!);
        Assert.True(secondDeploy.Ok, secondDeploy.Error?.ToString());
        Assert.True(File.Exists(Path.Combine(secondDeploy.Value!.BackupDirectory, "previous-install-manifest.json")));
        Assert.True(area.GameExists("game/a11y_platform/00_contract.rpy"));
        Assert.Equal("' generated speech bridge\n", area.GameText("a11y_say.vbs"));
    }

    [Fact]
    public void Rollback_RefusesChangedTargetWithoutForce()
    {
        using var area = new TempArea();
        area.GameFile("a11y_say.vbs", "' old bridge\n");
        WriteGeneration(area, TwoFiles());

        var context = area.CreateContext();
        var deployer = NewDeployer(area);
        var plan = deployer.Plan(context);
        Assert.True(deployer.DeployWithPlan(context, plan.Value!).Ok);

        // 目标被玩家/其他补丁改过。
        area.GameFile("game/a11y_platform/00_contract.rpy", "hand edited\n");

        var refused = deployer.RollbackFromFile(context, plan.Value!.ManifestPath);
        Assert.False(refused.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, refused.Error!.Code);
        Assert.True(area.GameExists("game/a11y_platform/00_contract.rpy"));

        var forced = deployer.RollbackFromFile(
            context,
            plan.Value!.ManifestPath,
            new RollbackOptions { Force = true });
        Assert.True(forced.Ok, forced.Error?.ToString());
    }

    [Fact]
    public void Plan_RejectsPathOutsideGameDirectory()
    {
        using var area = new TempArea();
        WriteGeneration(area, new[]
        {
            ("../escape.rpy", "outside\n"),
        });

        var plan = NewDeployer(area).Plan(area.CreateContext());

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, plan.Error!.Code);
        Assert.False(File.Exists(Path.Combine(area.GameDirectory, "..", "escape.rpy")));
    }

    [Fact]
    public void Plan_RejectsGeneratedFileChangedAfterManifest()
    {
        using var area = new TempArea();
        WriteGeneration(area, TwoFiles());
        // 同长度改写：先过大小校验，确保命中哈希校验。
        File.WriteAllText(
            Path.Combine(GenerationDirectory(area), "a11y_say.vbs"),
            "' tampered speech bridge \n",
            new UTF8Encoding(false));

        var plan = NewDeployer(area).Plan(area.CreateContext());

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, plan.Error!.Code);
        Assert.Contains("哈希", plan.Error.Message);
    }

    [Fact]
    public void Plan_RejectsDeployPlanThatDisagreesWithManifest()
    {
        using var area = new TempArea();
        WriteGeneration(area, TwoFiles(), planTargets: new[] { "a11y_say.vbs" });

        var plan = NewDeployer(area).Plan(area.CreateContext());

        Assert.False(plan.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, plan.Error!.Code);
        Assert.Contains("不一致", plan.Error.Message);
    }

    [Fact]
    public void Deploy_RequiresForceWhenLedgerAlreadyExists()
    {
        using var area = new TempArea();
        WriteGeneration(area, TwoFiles());

        var context = area.CreateContext();
        var first = NewDeployer(area, deploymentId: "dep-one").Plan(context);
        Assert.True(first.Ok, first.Error?.ToString());
        Assert.True(NewDeployer(area, deploymentId: "dep-one").DeployWithPlan(context, first.Value!).Ok);

        var blocked = NewDeployer(area, deploymentId: "dep-two").Plan(context);
        Assert.False(blocked.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, blocked.Error!.Code);
        Assert.Contains("账本", blocked.Error.Message);

        var forced = NewDeployer(area, allowRedeploy: true, deploymentId: "dep-two").Plan(context);
        Assert.True(forced.Ok, forced.Error?.ToString());
        var deploy = NewDeployer(area, allowRedeploy: true, deploymentId: "dep-two").DeployWithPlan(context, forced.Value!);
        Assert.True(deploy.Ok, deploy.Error?.ToString());
        Assert.True(File.Exists(Path.Combine(forced.Value!.BackupDirectory, "previous-install-manifest.json")));
    }

    [Fact]
    public void Deploy_WithoutDeployPlanUsesManifestAndWarns()
    {
        using var area = new TempArea();
        WriteGeneration(area, TwoFiles(), writePlan: false);

        var plan = NewDeployer(area).Plan(area.CreateContext());

        Assert.True(plan.Ok, plan.Error?.ToString());
        Assert.Contains(plan.Value!.Warnings, warning => warning.Contains("deploy_plan.json"));
    }
}
