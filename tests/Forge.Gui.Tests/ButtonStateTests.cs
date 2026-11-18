using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>
/// 按钮状态机：读屏用户看不到「灰掉的按钮」，必须能听到「为什么不能按」；
/// 所以每个禁用原因都要有可朗读的 Reason。
/// </summary>
public sealed class ButtonStateTests
{
    private const string Game = @"D:\Games\五月雨";
    private const string Exe = @"D:\DSHWorkSpace\src\Forge.Cli\bin\Release\net8.0\forge.exe";

    private static GuiInputs Inputs(
        string? game = Game,
        string? exe = Exe,
        bool dryRun = false,
        bool busy = false,
        ForgeCommandKind? running = null,
        bool gameExists = true,
        bool exeExists = true,
        bool advanced = false,
        bool force = false,
        string? ghidraExecutablePath = null,
        long? ghidraExecutableSizeBytes = null)
        => GuiInputs.Create(
            game,
            workspacePath: null,
            bepInExPath: null,
            forgeExePath: exe,
            dryRun,
            busy,
            running,
            _ => gameExists,
            _ => exeExists,
            speechLogPath: null,
            advanced: advanced,
            ghidraExecutablePath: ghidraExecutablePath,
            ghidraExecutableSizeBytes: ghidraExecutableSizeBytes,
            force: force);

