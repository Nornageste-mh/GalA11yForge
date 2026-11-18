using System.IO.Compression;
using System.Text;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Build.Tests;

/// <summary>
/// IL2CPP 安装链（BepInEx 6 + Il2CppInterop）的单测：
/// 包校验、interop 前置检查、安装/账本、重复安装拒绝、回滚与哈希不一致保护。
/// 全部使用临时目录与假包，不触碰真实游戏目录，也不依赖官方 34MB 压缩包。
/// </summary>
public sealed class Il2CppDeploymentTests
{
    private const string PluginFileName = "A11yPlugin.dll";

    private static string CreateBepInEx6Package(
        string root,
        bool includeIl2CppCore = true,
        string directoryName = "downloads")
    {
        var directory = Path.Combine(root, directoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, BepInEx6Locator.ExactFileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddEntry(archive, "winhttp.dll", "fake-winhttp");
        AddEntry(archive, "doorstop_config.ini", "fake-doorstop");
        AddEntry(archive, ".doorstop_version", "4.0.0");
        AddEntry(archive, "BepInEx/core/BepInEx.Core.dll", "fake-bepinex-core");
        if (includeIl2CppCore)
        {
            AddEntry(archive, "BepInEx/core/BepInEx.Unity.IL2CPP.dll", "fake-il2cpp");
        }

        AddEntry(archive, "dotnet/coreclr.dll", "fake-coreclr");
        AddEntry(archive, "BepInEx/plugins/keep.txt", "keep");
        return path;
    }

    private static string CreateSpeechRuntime(TempArea area, bool includeNvdaClient = true)
    {
        var directory = Path.Combine(area.Root, "speech");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Tolk.dll"), "fake-tolk", new UTF8Encoding(false));
        if (includeNvdaClient)
        {
            File.WriteAllText(
                Path.Combine(directory, Il2CppPluginDeployer.NvdaClientFileName),
                "fake-nvda-client",
                new UTF8Encoding(false));
        }

        return directory;
    }

    private static DeploymentOptions Options(
        string packagePath,
        string? speechRuntime = null,
        bool allowRedeploy = false,
        bool placeNvdaClientInGameRoot = true)
        => new()
        {
            BepInExPackagePath = packagePath,
            BepInExVersion = BepInEx6Package.Version,
            BepInExSha256 = FileHashes.Sha256File(packagePath),
            VerifyPackageHash = true,
            AllowRedeploy = allowRedeploy,
            SpeechRuntimeDirectory = speechRuntime,
            PlaceNvdaClientInGameRoot = placeNvdaClientInGameRoot,
        };

    private static Il2CppPluginDeployer Deployer(TempArea area, DeploymentOptions options)
        => new(area.CreateWorkspace(), options);

    private static void AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    [Fact]
    public void Package_Open_WithComputedHash_Succeeds_AndReportsMarkers()
    {
        using var area = new TempArea();
        var path = CreateBepInEx6Package(area.Root);

        var opened = BepInEx6Package.Open(path, FileHashes.Sha256File(path));

        Assert.True(opened.Ok, opened.Error?.Message);
        var package = opened.Value!;
        Assert.Equal(path, package.Path);
        Assert.Equal(BepInEx6Package.Version, BepInEx6Package.Version);
        Assert.Contains(package.Entries, entry => entry.RelativePath == "BepInEx/core/BepInEx.Unity.IL2CPP.dll");
        Assert.Contains(package.Entries, entry => entry.RelativePath == "dotnet/coreclr.dll");
        Assert.Equal("fake-winhttp", Encoding.UTF8.GetString(package.ReadBytes("winhttp.dll")));
    }

    [Fact]
    public void Package_Open_WithDefaultPinnedHash_RejectsUnknownArchive()
    {
        using var area = new TempArea();
        var path = CreateBepInEx6Package(area.Root);

        var opened = BepInEx6Package.Open(path);

        Assert.False(opened.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, opened.Error!.Code);
        Assert.Contains("哈希不匹配", opened.Error.Message);
        Assert.Contains(BepInEx6Package.PinnedSha256, opened.Error.Message);
    }

    [Fact]
    public void Package_Open_WithoutIl2CppCore_FailsWithActionableHint()
    {
        using var area = new TempArea();
        var path = CreateBepInEx6Package(area.Root, includeIl2CppCore: false);

        var opened = BepInEx6Package.Open(path, FileHashes.Sha256File(path));

        Assert.False(opened.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, opened.Error!.Code);
        Assert.Contains("BepInEx.Unity.IL2CPP.dll", opened.Error.Message);
        Assert.Contains("BepInEx 5.x", opened.Error.Message);
    }

    [Fact]
    public void Package_Open_MissingFile_ReturnsDependencyMissingWithDownloadUrl()
    {
        using var area = new TempArea();
        var path = Path.Combine(area.Root, "nope", BepInEx6Locator.ExactFileName);

        var opened = BepInEx6Package.Open(path);

        Assert.False(opened.Ok);
        Assert.Equal(ForgeErrorCodes.DependencyMissing, opened.Error!.Code);
        Assert.Contains(BepInEx6Package.OfficialDownloadUrl, opened.Error.Suggestion);
    }

    [Fact]
    public void InteropProbe_EmptyGame_IsNotReady_AndRequireGivesFourSteps()
    {
        using var area = new TempArea();
        area.GameFile("SiliconHeart.exe", "fake");

        var probe = Il2CppInteropProbe.Probe(area.GameDirectory);
        Assert.False(probe.ReadyForBuild);
        Assert.Equal(0, probe.InteropAssemblyCount);
        Assert.Contains(probe.Notes, note => note.Contains("BepInEx 6 还没装进游戏目录"));

        var required = Il2CppInteropProbe.RequireForBuild(area.GameDirectory);
        Assert.False(required.Ok);
        Assert.Equal(ForgeErrorCodes.DependencyMissing, required.Error!.Code);
        Assert.Contains("复制到可写目录", required.Error.Suggestion);
        Assert.Contains("BepInEx\\interop", required.Error.Suggestion);
        Assert.Contains("约 97 个 DLL", required.Error.Suggestion);
    }

    [Fact]
    public void InteropProbe_WithCoreAndInterop_IsReady()
    {
        using var area = new TempArea();
        area.GameFile("BepInEx/core/BepInEx.Unity.IL2CPP.dll", "fake");
        area.GameFile("BepInEx/core/BepInEx.Core.dll", "fake");
        area.GameFile("BepInEx/interop/Il2Cppmscorlib.dll", "fake");
        for (var index = 1; index < Il2CppInteropProbe.MinimumInteropAssemblies; index++)
        {
            area.GameFile($"BepInEx/interop/Assembly{index}.dll", "fake");
        }

        var state = Il2CppInteropProbe.Probe(area.GameDirectory);
        Assert.True(state.ReadyForBuild, state.Describe());
        Assert.Equal(Il2CppInteropProbe.MinimumInteropAssemblies, state.InteropAssemblyCount);
        Assert.True(Il2CppInteropProbe.RequireForBuild(area.GameDirectory).Ok);
    }

    [Fact]
    public void InteropProbe_MissingMscorlib_IsNotReadyAfterEnoughAssemblies()
    {
        using var area = new TempArea();
        area.GameFile("BepInEx/core/BepInEx.Unity.IL2CPP.dll", "fake");
        area.GameFile("BepInEx/core/BepInEx.Core.dll", "fake");
        for (var index = 0; index < Il2CppInteropProbe.MinimumInteropAssemblies; index++)
        {
            area.GameFile($"BepInEx/interop/Assembly{index}.dll", "fake");
        }

        var state = Il2CppInteropProbe.Probe(area.GameDirectory);

        Assert.False(state.ReadyForBuild);
        Assert.Contains(state.Notes, note => note.Contains("Il2Cppmscorlib.dll"));
    }

    [Fact]
    public async Task Deploy_InstallsBepInEx6PluginAndSpeech_AndSavesManifest()
    {
        using var area = new TempArea();
        var package = CreateBepInEx6Package(area.Root);
        var speech = CreateSpeechRuntime(area);
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var deployer = Deployer(area, Options(package, speech));

        var result = await deployer.DeployAsync(area.CreateContext(), build);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.True(area.GameExists("winhttp.dll"));
        Assert.True(area.GameExists("BepInEx/core/BepInEx.Unity.IL2CPP.dll"));
        Assert.True(area.GameExists($"BepInEx/plugins/{PluginFileName}"));
        Assert.True(area.GameExists("BepInEx/plugins/Tolk.dll"));
        Assert.True(area.GameExists(Il2CppPluginDeployer.NvdaClientFileName), "controller 必须在 exe 根（R21 实测）");
        Assert.True(area.GameExists($"BepInEx/plugins/{Il2CppPluginDeployer.NvdaClientFileName}"));

        var manifestPath = InstallManifestStore.DefaultPath(area.CreateWorkspace(), "sample-game");
        Assert.True(File.Exists(manifestPath));
        var loaded = InstallManifestStore.Load(area.CreateWorkspace(), manifestPath);
        Assert.True(loaded.Ok, loaded.Error?.Message);
        var manifest = loaded.Value!;
        Assert.Equal(BepInEx6Package.Version, manifest.BepInExVersion);
        Assert.Equal(BepInEx6Package.PinnedSha256, BepInEx6Package.PinnedSha256);
        var pluginEntry = Assert.Single(manifest.Files, file => file.RelativePath == $"BepInEx/plugins/{PluginFileName}");
        Assert.Equal(DeployFileAction.Add, pluginEntry.Action);
        Assert.Equal("plugin", pluginEntry.Source);
        Assert.Contains(manifest.Files, file => file.RelativePath == "nvdaControllerClient64.dll" && file.Source == "speech");
        Assert.Equal(area.GameDirectory, manifest.GamePath);
        Assert.NotNull(result.Value!.BackupDirectory);
        Assert.True(Directory.Exists(result.Value.BackupDirectory));
    }

    [Fact]
    public async Task Deploy_TwiceWithoutForce_RefusesSecondInstall()
    {
        using var area = new TempArea();
        var package = CreateBepInEx6Package(area.Root);
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var first = await Deployer(area, Options(package)).DeployAsync(area.CreateContext(), build);
        Assert.True(first.Ok, first.Error?.Message);

        var second = await Deployer(area, Options(package)).DeployAsync(area.CreateContext(), build);

        Assert.False(second.Ok);
        Assert.Equal(ForgeErrorCodes.ArgumentInvalid, second.Error!.Code);
        Assert.Contains("已有安装账本", second.Error.Message);
        Assert.Contains("--force", second.Error.Suggestion);
    }

    [Fact]
    public async Task Deploy_PluginOutsideWorkspace_IsRejected()
    {
        using var area = new TempArea();
        var package = CreateBepInEx6Package(area.Root);
        var outside = Path.Combine(area.Root, "outside", PluginFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "fake-plugin", new UTF8Encoding(false));
        var build = TestArtifacts.CreateBuildResult(outside);
        var deployer = Deployer(area, Options(package));

        var result = await deployer.DeployAsync(area.CreateContext(), build);

        Assert.False(result.Ok);
        Assert.Contains(
            result.Error!.Code,
            new[] { ForgeErrorCodes.GameWriteDenied, ForgeErrorCodes.PathUnsafe, ForgeErrorCodes.ArgumentInvalid });
        Assert.False(area.GameExists("winhttp.dll"), "预检失败不应写入任何文件");
    }

    [Fact]
    public async Task Rollback_RemovesAddsAndRestoresPreexistingFiles()
    {
        using var area = new TempArea();
        area.GameFile("doorstop_config.ini", "old-doorstop");
        area.GameFile("BepInEx/plugins/OldA11y.dll", "old-plugin");
        var package = CreateBepInEx6Package(area.Root);
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var context = area.CreateContext();
        var deployer = Deployer(area, Options(package));
        var deployed = await deployer.DeployAsync(context, build);
        Assert.True(deployed.Ok, deployed.Error?.Message);
        Assert.Equal("fake-doorstop", area.GameText("doorstop_config.ini"));

        var manifestPath = InstallManifestStore.DefaultPath(area.CreateWorkspace(), "sample-game");
        var rolledBack = await deployer.RollbackFromFileAsync(
            context,
            manifestPath,
            new RollbackOptions { Force = false },
            CancellationToken.None);

        Assert.True(rolledBack.Ok, rolledBack.Error?.Message + " / " + rolledBack.Error?.ToString());
        var report = rolledBack.Value!;
        Assert.True(report.Ok, string.Join("; ", report.Errors));
        Assert.True(report.RestoredFiles.Count >= 1, $"应还原被替换的 doorstop，实际 {report.RestoredFiles.Count}");
        Assert.Equal("old-doorstop", area.GameText("doorstop_config.ini"));
        Assert.True(area.GameExists("BepInEx/plugins/OldA11y.dll"));
        Assert.False(area.GameExists($"BepInEx/plugins/{PluginFileName}"));
        Assert.False(area.GameExists("winhttp.dll"));
        Assert.False(area.GameExists("BepInEx/core/BepInEx.Unity.IL2CPP.dll"));
        Assert.False(area.GameExists(Il2CppPluginDeployer.NvdaClientFileName));
    }

    [Fact]
    public async Task Rollback_ModifiedPluginWithoutForce_ReportsPartialAndKeepsFile()
    {
        using var area = new TempArea();
        var package = CreateBepInEx6Package(area.Root);
        var plugin = TestArtifacts.CreatePluginArtifact(area.WorkspaceRoot);
        var build = TestArtifacts.CreateBuildResult(plugin);
        var context = area.CreateContext();
        var deployer = Deployer(area, Options(package));
        Assert.True((await deployer.DeployAsync(context, build)).Ok);
        area.GameFile($"BepInEx/plugins/{PluginFileName}", "player-modified");

        var manifestPath = InstallManifestStore.DefaultPath(area.CreateWorkspace(), "sample-game");
        var rolledBack = await deployer.RollbackFromFileAsync(
            context,
            manifestPath,
            new RollbackOptions { Force = false },
            CancellationToken.None);

        Assert.False(rolledBack.Ok);
        Assert.Equal(ForgeErrorCodes.Partial, rolledBack.Error!.Code);
        Assert.True(area.GameExists($"BepInEx/plugins/{PluginFileName}"), "默认不覆盖玩家改动");
        Assert.Equal("player-modified", area.GameText($"BepInEx/plugins/{PluginFileName}"));
    }

    [Fact]
    public void Constructor_WrongBepInExVersion_Throws()
    {
        using var area = new TempArea();
        var options = new DeploymentOptions
        {
            BepInExVersion = "5.4.23.5",
            BepInExSha256 = BepInEx6Package.PinnedSha256,
        };

        var exception = Assert.Throws<ArgumentException>(() => new Il2CppPluginDeployer(area.CreateWorkspace(), options));
        Assert.Contains(BepInEx6Package.Version, exception.Message);
    }

    [Fact]
    public void Locator_FindsOnlyBepInEx6Archives()
    {
        using var area = new TempArea();
        var package = CreateBepInEx6Package(area.Root);
        File.WriteAllText(Path.Combine(area.Root, "downloads", "BepInEx_win_x64_5.4.23.5.zip"), "x");
        File.WriteAllText(Path.Combine(area.Root, "downloads", "BepInEx_unix_6.0.0-be.788.zip"), "x");

        var found = BepInEx6Locator.FindCandidates(area.Root);

        Assert.True(found.Ok, found.Error?.Message);
        Assert.Equal(new[] { package }, found.Value!.ToArray());
    }
}


