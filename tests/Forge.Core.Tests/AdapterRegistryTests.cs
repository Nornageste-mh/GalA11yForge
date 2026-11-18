using GalA11yForge.Core.Adapters;
using GalA11yForge.Core.Workspaces;

namespace GalA11yForge.Core.Tests;

public sealed class AdapterRegistryTests
{
    private const string ValidDocument = """
    {
      "schemaVersion": 1,
      "adapters": [
        {
          "name": "unity-mono-fallback",
          "engine": "unity_mono",
          "priority": 200,
          "assembly": "Forge.Adapters.UnityMono.dll",
          "type": "GalA11yForge.Adapters.UnityMono.FallbackAdapter",
          "capabilities": ["detect"]
        },
        {
          "name": "unity-mono",
          "engine": "unity_mono",
          "priority": 10,
          "assembly": "Forge.Adapters.UnityMono.dll",
          "type": "GalA11yForge.Adapters.UnityMono.UnityMonoAdapter",
          "capabilities": ["detect", "analyze", "plan", "generate", "verify"]
        }
      ]
    }
    """;

    [Fact]
    public void FromJson_SortsByPriorityThenName()
    {
        var result = AdapterRegistry.FromJson(ValidDocument);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Adapters.Count);
        Assert.Equal("unity-mono", result.Value.Adapters[0].Name);
        Assert.Equal(10, result.Value.Adapters[0].Priority);
        Assert.Equal("unity-mono-fallback", result.Value.Adapters[1].Name);
        Assert.Equal("unity_mono", result.Value.FindPreferred("unity_mono")!.Engine);
        Assert.Single(result.Value.FindByCapability("analyze"));
    }

    [Fact]
    public void FromJson_AcceptsTopLevelArray()
    {
        const string json = """
        [
          {
            "name": "renpy",
            "engine": "renpy",
            "assembly": "Forge.Adapters.RenPy.dll",
            "type": "GalA11yForge.Adapters.RenPy.RenPyAdapter",
            "capabilities": ["detect"]
          }
        ]
        """;

        var result = AdapterRegistry.FromJson(json);

        Assert.True(result.Ok);
        Assert.Single(result.Value!.Adapters);
    }

    [Fact]
    public void FromJson_RejectsDuplicateNames()
    {
        const string json = """
        {
          "adapters": [
            { "name": "dup", "engine": "renpy", "assembly": "a.dll", "type": "T", "capabilities": ["detect"] },
            { "name": "DUP", "engine": "renpy", "assembly": "a.dll", "type": "T", "capabilities": ["detect"] }
          ]
        }
        """;

        var result = AdapterRegistry.FromJson(json);

        Assert.False(result.Ok);
        Assert.Equal("E_ADAPTER_INVALID", result.Error!.Code);
    }

    [Fact]
    public void FromJson_RejectsMissingAssemblyAndCapabilities()
    {
        var missingAssembly = AdapterRegistry.FromJson("""
        { "adapters": [ { "name": "x", "engine": "renpy", "type": "T", "capabilities": ["detect"] } ] }
        """);
        var missingCapabilities = AdapterRegistry.FromJson("""
        { "adapters": [ { "name": "y", "engine": "renpy", "assembly": "a.dll", "type": "T" } ] }
        """);

        Assert.False(missingAssembly.Ok);
        Assert.Equal("E_ADAPTER_INVALID", missingAssembly.Error!.Code);
        Assert.False(missingCapabilities.Ok);
        Assert.Equal("E_ADAPTER_INVALID", missingCapabilities.Error!.Code);
    }

    [Fact]
    public void FromJson_RejectsUnsafeAssemblyPath()
    {
        var result = AdapterRegistry.FromJson("""
        { "adapters": [ { "name": "x", "engine": "renpy", "assembly": "../evil.dll", "type": "T", "capabilities": ["detect"] } ] }
        """);

        Assert.False(result.Ok);
        Assert.Equal("E_ADAPTER_INVALID", result.Error!.Code);
    }

    [Fact]
    public void LoadFromWorkspace_FindsAdaptersFolder()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);
        Assert.True(workspace.EnsureCreated().Ok);
        var registryPath = Path.Combine(workspace.RootPath, "adapters", "adapter.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registryPath)!);
        File.WriteAllText(registryPath, ValidDocument);

        var result = AdapterRegistry.LoadFromWorkspace(workspace);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Value!.Adapters.Count);
    }

    [Fact]
    public void LoadFromWorkspace_MissingRegistry_ReturnsAdapterNotFound()
    {
        using var temp = new TempFolder();
        var workspace = new ForgeWorkspace(temp.Root);

        var result = AdapterRegistry.LoadFromWorkspace(workspace);

        Assert.False(result.Ok);
        Assert.Equal("E_ADAPTER_NOT_FOUND", result.Error!.Code);
    }
}
