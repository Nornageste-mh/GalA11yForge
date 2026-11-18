using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>
/// forge.exe 定位：顺序必须可解释，结果区会把「来源」和「试过的路径」一起显示，
/// 用户找不到 exe 时能自己判断是哪一步出了偏差。
/// </summary>
public sealed class LocatorTests
{
    private sealed class FakeFileSystem
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ForgeExeSearchOptions Options(
            string? explicitPath = null,
            string? environmentValue = null,
            string? baseDirectory = null,
            string? currentDirectory = null,
            string? pathVariable = null,
            int maxParentHops = 8)
            => new()
            {
                ExplicitPath = explicitPath,
                EnvironmentValue = environmentValue,
                BaseDirectory = baseDirectory,
                CurrentDirectory = currentDirectory,
                PathVariable = pathVariable,
                FileExists = file => Files.Contains(file),
                DirectoryExists = _ => true,
                MaxParentHops = maxParentHops,
            };
    }

    [Fact]
    public void ExplicitPathWins()
    {
        var fs = new FakeFileSystem();
        fs.Files.Add(@"C:\tools\forge.exe");

        var resolution = ForgeExeLocator.Resolve(fs.Options(@"C:\tools\forge.exe", baseDirectory: @"C:\app"));

        Assert.True(resolution.Found);
        Assert.Equal(@"C:\tools\forge.exe", resolution.Path);
        Assert.Equal("界面指定", resolution.Source);
    }

    [Fact]
    public void ExplicitPathWithoutQuotesIsCleaned()
    {
        var fs = new FakeFileSystem();
        fs.Files.Add(@"C:\tools\forge.exe");

        var resolution = ForgeExeLocator.Resolve(fs.Options("\"C:\\tools\\forge.exe\""));

        Assert.Equal(@"C:\tools\forge.exe", resolution.Path);
    }

    [Fact]
    public void EnvironmentVariableIsSecondChoice()
    {
        var fs = new FakeFileSystem();
        fs.Files.Add(@"C:\env\forge.exe");

        var resolution = ForgeExeLocator.Resolve(fs.Options(
            explicitPath: @"C:\missing\forge.exe",
            environmentValue: @"C:\env\forge.exe"));

        Assert.Equal("环境变量 FORGE_EXE", resolution.Source);
        Assert.Equal(@"C:\env\forge.exe", resolution.Path);
    }

    [Fact]
    public void SameFolderAsGuiIsThirdChoice()
    {
        var fs = new FakeFileSystem();
        var exe = Path.Combine(@"C:\app", ForgeExeLocator.ExeFileName);
        fs.Files.Add(exe);

        var resolution = ForgeExeLocator.Resolve(fs.Options(baseDirectory: @"C:\app"));

        Assert.Equal("与 GUI 同目录", resolution.Source);
        Assert.Equal(Path.GetFullPath(exe), resolution.Path);
    }

    [Fact]
    public void ReleasePackage_SiblingAppFolder_IsFound()
    {
        var fs = new FakeFileSystem();
        var expected = Path.GetFullPath(Path.Combine(@"C:\pkg\gui", ForgeExeLocator.ReleaseAppRelativePath));
        fs.Files.Add(expected);

        var resolution = ForgeExeLocator.Resolve(fs.Options(baseDirectory: @"C:\pkg\gui"));

        Assert.Equal("发布包 app 目录", resolution.Source);
        Assert.Equal(expected, resolution.Path);
    }

    [Fact]
    public void FlattenedLayout_ParentFolderForgeExe_IsFound()
    {
        var fs = new FakeFileSystem();
        var expected = Path.GetFullPath(Path.Combine(@"C:\pkg\gui", ForgeExeLocator.ParentFolderRelativePath));
        fs.Files.Add(expected);

        var resolution = ForgeExeLocator.Resolve(fs.Options(baseDirectory: @"C:\pkg\gui"));

        Assert.Equal("上一级目录", resolution.Source);
        Assert.Equal(expected, resolution.Path);
    }

    [Fact]
    public void WalkUpFromGuiDirectoryFindsRepoReleaseBuild()
    {
        var fs = new FakeFileSystem();
        var repoRoot = @"C:\repo";
        var expected = Path.Combine(repoRoot, ForgeExeLocator.ReleaseRelativePath);
        fs.Files.Add(expected);

        var resolution = ForgeExeLocator.Resolve(
            fs.Options(baseDirectory: @"C:\repo\src\Forge.Gui\bin\Release\net8.0-windows"));

        Assert.Equal("从 GUI 目录向上查找", resolution.Source);
        Assert.Equal(Path.GetFullPath(expected), resolution.Path);
        Assert.Contains(resolution.TriedPaths, tried => tried.Contains("从 GUI 目录向上查找"));
    }

    [Fact]
    public void DebugBuildIsUsedWhenReleaseIsMissing()
    {
        var fs = new FakeFileSystem();
        var expected = Path.Combine(@"C:\repo", ForgeExeLocator.DebugRelativePath);
        fs.Files.Add(expected);

        var resolution = ForgeExeLocator.Resolve(
            fs.Options(baseDirectory: @"C:\repo\src\Forge.Gui\bin\Debug\net8.0-windows"));

        Assert.True(resolution.Found);
        Assert.Equal(Path.GetFullPath(expected), resolution.Path);
    }

    [Fact]
    public void WalkUpRespectsMaxParentHops()
    {
        var fs = new FakeFileSystem();
        fs.Files.Add(Path.Combine(@"C:\repo", ForgeExeLocator.ReleaseRelativePath));

        var resolution = ForgeExeLocator.Resolve(fs.Options(
            baseDirectory: @"C:\repo\src\Forge.Gui\bin\Release\net8.0-windows",
            maxParentHops: 1));

        Assert.False(resolution.Found);
    }

    [Fact]
    public void WalkUpFromCurrentDirectoryWorks()
    {
        var fs = new FakeFileSystem();
        var expected = Path.Combine(@"C:\repo", ForgeExeLocator.ReleaseRelativePath);
        fs.Files.Add(expected);

        var resolution = ForgeExeLocator.Resolve(fs.Options(currentDirectory: @"C:\repo\src\Forge.Cli"));

        Assert.Equal("从当前目录向上查找", resolution.Source);
        Assert.Equal(Path.GetFullPath(expected), resolution.Path);
    }

    [Fact]
    public void PathVariableIsLastResort()
    {
        var fs = new FakeFileSystem();
        var expected = Path.Combine(@"C:\bin2", ForgeExeLocator.ExeFileName);
        fs.Files.Add(expected);

        var resolution = ForgeExeLocator.Resolve(fs.Options(pathVariable: @"""C:\bin1"";C:\bin2"));

        Assert.Equal("PATH 环境变量", resolution.Source);
        Assert.Equal(Path.GetFullPath(expected), resolution.Path);
    }

    [Fact]
    public void MissingEverythingReportsTriedPaths()
    {
        var fs = new FakeFileSystem();

        var resolution = ForgeExeLocator.Resolve(fs.Options(
            explicitPath: @"C:\a\forge.exe",
            baseDirectory: @"C:\app",
            pathVariable: @"C:\bin"));

        Assert.False(resolution.Found);
        Assert.Null(resolution.Path);
        Assert.Null(resolution.Source);
        Assert.Equal("没找到 forge.exe", resolution.Summary);
        Assert.NotEmpty(resolution.TriedPaths);
    }

    [Fact]
    public void FoundSummaryShowsPathAndSource()
    {
        var fs = new FakeFileSystem();
        fs.Files.Add(@"C:\tools\forge.exe");

        var resolution = ForgeExeLocator.Resolve(fs.Options(explicitPath: @"C:\tools\forge.exe"));

        Assert.Equal(@"C:\tools\forge.exe（来源：界面指定）", resolution.Summary);
    }

    [Fact]
    public void RelativePathsAreCleanedFromEnvironment()
    {
        var fs = new FakeFileSystem();
        fs.Files.Add(@"C:\env\forge.exe");

        var resolution = ForgeExeLocator.Resolve(fs.Options(environmentValue: "  \"C:\\env\\forge.exe\"  "));

        Assert.True(resolution.Found);
    }
}
