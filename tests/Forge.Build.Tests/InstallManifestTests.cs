using GalA11yForge.Core.Results;

namespace GalA11yForge.Build.Tests;

public sealed class InstallManifestTests
{
    [Fact]
    public void Validate_ValidAddManifest_Succeeds()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        var manifest = TestArtifacts.Manifest(area, backup, TestArtifacts.AddEntry("BepInEx/plugins/A11yPlugin.dll"));

        var result = InstallManifestValidation.Validate(workspace, manifest, requireBackupFiles: false);

        Assert.True(result.Ok, result.Error?.ToString());
    }

    [Fact]
    public void Validate_ValidReplaceWithBackup_Succeeds()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "doorstop_config.ini"), "old");
        var manifest = TestArtifacts.Manifest(area, backup,
            TestArtifacts.ReplaceEntry("doorstop_config.ini", "doorstop_config.ini"));

        var result = InstallManifestValidation.Validate(workspace, manifest, requireHashes: false);

        Assert.True(result.Ok, result.Error?.ToString());
    }

    [Fact]
    public void Validate_PathEscape_IsRejected()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        var manifest = TestArtifacts.Manifest(area, backup, TestArtifacts.AddEntry("../outside.dll"));

        var result = InstallManifestValidation.Validate(workspace, manifest, requireBackupFiles: false);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }

    [Fact]
    public void Validate_BackupRootOutsideWorkspace_IsRejected()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var manifest = TestArtifacts.Manifest(area, Path.Combine(area.Root, "not-in-workspace"),
            TestArtifacts.AddEntry("file.dll"));

        var result = InstallManifestValidation.Validate(workspace, manifest, requireBackupFiles: false);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }

    [Fact]
    public void Validate_MissingBackupFile_IsRejected()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        Directory.CreateDirectory(backup);
        var manifest = TestArtifacts.Manifest(area, backup,
            TestArtifacts.ReplaceEntry("doorstop_config.ini", "doorstop_config.ini"));

        var result = InstallManifestValidation.Validate(workspace, manifest);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactMissing, result.Error!.Code);
    }

    [Fact]
    public void Validate_DuplicatePathIgnoringCase_IsRejected()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        var manifest = TestArtifacts.Manifest(area, backup,
            TestArtifacts.AddEntry("BepInEx/plugins/A11yPlugin.dll"),
            TestArtifacts.AddEntry("bepinex/PLUGINS/a11yplugin.dll"));

        var result = InstallManifestValidation.Validate(workspace, manifest, requireBackupFiles: false);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, result.Error!.Code);
        Assert.Contains("重复", result.Error.Message);
    }

    [Fact]
    public void Validate_AddWithExistedBefore_IsRejected()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        var entry = TestArtifacts.AddEntry("file.dll");
        entry.ExistedBefore = true;
        var manifest = TestArtifacts.Manifest(area, backup, entry);

        var result = InstallManifestValidation.Validate(workspace, manifest, requireBackupFiles: false);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, result.Error!.Code);
    }

    [Fact]
    public void Validate_BackupPathEscape_IsRejected()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        Directory.CreateDirectory(backup);
        var manifest = TestArtifacts.Manifest(area, backup,
            TestArtifacts.ReplaceEntry("doorstop_config.ini", "../outside.ini"));

        var result = InstallManifestValidation.Validate(workspace, manifest);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsWithoutBom()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        var manifest = TestArtifacts.Manifest(area, backup, TestArtifacts.AddEntry("BepInEx/plugins/A11yPlugin.dll"));

        var saved = InstallManifestStore.Save(workspace, manifest);
        Assert.True(saved.Ok, saved.Error?.ToString());
        Assert.Equal(InstallManifestStore.DefaultPath(workspace, "sample-game"), saved.Value);
        Assert.False(InstallManifestStore.HasBom(saved.Value!));

        var loaded = InstallManifestStore.Load(workspace, saved.Value!);
        Assert.True(loaded.Ok, loaded.Error?.ToString());
        Assert.Equal("dep-test-123", loaded.Value!.DeploymentId);
        Assert.Single(loaded.Value.Files);
    }

    [Fact]
    public void Load_ManifestOutsideWorkspace_IsRejected()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var outside = Path.Combine(area.Root, "outside-manifest.json");
        File.WriteAllText(outside, "{}");

        var loaded = InstallManifestStore.Load(workspace, outside);

        Assert.False(loaded.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, loaded.Error!.Code);
    }

    [Fact]
    public void Save_InvalidManifest_IsRejectedWithoutWriting()
    {
        using var area = new TempArea();
        var workspace = area.CreateWorkspace();
        var backup = Path.Combine(workspace.BackupRoot, "dep-test-123");
        var manifest = TestArtifacts.Manifest(area, backup, TestArtifacts.AddEntry("../evil.dll"));

        var saved = InstallManifestStore.Save(workspace, manifest);

        Assert.False(saved.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, saved.Error!.Code);
        Assert.False(File.Exists(InstallManifestStore.DefaultPath(workspace, "sample-game")));
    }
}
