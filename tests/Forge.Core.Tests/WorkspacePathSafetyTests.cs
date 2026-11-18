using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Core.Tests;

public sealed class WorkspacePathSafetyTests
{
    [Fact]
    public void EnsureCreated_CreatesLayoutDirectories()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);

        var result = workspace.EnsureCreated();

        Assert.True(result.Ok);
        Assert.True(Directory.Exists(workspace.GamesRoot));
        Assert.True(Directory.Exists(workspace.WorkRoot));
        Assert.True(Directory.Exists(workspace.BackupRoot));
        Assert.True(Directory.Exists(workspace.ReportsRoot));
        Assert.True(Directory.Exists(workspace.LogsRoot));
    }

    [Fact]
    public void EnsureGameLayout_CreatesAllStageDirectories()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);
        Assert.True(workspace.EnsureCreated().Ok);

        var result = workspace.EnsureGameLayout("sample-game");

        Assert.True(result.Ok);
        foreach (var stage in Enum.GetValues<PipelineStage>())
        {
            Assert.True(Directory.Exists(workspace.StageDirectory("sample-game", stage)));
        }

        Assert.EndsWith(Path.Combine("games", "sample-game", "06-verify"), workspace.StageDirectory("sample-game", PipelineStage.Verify));
        Assert.True(Directory.Exists(workspace.GameLogsDirectory("sample-game")));
    }

    [Fact]
    public void ResolveWritePath_RejectsParentEscape()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);

        var ex = Assert.Throws<ForgePathException>(() => workspace.ResolveWritePath("../outside.txt"));
        Assert.Equal("E_PATH_UNSAFE", ex.Error.Code);
    }

    [Fact]
    public void ResolveWritePath_RejectsAbsoluteAndDrivePaths()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);

        Assert.Throws<ForgePathException>(() => workspace.ResolveWritePath(Path.Combine(temp.Root, "abs.txt")));
        Assert.Throws<ForgePathException>(() => PathSafety.ValidateRelative("C:/Windows/System32/evil.dll"));
        Assert.Throws<ForgePathException>(() => PathSafety.ValidateRelative("C:\\Windows\\evil.dll"));
    }

    [Fact]
    public void ResolveWritePath_AllowsSafeRelativePath()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);

        var resolved = workspace.ResolveWritePath("reports/sample/report.json");

        Assert.StartsWith(workspace.RootPath, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("reports", "sample", "report.json"), resolved);
    }

    [Fact]
    public void EnsureWriteAllowed_RejectsGameDirectoryWrites()
    {
        using var temp = new TempFolder();
        var gameDirectory = temp.Sub("Game");
        var workspace = new ForgeWorkspace(temp.Sub("Workspace"), gameDirectory);

        var ex = Assert.Throws<ForgePathException>(() =>
            workspace.EnsureWriteAllowed(Path.Combine(gameDirectory, "a11y.dll")));

        Assert.Equal("E_GAME_WRITE_DENIED", ex.Error.Code);
    }

    [Fact]
    public void Constructor_RejectsWorkspaceInsideGameDirectory()
    {
        using var temp = new TempFolder();
        var gameDirectory = temp.Sub("Game");
        var workspaceInsideGame = Path.Combine(gameDirectory, "workspace");

        var ex = Assert.Throws<ForgePathException>(() => new ForgeWorkspace(workspaceInsideGame, gameDirectory));
        Assert.Equal("E_GAME_WRITE_DENIED", ex.Error.Code);
    }

    [Fact]
    public void EnsureWriteAllowed_RejectsOutsideWorkspace()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Sub("Workspace"));

        var ex = Assert.Throws<ForgePathException>(() => workspace.EnsureWriteAllowed(temp.File("outside.txt", "x")));
        Assert.Equal("E_PATH_UNSAFE", ex.Error.Code);
    }

    [Fact]
    public void GameRoot_RejectsUnsafeSlug()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);

        Assert.Throws<ForgePathException>(() => workspace.GameRoot("../escape"));
        Assert.Throws<ForgePathException>(() => workspace.GameRoot("UPPER"));
        Assert.Throws<ForgePathException>(() => workspace.GameRoot("has space"));
    }

    [Fact]
    public void EnsureBackupPath_RejectsPathOutsideBackupRoot()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);

        var ex = Assert.Throws<ForgePathException>(() => workspace.EnsureBackupPath(Path.Combine(temp.Root, "logs", "x")));
        Assert.Equal("E_PATH_UNSAFE", ex.Error.Code);
    }

    [Fact]
    public void DefaultRoot_UsesLocalAppDataProductFolder()
    {
        Assert.EndsWith("GalA11yForge", ForgeWorkspace.DefaultRootPath);
        Assert.Contains("GalA11yForge", ForgeWorkspace.DefaultRootPath);
    }

    [Fact]
    public void SanitizeSlug_ProducesSafeSlug()
    {
        Assert.Equal("sample-game-2", PathSafety.SanitizeSlug("Sample Game 2"));
        Assert.Equal("game", PathSafety.SanitizeSlug(".."));
        Assert.True(PathSafety.IsValidSlug("sample_game-1.0"));
        Assert.False(PathSafety.IsValidSlug("a/b"));
    }
}
