namespace GalA11yForge.Analyzers.Ghidra.Tests;

public sealed class GhidraCommandBuilderTests
{
    private static GhidraCommandRequest CreateRequest(
        TempDir temp,
        string? headlessPath = null,
        string projectName = "gala11y",
        bool deleteProject = true,
        params string[] extraArguments)
    {
        var home = FakeGhidra.CreateHome(temp.Sub("ghidra-home"));
        var import = Path.Combine(temp.Root, "game.exe");
        File.WriteAllText(import, "MZ");

        return new GhidraCommandRequest
        {
            AnalyzeHeadlessPath = headlessPath ?? Path.Combine(home, "support", "analyzeHeadless.bat"),
            ProjectDirectory = Path.Combine(temp.Root, "project"),
            ProjectName = projectName,
            ImportPath = import,
            ScriptDirectory = Path.Combine(temp.Root, "scripts"),
            OutputDirectory = Path.Combine(temp.Root, "out"),
            DeleteProject = deleteProject,
            ExtraArguments = extraArguments,
        };
    }

    [Fact]
    public void Build_BatchFile_IsWrappedInCmdWithQuotedHeadlessPath()
    {
        using var temp = new TempDir();
        var request = CreateRequest(temp);

        var command = GhidraCommandBuilder.Build(request);

        Assert.True(command.ViaCmd);
        Assert.Equal("cmd.exe", command.FileName);
        Assert.StartsWith("/d /s /c \"", command.Arguments);
        Assert.Contains(GhidraCommandBuilder.Quote(Path.GetFullPath(request.AnalyzeHeadlessPath)), command.Arguments);
        Assert.Equal(
            new[]
            {
                Path.GetFullPath(request.ProjectDirectory),
                "gala11y",
                "-import",
                Path.GetFullPath(request.ImportPath),
                "-overwrite",
                "-analysisTimeoutPerFile",
                "900",
                "-max-cpu",
                "4",
                "-scriptPath",
                Path.GetFullPath(request.ScriptDirectory),
                "-postScript",
                "CandidateHookDump.java",
                Path.GetFullPath(request.OutputDirectory),
                "-deleteProject",
            },
            command.Argv);
    }

    [Fact]
    public void Build_ExeHeadless_StartsDirectly()
    {
        using var temp = new TempDir();
        var exe = Path.Combine(temp.Root, "analyzeHeadless.exe");
        File.WriteAllText(exe, "MZ");
        var request = CreateRequest(temp, headlessPath: exe);

        var command = GhidraCommandBuilder.Build(request);

        Assert.False(command.ViaCmd);
        Assert.Equal(Path.GetFullPath(exe), command.FileName);
        Assert.DoesNotContain("/d /s /c", command.Arguments);
    }

    [Fact]
    public void Build_RelativePath_IsRejected()
    {
        using var temp = new TempDir();
        var request = CreateRequest(temp);
        var broken = new GhidraCommandRequest
        {
            AnalyzeHeadlessPath = request.AnalyzeHeadlessPath,
            ProjectDirectory = "relative-project",
            ProjectName = request.ProjectName,
            ImportPath = request.ImportPath,
            ScriptDirectory = request.ScriptDirectory,
            OutputDirectory = request.OutputDirectory,
        };

        var ex = Assert.Throws<ArgumentException>(() => GhidraCommandBuilder.Build(broken));
        Assert.Contains("绝对路径", ex.Message);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("bad/name")]
    [InlineData("")]
    public void Build_InvalidProjectName_IsRejected(string projectName)
    {
        using var temp = new TempDir();
        var request = CreateRequest(temp, projectName: projectName);

        Assert.Throws<ArgumentException>(() => GhidraCommandBuilder.Build(request));
    }

    [Fact]
    public void Build_DeleteProjectFalse_OmitsFlag()
    {
        using var temp = new TempDir();
        var request = CreateRequest(temp, deleteProject: false);

        var command = GhidraCommandBuilder.Build(request);

        Assert.DoesNotContain("-deleteProject", command.Argv);
    }

    [Fact]
    public void Build_ExtraArguments_SitBeforeDeleteProject()
    {
        using var temp = new TempDir();
        var request = CreateRequest(temp, extraArguments: new[] { "-noanalysis", "-processor", "x86:LE:32:default" });

        var command = GhidraCommandBuilder.Build(request);

        Assert.Contains("-noanalysis", command.Argv);
        Assert.True(command.Argv.ToList().IndexOf("-noanalysis") < command.Argv.ToList().IndexOf("-deleteProject"));
        Assert.Equal(
            Path.GetFullPath(request.OutputDirectory),
            command.Argv[command.Argv.ToList().IndexOf("-noanalysis") - 1]);
    }

    [Fact]
    public void Build_BlankPostScript_IsRejected()
    {
        using var temp = new TempDir();
        var request = CreateRequest(temp);
        var broken = new GhidraCommandRequest
        {
            AnalyzeHeadlessPath = request.AnalyzeHeadlessPath,
            ProjectDirectory = request.ProjectDirectory,
            ProjectName = request.ProjectName,
            ImportPath = request.ImportPath,
            ScriptDirectory = request.ScriptDirectory,
            OutputDirectory = request.OutputDirectory,
            PostScript = " ",
        };

        Assert.Throws<ArgumentException>(() => GhidraCommandBuilder.Build(broken));
    }

    [Fact]
    public void Quote_LeavesSimpleTokensBare()
    {
        Assert.Equal("-import", GhidraCommandBuilder.Quote("-import"));
        Assert.Equal(@"C:\games\a.exe", GhidraCommandBuilder.Quote(@"C:\games\a.exe"));
    }

    [Fact]
    public void Quote_WrapsSpacesAndDoublesTrailingBackslashes()
    {
        Assert.Equal("\"C:\\games\\my game\"", GhidraCommandBuilder.Quote(@"C:\games\my game"));
        Assert.Equal("\"C:\\dir with space\\\\\"", GhidraCommandBuilder.Quote(@"C:\dir with space\"));
    }

    [Fact]
    public void Quote_EscapesEmbeddedQuotes()
    {
        Assert.Equal("\"a\\\"b\"", GhidraCommandBuilder.Quote("a\"b"));
    }
}
