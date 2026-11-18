using GalA11yForge.Gui;

namespace GalA11yForge.Gui.Tests;

/// <summary>启动参数：解析失败不能让 GUI 打不开，只记 Problems 在界面上提示。</summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public void NoArgumentsMeansDefaults()
    {
        var options = GuiStartupOptions.Parse(null);

        Assert.Null(options.ForgeExePath);
        Assert.Null(options.GamePath);
        Assert.Null(options.WorkspacePath);
        Assert.False(options.DryRun);
        Assert.Empty(options.Problems);
    }

    [Fact]
    public void ParsesAllSupportedArguments()
    {
        var options = GuiStartupOptions.Parse(new[]
        {
            "--forge", @"C:\tools\forge.exe",
            "--game", @"D:\Games\五月雨",
            "--workspace", @"D:\ws",
            "--dry-run",
        });

        Assert.Equal(@"C:\tools\forge.exe", options.ForgeExePath);
        Assert.Equal(@"D:\Games\五月雨", options.GamePath);
        Assert.Equal(@"D:\ws", options.WorkspacePath);
        Assert.True(options.DryRun);
        Assert.Empty(options.Problems);
    }

    [Fact]
    public void SupportsInlineEqualsSyntax()
    {
        var options = GuiStartupOptions.Parse(new[] { "--game=D:\\Games", "--ws=D:\\ws", "--exe=C:\\forge.exe" });

        Assert.Equal(@"D:\Games", options.GamePath);
        Assert.Equal(@"D:\ws", options.WorkspacePath);
        Assert.Equal(@"C:\forge.exe", options.ForgeExePath);
    }

    [Fact]
    public void SupportsAliases()
    {
        var options = GuiStartupOptions.Parse(new[] { "--exe", @"C:\forge.exe", "--ws", @"D:\ws", "--dryrun" });

        Assert.Equal(@"C:\forge.exe", options.ForgeExePath);
        Assert.Equal(@"D:\ws", options.WorkspacePath);
        Assert.True(options.DryRun);
    }

    [Fact]
    public void MissingValueIsRecordedButOtherArgumentsStillParse()
    {
        var options = GuiStartupOptions.Parse(new[] { "--game", "--dry-run" });

        Assert.Null(options.GamePath);
        Assert.Contains(options.Problems, problem => problem.Contains("缺少路径"));
    }

    [Fact]
    public void UnknownArgumentIsRecordedNotThrown()
    {
        var options = GuiStartupOptions.Parse(new[] { "--nope", "--game", @"D:\Games" });

        Assert.Equal(@"D:\Games", options.GamePath);
        Assert.Contains(options.Problems, problem => problem.Contains("未知参数"));
    }

    [Fact]
    public void ValuesAreTrimmed()
    {
        var options = GuiStartupOptions.Parse(new[] { "--game", "  D:\\Games  " });

        Assert.Equal(@"D:\Games", options.GamePath);
    }

    [Fact]
    public void BlankArgumentIsIgnored()
    {
        var options = GuiStartupOptions.Parse(new[] { "  ", "--dry-run" });

        Assert.True(options.DryRun);
        Assert.Empty(options.Problems);
    }
}