    [Fact]
    public void HintText_MentionsForce_WhenChecked()
    {
        var hint = GuiStateMachine.HintText(Inputs(force: true));
        Assert.Contains("允许强制", hint, StringComparison.Ordinal);
        Assert.Contains("--force", hint, StringComparison.Ordinal);

        var normal = GuiStateMachine.HintText(Inputs());
        Assert.DoesNotContain("允许强制", normal, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceInput_IsCarriedIntoInputs()
    {
        Assert.True(Inputs(force: true).Force);
        Assert.False(Inputs().Force);
    }

    [Fact]
    public void ReadyInputs_EnableEverything()
    {
        var inputs = Inputs();

        Assert.True(GuiStateMachine.AnyEnabled(inputs));
        foreach (var descriptor in ForgeCommands.AllOrdered)
        {
            var availability = GuiStateMachine.Evaluate(descriptor.Kind, inputs);
            Assert.True(availability.Enabled, descriptor.Title);
            Assert.Equal(BlockingKind.None, availability.BlockingKind);
            Assert.Null(availability.Reason);
        }
    }

    [Fact]
    public void BusyDisablesEveryButton_AndSaysWhatIsRunning()
    {
        var inputs = Inputs(busy: true, running: ForgeCommandKind.Build);

        foreach (var descriptor in ForgeCommands.AllOrdered)
        {
            var availability = GuiStateMachine.Evaluate(descriptor.Kind, inputs);
            Assert.False(availability.Enabled, descriptor.Title);
            Assert.Equal(BlockingKind.Busy, availability.BlockingKind);
            Assert.Contains("构建补丁", availability.Reason);
            Assert.Contains("停止", availability.Reason);
        }

        Assert.False(GuiStateMachine.AnyEnabled(inputs));
    }

    [Fact]
    public void MissingExecutableDisablesEverything()
    {
        var inputs = Inputs(exe: null, exeExists: false);

        foreach (var descriptor in ForgeCommands.AllOrdered)
        {
            var availability = GuiStateMachine.Evaluate(descriptor.Kind, inputs);
            Assert.False(availability.Enabled, descriptor.Title);
            Assert.Equal(BlockingKind.MissingExecutable, availability.BlockingKind);
            Assert.Contains("forge.exe", availability.Reason);
        }
    }

    [Fact]
    public void MissingGamePath_BlocksEverythingExceptDoctor()
    {
        var inputs = Inputs(game: null, gameExists: false);

        foreach (var descriptor in ForgeCommands.AllOrdered)
        {
            var availability = GuiStateMachine.Evaluate(descriptor.Kind, inputs);
            if (descriptor.RequiresGame)
            {
                Assert.False(availability.Enabled, descriptor.Title);
                Assert.Equal(BlockingKind.MissingGamePath, availability.BlockingKind);
                Assert.Contains("Alt+1", availability.Reason);
            }
            else
            {
                Assert.True(availability.Enabled, descriptor.Title);
            }
        }
    }

    [Fact]
    public void MissingGameDirectory_ReportsPath()
    {
        var inputs = Inputs(gameExists: false);

        var availability = GuiStateMachine.Evaluate(ForgeCommandKind.Detect, inputs);

        Assert.False(availability.Enabled);
        Assert.Equal(BlockingKind.GamePathNotFound, availability.BlockingKind);
        Assert.Contains(Game, availability.Reason);
    }

    [Fact]
    public void OneClickFollowsDetectRules()
    {
        var blocked = GuiStateMachine.EvaluateOneClick(Inputs(game: null, gameExists: false));
        var allowed = GuiStateMachine.EvaluateOneClick(Inputs());

        Assert.False(blocked.Enabled);
        Assert.Equal(BlockingKind.MissingGamePath, blocked.BlockingKind);
        Assert.True(allowed.Enabled);
    }

    [Fact]
    public void StopIsOnlyEnabledWhileBusy()
    {
        Assert.True(GuiStateMachine.EvaluateStop(Inputs(busy: true, running: ForgeCommandKind.Detect)).Enabled);

        var idle = GuiStateMachine.EvaluateStop(Inputs());
        Assert.False(idle.Enabled);
        Assert.Contains("没有正在执行", idle.Reason);
    }

    [Fact]
    public void EvaluateAllCoversEveryCommand()
    {
        var map = GuiStateMachine.EvaluateAll(Inputs());

        Assert.Equal(ForgeCommands.AllOrdered.Count, map.Count);
        Assert.All(map.Values, availability => Assert.True(availability.Enabled));
    }

    [Fact]
    public void HintText_ExplainsBusyState()
    {
        var hint = GuiStateMachine.HintText(Inputs(busy: true, running: ForgeCommandKind.Install));

        Assert.Contains("安装补丁", hint);
        Assert.Contains("Esc", hint);
        Assert.Contains("Alt+X", hint);
    }

    [Fact]
    public void HintText_ExplainsMissingExecutable()
    {
        var hint = GuiStateMachine.HintText(Inputs(exe: null, exeExists: false));

        Assert.Contains("forge.exe", hint);
        Assert.Contains("Release", hint);
    }

    [Fact]
    public void HintText_ExplainsMissingGameButMentionsDoctor()
    {
        var hint = GuiStateMachine.HintText(Inputs(game: "  ", gameExists: false));

        Assert.Contains("游戏目录", hint);
        Assert.Contains("环境自检", hint);
    }

    [Fact]
    public void HintText_MentionsMissingDirectoryPath()
    {
        var hint = GuiStateMachine.HintText(Inputs(gameExists: false));

        Assert.Contains(Game, hint);
    }

    [Fact]
    public void HintText_DescribesDryRunAndNormalModes()
    {
        Assert.Contains("演练模式", GuiStateMachine.HintText(Inputs(dryRun: true)));
        Assert.Contains("确认框", GuiStateMachine.HintText(Inputs()));
        // 0.2.18：GUI 里也要能知道“首句漏读”可调，不然用户不知道有这个开关。
        Assert.Contains("字段补读最长等待秒", GuiStateMachine.HintText(Inputs()));
        // 0.2.21：提示里要告诉用户“语音日志”能自动检查补丁有没有读出台词。
        Assert.Contains("语音日志", GuiStateMachine.HintText(Inputs()));
    }

    [Fact]
    public void ValidateBeforeRun_ReturnsNullWhenAllowed()
    {
        Assert.Null(GuiStateMachine.ValidateBeforeRun(ForgeCommandKind.Build, Inputs()));
        Assert.Null(GuiStateMachine.ValidateBeforeRun(ForgeCommandKind.Doctor, Inputs(game: null, gameExists: false)));
    }

    [Fact]
    public void ValidateBeforeRun_ReturnsReasonWhenBlocked()
    {
        var reason = GuiStateMachine.ValidateBeforeRun(ForgeCommandKind.Install, Inputs(game: null, gameExists: false));

        Assert.NotNull(reason);
        Assert.Contains("游戏目录", reason);
    }

    [Fact]
    public void Create_TrimsWhitespaceAndDetectsBlankPaths()
    {
        var inputs = Inputs(game: "  " + Game + "  ");

        Assert.Equal(Game, inputs.GamePath);
        Assert.True(inputs.HasGamePath);
        Assert.True(inputs.HasValidGame);

        var blank = Inputs(game: "   ", gameExists: false);
        Assert.Null(blank.GamePath);
        Assert.False(blank.HasGamePath);
        Assert.False(blank.HasValidGame);
    }

    [Fact]
    public void Create_RequiresProbes()
    {
        Assert.Throws<ArgumentNullException>(() => GuiInputs.Create(
            Game, null, null, Exe, false, false, null, null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => GuiInputs.Create(
            Game, null, null, Exe, false, false, null, _ => true, null!));
    }

    [Fact]
    public void HintText_MentionsAdvancedAnalysis_WhenChecked()
    {
        Assert.Contains("高级分析", GuiStateMachine.HintText(Inputs(advanced: true)));
    }

    [Fact]
    public void HintText_EstimatesRoughDuration_WhenExecutableSizeKnown()
    {
        var hint = GuiStateMachine.HintText(Inputs(
            advanced: true,
            ghidraExecutablePath: @"D:\Games\SenrenBanka.exe",
            ghidraExecutableSizeBytes: 4_583_168));

        Assert.Contains("将分析你指定的程序：SenrenBanka.exe", hint);
        Assert.Contains("按文件大小粗估", hint);
        Assert.Contains("约 402 秒", hint);
    }

    [Fact]
    public void HintText_OmitsEstimate_WhenSizeUnknown()
    {
        var hint = GuiStateMachine.HintText(Inputs(
            advanced: true,
            ghidraExecutablePath: @"D:\Games\SenrenBanka.exe"));

        Assert.Contains("将分析你指定的程序：SenrenBanka.exe", hint);
        Assert.DoesNotContain("按文件大小粗估", hint);
    }
}
