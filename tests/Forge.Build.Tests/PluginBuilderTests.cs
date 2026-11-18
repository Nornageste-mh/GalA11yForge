using System.ComponentModel;
using GalA11yForge.Core.Models;
using GalA11yForge.Core.Results;

namespace GalA11yForge.Build.Tests;

public sealed class PluginBuilderTests
{
    [Fact]
    public async Task DryRun_ComputesPlanWithoutCreatingOutputOrLog()
    {
        using var area = new TempArea();
        var projectDirectory = TestArtifacts.CreateProject(area.WorkspaceRoot);
        var generation = new GenerationResult { ProjectDirectory = projectDirectory };
        var options = TestArtifacts.Options(area);
        var output = options.OutputDirectory!;
        var logDirectory = options.LogDirectory!;
        var builder = new PluginBuilder(area.CreateWorkspace(), options);
        var context = area.CreateContext(dryRun: true);

        var result = await builder.BuildAsync(context, generation);

        Assert.True(result.Ok, result.Error?.ToString());
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(logDirectory));
        Assert.Empty(result.Value!.Artifacts);
        Assert.True(result.Value.Metadata.TryGetValue("dryRun", out var dryRun));
        Assert.Equal("true", dryRun);
        Assert.True(result.Value.Metadata.TryGetValue("plannedArtifacts", out var planned));
        Assert.EndsWith("SamplePlugin.dll", planned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Build_SucceedsAndReturnsArtifactHashesAndLog()
    {
        using var area = new TempArea();
        var projectDirectory = TestArtifacts.CreateProject(area.WorkspaceRoot);
        var generation = new GenerationResult { ProjectDirectory = projectDirectory };
        var options = TestArtifacts.Options(area);
        var builder = new PluginBuilder(area.CreateWorkspace(), options);

        var result = await builder.BuildAsync(area.CreateContext(), generation);

        Assert.True(result.Ok, result.Error?.ToString());
        var build = result.Value!;
        Assert.Single(build.Artifacts);
        Assert.True(File.Exists(build.Artifacts[0]));
        Assert.Equal("SamplePlugin.dll", Path.GetFileName(build.Artifacts[0]));
        Assert.NotNull(build.LogPath);
        Assert.True(File.Exists(build.LogPath));
        Assert.False(InstallManifestStore.HasBom(build.LogPath!));
        Assert.True(build.Metadata.ContainsKey("sha256:SamplePlugin.dll"));
        Assert.Equal(64, build.Metadata["sha256:SamplePlugin.dll"].Length);
        Assert.Equal("0", build.Metadata["exitCode"]);
    }

    [Fact]
    public async Task Build_CompileErrorReturnsArtifactInvalidAndKeepsLog()
    {
        using var area = new TempArea();
        var projectDirectory = TestArtifacts.CreateProject(area.WorkspaceRoot, invalidCode: true);
        var generation = new GenerationResult { ProjectDirectory = projectDirectory };
        var builder = new PluginBuilder(area.CreateWorkspace(), TestArtifacts.Options(area));

        var result = await builder.BuildAsync(area.CreateContext(), generation);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.ArtifactInvalid, result.Error!.Code);
        Assert.True(result.Error.Details!.ContainsKey("logPath"));
        Assert.True(File.Exists(result.Error.Details["logPath"]));
    }

    [Fact]
    public async Task Build_TimeoutIsReportedAsIoAndDoesNotSwallowResult()
    {
        using var area = new TempArea();
        var projectDirectory = TestArtifacts.CreateProject(area.WorkspaceRoot);
        var generation = new GenerationResult { ProjectDirectory = projectDirectory };
        var runner = new FakeProcessRunner
        {
            Result = new ProcessResult { ExitCode = -1, TimedOut = true, StandardOutput = "hung" },
        };
        var builder = new PluginBuilder(area.CreateWorkspace(), TestArtifacts.Options(area), runner);

        var result = await builder.BuildAsync(area.CreateContext(), generation);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.Io, result.Error!.Code);
        Assert.Contains("超时", result.Error.Message);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(runner.LastFileName!));
    }

    [Fact]
    public async Task Build_ProcessStartFailureIsDependencyMissing()
    {
        using var area = new TempArea();
        var projectDirectory = TestArtifacts.CreateProject(area.WorkspaceRoot);
        var generation = new GenerationResult { ProjectDirectory = projectDirectory };
        var runner = new FakeProcessRunner { Throw = new Win32Exception(2, "not found") };
        var builder = new PluginBuilder(area.CreateWorkspace(), TestArtifacts.Options(area), runner);

        var result = await builder.BuildAsync(area.CreateContext(), generation);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.DependencyMissing, result.Error!.Code);
        Assert.True(result.Error.Details!.ContainsKey("logPath"));
    }

    [Fact]
    public async Task Build_RejectsOutputOutsideWorkRoot()
    {
        using var area = new TempArea();
        var projectDirectory = TestArtifacts.CreateProject(area.WorkspaceRoot);
        var generation = new GenerationResult { ProjectDirectory = projectDirectory };
        var options = TestArtifacts.Options(area);
        options.OutputDirectory = area.WorkspaceRoot;
        var keep = area.WorkspaceFile("important.txt", "do not delete");
        var builder = new PluginBuilder(area.CreateWorkspace(), options);

        var result = await builder.BuildAsync(area.CreateContext(dryRun: true), generation);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Build_RejectsOutputAtWorkRootEvenWhenCleanIsEnabled()
    {
        using var area = new TempArea();
        var projectDirectory = TestArtifacts.CreateProject(area.WorkspaceRoot);
        var generation = new GenerationResult { ProjectDirectory = projectDirectory };
        var options = TestArtifacts.Options(area);
        options.OutputDirectory = Path.Combine(area.WorkspaceRoot, "work");
        options.CleanOutputDirectory = true;
        var keep = area.WorkspaceFile("work/keep.txt", "do not delete");
        var builder = new PluginBuilder(area.CreateWorkspace(), options);

        var result = await builder.BuildAsync(area.CreateContext(dryRun: true), generation);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Build_RejectsProjectOutsideWorkspace()
    {
        using var area = new TempArea();
        var outside = Path.Combine(area.Root, "outside-project");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Outside.csproj"), "<Project />");
        var generation = new GenerationResult { ProjectDirectory = outside };
        var builder = new PluginBuilder(area.CreateWorkspace(), TestArtifacts.Options(area));

        var result = await builder.BuildAsync(area.CreateContext(dryRun: true), generation);

        Assert.False(result.Ok);
        Assert.Equal(ForgeErrorCodes.PathUnsafe, result.Error!.Code);
    }
}
