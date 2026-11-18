using System.Text;
using UnityMonoAnalyzer;
using UnityMonoAnalyzer.Discovery;
using Xunit;

namespace UnityMonoAnalyzer.Tests;

/// <summary>入口 exe 选择、Unity 版本探测、`*_Data` 发现（全部在临时目录里造假）。</summary>
public class DiscoveryTests
{
    [Theory]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("UnityCrashHandler32.exe", true)]
    [InlineData("Windose_BurstDebugInformation_DoNotShip.exe", true)]
    [InlineData("UnityPlayer.exe", true)]
    [InlineData("Windose.exe", false)]
    [InlineData("The seven days i spent with you.exe", false)]
    public void IsAlwaysExcluded_FiltersCrashHandlerAndBuildArtifacts(string fileName, bool excluded)
    {
        Assert.Equal(excluded, EntrySelector.IsAlwaysExcluded(fileName));
    }

    [Fact]
    public void Pick_PrefersExeMatchingDataDir_EvenWhenCrashHandlerIsBigger()
    {
        using var tmp = new TempDir("pick-same");
        tmp.Bytes("MyGame.exe", new byte[100]);
        tmp.Bytes("UnityCrashHandler64.exe", new byte[50_000]);
        tmp.Dir("MyGame_Data");

        var choice = EntrySelector.Pick(tmp.Path, "MyGame_Data");

        Assert.False(choice.Excluded);
        Assert.Equal("MyGame.exe", Path.GetFileName(choice.Path));
        Assert.Contains("同名", choice.Reason);
    }

    [Fact]
    public void Pick_NeverReturnsCrashHandler_EvenIfItIsTheBiggest()
    {
        using var tmp = new TempDir("pick-crash");
        tmp.Bytes("Windose.exe", new byte[200]);
        tmp.Bytes("UnityCrashHandler64.exe", new byte[80_000]);

        var choice = EntrySelector.Pick(tmp.Path, "Windose_Data");

        Assert.Equal("Windose.exe", Path.GetFileName(choice.Path));
    }

    [Fact]
    public void Pick_FallsBackToLargestUsableExe_WhenNameDoesNotMatch()
    {
        using var tmp = new TempDir("pick-fallback");
        tmp.Bytes("foo.exe", new byte[100]);
        tmp.Bytes("bar.exe", new byte[900]);

        var choice = EntrySelector.Pick(tmp.Path, "5omeday_Data");

        Assert.Equal("bar.exe", Path.GetFileName(choice.Path));
        Assert.Contains("兜底", choice.Reason);
    }

    [Fact]
    public void Pick_ReportsExcluded_WhenOnlyUnusableExesExist()
    {
        using var tmp = new TempDir("pick-none");
        tmp.Bytes("UnityCrashHandler64.exe", new byte[100]);

        var choice = EntrySelector.Pick(tmp.Path, "Whatever_Data");

        Assert.True(choice.Excluded);
        Assert.Null(choice.Path);
    }

    [Fact]
    public void DataDirForExe_ReplacesExeNameWithDataSuffix()
    {
        var data = EntrySelector.DataDirForExe(@"C:\games\needy\Windose.exe");

        Assert.Equal("Windose_Data", Path.GetFileName(data));
    }

    [Fact]
    public void Detect_ReadsUnityVersionFromGlobalGameManagers()
    {
        using var tmp = new TempDir("version");
        var dataDir = tmp.Dir("X_Data");
        // 模拟 globalgamemanagers 头部：前若干字节是二进制元数据，版本串在 0x33 附近
        var header = new byte[0x60];
        Encoding.ASCII.GetBytes("2021.3.31f1").CopyTo(header, 0x33);
        tmp.Bytes(@"X_Data\globalgamemanagers", header);

        var detected = UnityVersionDetector.Detect(dataDir, tmp.Path);

        Assert.Equal("2021.3.31f1", detected.Version);
        Assert.Equal("globalgamemanagers", detected.Source);
        Assert.EndsWith("globalgamemanagers", detected.EvidencePath);
    }

    [Fact]
    public void Detect_ReturnsEmptyAndUnknown_WhenNoEvidenceAtAll()
    {
        using var tmp = new TempDir("version-none");
        var dataDir = tmp.Dir("X_Data");

        var detected = UnityVersionDetector.Detect(dataDir, tmp.Path);

        Assert.Equal("", detected.Version);
        Assert.Equal("unknown", detected.Source);
    }

    [Fact]
    public void Find_DiscoversNestedManagedAssembly()
    {
        using var tmp = new TempDir("find-nested");
        tmp.Bytes(@"game\TheGame_Data\Managed\Assembly-CSharp.dll", new byte[16]);
        tmp.File("Readme.txt", "not a game");

        var found = GameDiscovery.Find(tmp.Path);

        var c = Assert.Single(found);
        Assert.Equal("TheGame_Data", c.DataDirName);
        Assert.EndsWith(@"Managed\Assembly-CSharp.dll", c.AssemblyPath);
        Assert.True(c.Depth >= 1, $"嵌套一层的深度应 >=1，实际 {c.Depth}");
    }

    [Fact]
    public void Find_SkipsToolingDirectories()
    {
        using var tmp = new TempDir("find-skip");
        tmp.Bytes(@"BepInEx\Dummy_Data\Managed\Assembly-CSharp.dll", new byte[16]);
        tmp.Bytes(@"dotnet\Shared_Data\Managed\Assembly-CSharp.dll", new byte[16]);

        Assert.Empty(GameDiscovery.Find(tmp.Path));
    }

    [Fact]
    public void Find_AcceptsDataDirectoryItself()
    {
        using var tmp = new TempDir("find-data");
        tmp.Bytes(@"X_Data\Managed\Assembly-CSharp.dll", new byte[16]);

        var found = GameDiscovery.Find(Path.Combine(tmp.Path, "X_Data"));

        Assert.Single(found);
    }

    [Fact]
    public void DetectDeployed_SeesBepInExAndDoorstop()
    {
        using var tmp = new TempDir("deployed");
        tmp.Dir("BepInEx", "plugins");
        tmp.File("doorstop_config.ini", "[General]");
        tmp.Bytes("winhttp.dll", new byte[8]);

        var state = GameDiscovery.DetectDeployed(tmp.Path);

        Assert.True(state.Deployed);
        Assert.NotEmpty(state.Evidence);
    }

    [Fact]
    public void DetectDeployed_IsQuietOnCleanInstall()
    {
        using var tmp = new TempDir("clean");
        tmp.Bytes("Game.exe", new byte[8]);

        Assert.False(GameDiscovery.DetectDeployed(tmp.Path).Deployed);
    }
}
