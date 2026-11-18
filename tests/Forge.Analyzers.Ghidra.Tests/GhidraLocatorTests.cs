namespace GalA11yForge.Analyzers.Ghidra.Tests;

public sealed class GhidraLocatorTests
{
    private static IReadOnlyDictionary<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var dictionary = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            dictionary[key] = value;
        }

        return dictionary;
    }

    [Fact]
    public void Locate_ExplicitOption_WinsOverEnvironmentAndProbe()
    {
        using var temp = new TempDir();
        var explicitHome = FakeGhidra.CreateHome(temp.Sub("explicit"), "12.1.4");
        var envHome = FakeGhidra.CreateHome(temp.Sub("env"), "11.0.3");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            ExplicitPath = explicitHome,
            Environment = Env(("GHIDRA_HOME", envHome), ("GHIDRA_INSTALL_DIR", envHome)),
            ProbeRoots = Array.Empty<string>(),
        });

        Assert.True(location.Available);
        Assert.Equal(Path.GetFullPath(explicitHome), Path.GetFullPath(location.HomeDirectory!));
        Assert.Equal("option:ghidra.path", location.Source);
        Assert.Equal("12.1.4", location.Version);
        Assert.Equal(
            Path.Combine(explicitHome, "support", "analyzeHeadless.bat"),
            location.AnalyzeHeadlessPath);
    }

    [Fact]
    public void Locate_InvalidExplicitPath_DoesNotFallBackToEnvironment()
    {
        using var temp = new TempDir();
        var envHome = FakeGhidra.CreateHome(temp.Sub("env"), "11.0.3");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            ExplicitPath = Path.Combine(temp.Root, "no-such-ghidra"),
            Environment = Env(("GHIDRA_HOME", envHome)),
            ProbeRoots = Array.Empty<string>(),
        });

        Assert.False(location.Available);
        Assert.Null(location.HomeDirectory);
        Assert.Equal("option:ghidra.path", location.Source);
        Assert.Contains("指定的 Ghidra 路径无效", location.Error);
    }

    [Fact]
    public void Locate_EnvHome_PreferredOverInstallDir()
    {
        using var temp = new TempDir();
        var home = FakeGhidra.CreateHome(temp.Sub("home"), "12.1.4");
        var install = FakeGhidra.CreateHome(temp.Sub("install"), "11.0.3");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            Environment = Env(("GHIDRA_HOME", home), ("GHIDRA_INSTALL_DIR", install)),
            ProbeRoots = Array.Empty<string>(),
        });

        Assert.True(location.Available);
        Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(location.HomeDirectory!));
        Assert.Equal("env:GHIDRA_HOME", location.Source);
    }

    [Fact]
    public void Locate_EnvInstallDir_UsedWhenHomeMissing()
    {
        using var temp = new TempDir();
        var install = FakeGhidra.CreateHome(temp.Sub("install"), "12.1.2");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            Environment = Env(("GHIDRA_HOME", null), ("GHIDRA_INSTALL_DIR", install)),
            ProbeRoots = Array.Empty<string>(),
        });

        Assert.True(location.Available);
        Assert.Equal("env:GHIDRA_INSTALL_DIR", location.Source);
        Assert.Equal("12.1.2", location.Version);
    }

    [Fact]
    public void Locate_InvalidEnvPath_FallsThroughToProbe()
    {
        using var temp = new TempDir();
        var probeHome = FakeGhidra.CreateHome(temp.Sub("probe"), "12.1.4");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            Environment = Env(("GHIDRA_HOME", Path.Combine(temp.Root, "broken"))),
            ProbeRoots = new[] { Path.Combine(temp.Root, "probe") },
        });

        Assert.True(location.Available);
        Assert.Equal("probe", location.Source);
        Assert.Equal(Path.GetFullPath(probeHome), Path.GetFullPath(location.HomeDirectory!));
    }

    [Fact]
    public void Locate_ProbeRoot_FindsVersionedSubdirectory_AndReadsVersionFromProperties()
    {
        using var temp = new TempDir();
        var home = FakeGhidra.CreateHome(temp.Root, "12.1.4");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            Environment = Env(),
            ProbeRoots = new[] { temp.Root },
        });

        Assert.True(location.Available);
        Assert.Equal("probe", location.Source);
        Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(location.HomeDirectory!));
        Assert.Equal("12.1.4", location.Version);
        Assert.NotEmpty(location.Probes);
    }

    [Fact]
    public void Locate_ProbeRoot_PrefersNewestVersion()
    {
        using var temp = new TempDir();
        FakeGhidra.CreateHome(temp.Root, "11.0.3");
        var newest = FakeGhidra.CreateHome(temp.Root, "12.1.4");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            Environment = Env(),
            ProbeRoots = new[] { temp.Root },
        });

        Assert.True(location.Available);
        Assert.Equal(Path.GetFullPath(newest), Path.GetFullPath(location.HomeDirectory!));
        Assert.Equal("12.1.4", location.Version);
    }

    [Fact]
    public void Locate_DirectoryWithoutAnalyzeHeadless_IsNotAvailable()
    {
        using var temp = new TempDir();
        var home = Path.Combine(temp.Root, "ghidra_12.1.4_PUBLIC");
        Directory.CreateDirectory(Path.Combine(home, "Ghidra"));
        File.WriteAllText(
            Path.Combine(home, "Ghidra", "application.properties"),
            "application.version=12.1.4\n");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            Environment = Env(),
            ProbeRoots = new[] { home },
        });

        Assert.False(location.Available);
        Assert.Contains("未找到可用的 Ghidra", location.Error);
        Assert.Contains(location.Probes, p => p.Contains("ghidra_12.1.4_PUBLIC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Locate_ExplicitAnalyzeHeadlessBat_ResolvesHome()
    {
        using var temp = new TempDir();
        var home = FakeGhidra.CreateHome(temp.Sub("explicit"), "12.1.4");
        var bat = Path.Combine(home, "support", "analyzeHeadless.bat");

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            ExplicitPath = bat,
            Environment = Env(),
            ProbeRoots = Array.Empty<string>(),
        });

        Assert.True(location.Available);
        Assert.Equal(Path.GetFullPath(home), Path.GetFullPath(location.HomeDirectory!));
        Assert.Equal(Path.GetFullPath(bat), Path.GetFullPath(location.AnalyzeHeadlessPath!));
    }

    [Fact]
    public void Locate_NothingConfigured_ReportsActionableError()
    {
        using var temp = new TempDir();

        var location = GhidraLocator.Locate(new GhidraLocationRequest
        {
            Environment = Env(),
            ProbeRoots = new[] { temp.Sub("empty") },
        });

        Assert.False(location.Available);
        Assert.NotNull(location.Error);
        Assert.Contains("support\\analyzeHeadless.bat", location.Error);
        Assert.Null(location.HomeDirectory);
    }
}
