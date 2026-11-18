using System.Text.Json;
using GalA11yForge.Cli;
using GalA11yForge.Core.Tools;

namespace GalA11yForge.Core.Tests;

public sealed class ReToolLocatorTests
{
    [Fact]
    public void Catalog_HasExpectedToolsWithUniqueIds()
    {
        var ids = ReToolCatalog.All.Select(static tool => tool.Id).ToArray();

        Assert.Equal(5, ReToolCatalog.All.Count);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("il2cppdumper", ids);
        Assert.Contains("pyghidra", ids);
        Assert.Contains("frida", ids);
        Assert.Contains("frida-il2cpp-bridge", ids);
        Assert.Contains("dnspyex", ids);
        Assert.All(ReToolCatalog.All, tool =>
        {
            Assert.StartsWith("https://", tool.DownloadUrl, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(tool.License));
            Assert.False(string.IsNullOrWhiteSpace(tool.PlacementHint));
        });
    }

    [Fact]
    public async Task ToolsCommand_TextOutput_ShowsLicenseForCompliance()
    {
        using var temp = new TempFolder();
        var toolDirectory = Path.Combine(temp.Root, "il2cppdumper", "v1.0");
        Directory.CreateDirectory(toolDirectory);
        var exe = Path.Combine(toolDirectory, "Il2CppDumper.exe");
        File.WriteAllText(exe, "fake");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["tools", "--workspace", temp.Root, "--option", "re.il2cppdumper.path=" + exe],
            stdout,
            stderr);

        Assert.Equal(0, exitCode);
        var text = stdout.ToString();
        Assert.Contains("已找到 Il2CppDumper", text);
        Assert.Contains("许可：MIT", text);
    }

    [Fact]
    public void Locate_FindsExecutableInToolDirectory()
    {
        using var temp = new TempFolder();
        var toolDirectory = Path.Combine(temp.Root, "il2cppdumper", "v1.0");
        Directory.CreateDirectory(toolDirectory);
        var exe = Path.Combine(toolDirectory, "Il2CppDumper.exe");
        File.WriteAllText(exe, "fake");

        var status = ReToolLocator.Locate([temp.Root]).Single(static tool => tool.Id == "il2cppdumper");

        Assert.True(status.Found);
        Assert.Equal(Path.GetFullPath(exe), status.Path);
    }

    [Fact]
    public void Locate_FindsExecutableUnderToolsReDirectory()
    {
        using var temp = new TempFolder();
        var toolDirectory = Path.Combine(temp.Root, "tools", "re", "dnspyex");
        Directory.CreateDirectory(toolDirectory);
        var exe = Path.Combine(toolDirectory, "dnSpy.exe");
        File.WriteAllText(exe, "fake");

        var status = ReToolLocator.Locate([temp.Root]).Single(static tool => tool.Id == "dnspyex");

        Assert.True(status.Found);
        Assert.Equal(Path.GetFullPath(exe), status.Path);
    }

    [Fact]
    public void Locate_ExplicitPathCanPointToFileOrDirectory()
    {
        using var temp = new TempFolder();
        var directory = Path.Combine(temp.Root, "external");
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "Il2CppDumper.exe");
        File.WriteAllText(exe, "fake");

        var byFile = ReToolLocator.Locate(
            [temp.Root],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ReToolCatalog.PathOptionKey("il2cppdumper")] = exe,
            }).Single(static tool => tool.Id == "il2cppdumper");
        var byDirectory = ReToolLocator.Locate(
            [temp.Root],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ReToolCatalog.PathOptionKey("il2cppdumper")] = directory,
            }).Single(static tool => tool.Id == "il2cppdumper");

        Assert.True(byFile.Found);
        Assert.True(byDirectory.Found);
        Assert.Equal(Path.GetFullPath(exe), byDirectory.Path);
    }

    [Fact]
    public void Locate_MissingExecutableReportsSearchedPathsAndHint()
    {
        using var temp = new TempFolder();

        var status = ReToolLocator.Locate([temp.Root]).Single(static tool => tool.Id == "il2cppdumper");

        Assert.False(status.Found);
        Assert.Null(status.Path);
        Assert.NotEmpty(status.SearchedPaths);
        Assert.Contains("re.il2cppdumper.path", status.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_PythonPackageOnlyAcceptedWhenExplicitlyConfigured()
    {
        using var temp = new TempFolder();
        var packageDirectory = Path.Combine(temp.Root, "tools", "re", "pyghidra");
        Directory.CreateDirectory(packageDirectory);

        var withoutOverride = ReToolLocator.Locate([temp.Root]).Single(static tool => tool.Id == "pyghidra");
        var withOverride = ReToolLocator.Locate(
            [temp.Root],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [ReToolCatalog.PathOptionKey("pyghidra")] = packageDirectory,
            }).Single(static tool => tool.Id == "pyghidra");

        Assert.False(withoutOverride.Found);
        Assert.True(withOverride.Found);
        Assert.Equal(Path.GetFullPath(packageDirectory), withOverride.Path);
    }

    [Fact]
    public async Task Run_ToolsJson_ReportsAllToolsAndSucceeds()
    {
        using var temp = new TempFolder();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["tools", "--workspace", temp.Root, "--json"],
            stdout,
            stderr);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("tools", json.RootElement.GetProperty("command").GetString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(5, data.GetProperty("total").GetInt32());
        Assert.Equal(0, data.GetProperty("found").GetInt32());
        Assert.Equal(5, data.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Run_ToolsWithExplicitPath_CountsFoundTool()
    {
        using var temp = new TempFolder();
        var directory = Path.Combine(temp.Root, "external");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Il2CppDumper.exe"), "fake");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            [
                "tools", "--workspace", temp.Root, "--json",
                "--option", "re.il2cppdumper.path=" + directory,
            ],
            stdout,
            stderr);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("found").GetInt32());
    }
}
