using GalA11yForge.Build;

namespace GalA11yForge.Build.Tests;

/// <summary>
/// BepInExLocator 的搜索根行为：工作区找不到时，应能退到 forge.exe 同目录的 bepinex\（发行包内置离线包）。
/// </summary>
public sealed class BepInExLocatorTests
{
    [Fact]
    public void FindCandidates_MultiRoot_FindsBundledPackageUnderSecondRoot()
    {
        using var area = new TempArea();
        var appDirectory = Path.Combine(area.Root, "app");
        var bepinexDirectory = Path.Combine(appDirectory, "bepinex");
        Directory.CreateDirectory(bepinexDirectory);
        var bundled = Path.Combine(bepinexDirectory, BepInExLocator.ExactFileName);
        File.WriteAllText(bundled, "fake zip for locator test");

        var found = BepInExLocator.FindCandidates(new[] { area.WorkspaceRoot, appDirectory });

        Assert.True(found.Ok);
        Assert.NotNull(found.Value);
        Assert.Single(found.Value!);
        Assert.Equal(Path.GetFullPath(bundled), found.Value![0]);
    }

    [Fact]
    public void FindCandidates_WorkspacePackage_BeatsBundledPackage()
    {
        using var area = new TempArea();
        // 工作区里更"浅"、更新的一份，应当排在前面。
        var workspaceZip = area.WorkspaceFile("cache/BepInEx_win_x64_5.4.23.5.zip", "workspace copy");
        var appDirectory = Path.Combine(area.Root, "app", "bepinex");
        Directory.CreateDirectory(appDirectory);
        var bundled = Path.Combine(appDirectory, BepInExLocator.ExactFileName);
        File.WriteAllText(bundled, "bundled copy");
        File.SetLastWriteTimeUtc(workspaceZip, DateTime.UtcNow.AddMinutes(5));

        var found = BepInExLocator.FindCandidates(new[] { area.WorkspaceRoot, Path.GetDirectoryName(appDirectory)! });

        Assert.True(found.Ok);
        Assert.NotNull(found.Value);
        Assert.Equal(2, found.Value!.Count);
        Assert.Equal(Path.GetFullPath(workspaceZip), found.Value![0]);
    }

    [Fact]
    public void FindCandidates_ExplicitPath_IsTheOnlyResult()
    {
        using var area = new TempArea();
        var explicitZip = Path.Combine(area.Root, "my-bepinex.zip");
        File.WriteAllText(explicitZip, "explicit");
        var bundledDirectory = Path.Combine(area.Root, "app", "bepinex");
        Directory.CreateDirectory(bundledDirectory);
        File.WriteAllText(Path.Combine(bundledDirectory, BepInExLocator.ExactFileName), "bundled");

        var found = BepInExLocator.FindCandidates(new[] { area.WorkspaceRoot }, explicitZip);

        Assert.True(found.Ok);
        Assert.NotNull(found.Value);
        Assert.Single(found.Value!);
        Assert.Equal(Path.GetFullPath(explicitZip), found.Value![0]);
    }

    [Fact]
    public void FindCandidates_NoPackageAnywhere_ReturnsEmptySuccess()
    {
        using var area = new TempArea();
        var appDirectory = Path.Combine(area.Root, "app");
        Directory.CreateDirectory(appDirectory);

        var found = BepInExLocator.FindCandidates(new[] { area.WorkspaceRoot, appDirectory });

        Assert.True(found.Ok);
        Assert.NotNull(found.Value);
        Assert.Empty(found.Value!);
    }

    [Fact]
    public void FindCandidates_ExplicitPathMissing_ReturnsDependencyMissing()
    {
        using var area = new TempArea();
        var missing = Path.Combine(area.Root, "nope.zip");

        var found = BepInExLocator.FindCandidates(area.WorkspaceRoot, missing);

        Assert.False(found.Ok);
        Assert.NotNull(found.Error);
        Assert.Equal("E_DEPENDENCY_MISSING", found.Error!.Code);
    }

    [Fact]
    public void FindBepInEx6Candidates_MultiRoot_FindsBundledPackageUnderSecondRoot()
    {
        using var area = new TempArea();
        var appDirectory = Path.Combine(area.Root, "app");
        var bundledDirectory = Path.Combine(appDirectory, "bepinex6");
        Directory.CreateDirectory(bundledDirectory);
        var bundled = Path.Combine(bundledDirectory, BepInEx6Locator.ExactFileName);
        File.WriteAllText(bundled, "fake bep6 zip for locator test");

        var found = BepInEx6Locator.FindCandidates(new[] { area.WorkspaceRoot, appDirectory });

        Assert.True(found.Ok);
        Assert.NotNull(found.Value);
        Assert.Single(found.Value!);
        Assert.Equal(Path.GetFullPath(bundled), found.Value![0]);
    }

    [Fact]
    public void FindBepInEx6Candidates_ExactVersion_BeatsOtherSixZipInWorkspace()
    {
        using var area = new TempArea();
        var other = area.WorkspaceFile("cache/BepInEx_win_x64_6.0.0-be.785.zip", "older bep6");
        var appDirectory = Path.Combine(area.Root, "app");
        var bundledDirectory = Path.Combine(appDirectory, "bepinex6");
        Directory.CreateDirectory(bundledDirectory);
        var exact = Path.Combine(bundledDirectory, BepInEx6Locator.ExactFileName);
        File.WriteAllText(exact, "exact bep6");
        // 即便工作区那份更新，固定版本（be.788）也必须排第一，部署器才能通过哈希校验。
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddMinutes(5));

        var found = BepInEx6Locator.FindCandidates(new[] { area.WorkspaceRoot, appDirectory });

        Assert.True(found.Ok);
        Assert.NotNull(found.Value);
        Assert.Equal(2, found.Value!.Count);
        Assert.Equal(Path.GetFullPath(exact), found.Value![0]);
    }
}
