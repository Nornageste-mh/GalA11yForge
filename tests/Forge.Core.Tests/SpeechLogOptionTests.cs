using GalA11yForge.Cli;

namespace GalA11yForge.Core.Tests;

public sealed class SpeechLogOptionTests
{
    [Fact]
    public void Verify_accepts_speech_log_path()
    {
        var result = CommandLineParser.Parse(["verify", "--speech-log", "D:\\logs\\LogOutput.log"]);

        Assert.Null(result.Error);
        Assert.NotNull(result.Options);
        Assert.Equal(CliCommand.Verify, result.Options!.Command);
        Assert.Equal("D:\\logs\\LogOutput.log", result.Options.SpeechLogPath);
    }

    [Fact]
    public void Speech_log_requires_value()
    {
        var result = CommandLineParser.Parse(["verify", "--speech-log"]);

        Assert.NotNull(result.Error);
        Assert.Contains("--speech-log", result.Error!.Message);
    }

    [Fact]
    public void Speech_log_supports_equals_form()
    {
        var result = CommandLineParser.Parse(["verify", "--speech-log=ws\\log.txt"]);

        Assert.Null(result.Error);
        Assert.Equal("ws\\log.txt", result.Options!.SpeechLogPath);
    }
}
