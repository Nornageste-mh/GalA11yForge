using GalA11yForge.Core.Deploy;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Core.Tests;

public sealed class DeployManifestTests
{
    [Fact]
    public void BuildRollbackPlan_ReversesDeploymentOrder()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);
        var backupRoot = workspace.BackupDirectory("dep-test-001");
        Directory.CreateDirectory(backupRoot);
        var manifest = TestData.DeployManifest(backupRoot);

        var result = DeploymentLedger.BuildRollbackPlan(workspace, manifest);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Count);
        Assert.Equal(1, result.Value[0].Order);
        Assert.Equal(DeployRollbackAction.Restore, result.Value[0].Action);
        Assert.Equal("doorstop_config.ini", result.Value[0].TargetRelativePath);
        Assert.Equal(2, result.Value[1].Order);
        Assert.Equal(DeployRollbackAction.Delete, result.Value[1].Action);
        Assert.Equal("BepInEx/plugins/A11yPlugin.dll", result.Value[1].TargetRelativePath);
    }

    [Fact]
    public void BuildRollbackPlan_RequiresBackupForReplacedFile()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);
        var backupRoot = workspace.BackupDirectory("dep-test-001");
        Directory.CreateDirectory(backupRoot);
        var manifest = TestData.DeployManifest(backupRoot);
        manifest.Files[1].BackupPath = null;

        var result = DeploymentLedger.BuildRollbackPlan(workspace, manifest);

        Assert.False(result.Ok);
        Assert.Equal("E_ARTIFACT_MISSING", result.Error!.Code);
    }

    [Fact]
    public void BuildRollbackPlan_RejectsBackupOutsideBackupRoot()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);
        var manifest = TestData.DeployManifest(Path.Combine(temp.Root, "backup"));
        manifest.Files[1].BackupPath = Path.Combine(temp.Root, "logs", "stolen.ini");

        var result = DeploymentLedger.BuildRollbackPlan(workspace, manifest);

        Assert.False(result.Ok);
        Assert.Equal("E_PATH_UNSAFE", result.Error!.Code);
    }

    [Fact]
    public void BuildRollbackPlan_RejectsEscapingRelativePath()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);
        var manifest = TestData.DeployManifest(Path.Combine(temp.Root, "backup"));
        manifest.Files[0].RelativePath = "../outside.dll";

        var result = DeploymentLedger.BuildRollbackPlan(workspace, manifest);

        Assert.False(result.Ok);
        Assert.Equal("E_ARTIFACT_INVALID", result.Error!.Code);
    }

    [Fact]
    public void BuildRollbackPlan_EmptyManifest_IsClean()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);
        var manifest = new DeployManifest { BackupRoot = workspace.BackupRoot };

        var result = DeploymentLedger.BuildRollbackPlan(workspace, manifest);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!);
    }
}
