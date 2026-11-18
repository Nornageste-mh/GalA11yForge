using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>参数构建：GUI 与 CLI 之间的唯一契约，顺序被固定以便断言与复现。</summary>
public sealed class ArgumentBuilderTests
{
    private const string Game = @"D:\Games\五月雨";

    [Fact]
    public void Detect_BuildsGameThenJson()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Detect,
            GamePath = Game,
        });

        Assert.Equal(new[] { "detect", "--game", Game, "--json" }, args);
    }

    [Fact]
    public void Doctor_DoesNotNeedGame()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest { Command = ForgeCommandKind.Doctor });

        Assert.Equal(new[] { "doctor", "--json" }, args);
    }

    [Fact]
    public void Rollback_UsesVerifyWithRollbackFlagBeforeJson()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Rollback,
            GamePath = Game,
        });

        Assert.Equal(new[] { "verify", "--game", Game, "--rollback", "--json" }, args);
    }

    [Fact]
    public void Paths_KeepWorkspaceAndBepInExOrder()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Install,
            GamePath = Game,
            WorkspacePath = @"D:\ws",
            BepInExPath = @"D:\pkg\BepInEx.zip",
        });

        Assert.Equal(
            new[] { "install", "--game", Game, "--workspace", @"D:\ws", "--bepinex", @"D:\pkg\BepInEx.zip", "--json" },
            args);
    }

    [Fact]
    public void BlankPaths_AreDropped()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Detect,
            GamePath = "   ",
            WorkspacePath = null,
            BepInExPath = "",
        });

        Assert.Equal(new[] { "detect", "--json" }, args);
    }

    [Fact]
    public void Paths_AreTrimmed()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Analyze,
            GamePath = "  " + Game + "  ",
        });

        Assert.Equal(new[] { "analyze", "--game", Game, "--json" }, args);
    }

    [Fact]
    public void DryRun_AndVerbose_ProduceFlags()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Install,
            GamePath = Game,
            DryRun = true,
            Verbose = true,
        });

        Assert.Equal(new[] { "install", "--game", Game, "--dry-run", "--verbose", "--json" }, args);
    }

    [Fact]
    public void Force_IsOnlyEmittedForCommandsThatSupportIt()
    {
        var install = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Install,
            GamePath = Game,
            Force = true,
        });
        var detect = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Detect,
            GamePath = Game,
            Force = true,
        });

        Assert.Contains("--force", install);
        Assert.DoesNotContain("--force", detect);
    }

    [Fact]
    public void IncludeJsonFalse_OmitsEnvelopeFlag()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Detect,
            GamePath = Game,
            IncludeJson = false,
        });

        Assert.Equal(new[] { "detect", "--game", Game }, args);
    }

    [Fact]
    public void Verify_WithSpeechLog_AddsSpeechLogFlag()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Verify,
            GamePath = Game,
            SpeechLogPath = @"D:\Games\五月雨\BepInEx\LogOutput.log",
        });

        Assert.Equal(
            new[] { "verify", "--game", Game, "--speech-log", @"D:\Games\五月雨\BepInEx\LogOutput.log", "--json" },
            args);
    }

    [Fact]
    public void SpeechLog_IsIgnoredForCommandsThatDoNotAcceptIt()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Install,
            GamePath = Game,
            SpeechLogPath = @"D:\log.txt",
        });

        Assert.DoesNotContain("--speech-log", args);
    }

    [Fact]
    public void Analyze_WithAdvanced_AddsAdvancedFlag()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Analyze,
            GamePath = Game,
            Advanced = true,
        });

        Assert.Equal(new[] { "analyze", "--game", Game, "--advanced", "--json" }, args);
    }

    [Fact]
    public void Advanced_IsIgnoredForCommandsOtherThanAnalyze()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Detect,
            GamePath = Game,
            Advanced = true,
        });

        Assert.DoesNotContain("--advanced", args);
    }

    [Fact]
    public void Report_BuildsReportCommandWithGameAndJson()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Report,
            GamePath = Game,
        });

        Assert.Equal(new[] { "report", "--game", Game, "--json" }, args);
    }

    [Fact]
    public void Report_IgnoresAdvancedAndSpeechLog()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Report,
            GamePath = Game,
            Advanced = true,
            SpeechLogPath = @"D:\log.txt",
        });

        Assert.DoesNotContain("--advanced", args);
        Assert.DoesNotContain("--speech-log", args);
    }

    [Fact]
    public void Describe_QuotesArgumentsWithSpaces()
    {
        var text = ForgeArgumentBuilder.Describe(new[] { "detect", "--game", @"D:\My Games\foo" }, "forge");

        Assert.Equal("forge detect --game \"D:\\My Games\\foo\"", text);
    }

    [Fact]
    public void Describe_EscapesEmbeddedQuotes_AndKeepsEmptyArgument()
    {
        var text = ForgeArgumentBuilder.Describe(new[] { "echo", "a\"b", "" }, "tool");

        Assert.Equal("tool echo \"a\\\"b\" \"\"", text);
    }

    [Fact]
    public void Request_RequiresGameMirrorsDescriptor()
    {
        Assert.True(new ForgeRequest { Command = ForgeCommandKind.Install }.RequiresGame);
        Assert.False(new ForgeRequest { Command = ForgeCommandKind.Doctor }.RequiresGame);
    }

    [Fact]
    public void Analyze_WithGhidraExecutable_UsesOptionPath()
    {
        var executable = @"D:\Games\To the Moon\To the Moon\To the Moon.exe";
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Analyze,
            GamePath = Game,
            Advanced = true,
            GhidraExecutablePath = executable,
        });

        Assert.Equal(
            new[] { "analyze", "--game", Game, "--advanced", "--option", "ghidra.executablePath=" + executable, "--json" },
            args);
    }

    [Fact]
    public void GhidraExecutable_IsIgnoredForCommandsOtherThanAnalyze()
    {
        var args = ForgeArgumentBuilder.Build(new ForgeRequest
        {
            Command = ForgeCommandKind.Report,
            GamePath = Game,
            GhidraExecutablePath = @"D:\Games\game.exe",
        });

        Assert.DoesNotContain(args, argument => argument.Contains("ghidra.executablePath", StringComparison.Ordinal));
    }
}
