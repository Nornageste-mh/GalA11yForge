using GalA11yForge.Gui.Core;

namespace GalA11yForge.Gui.Tests;

/// <summary>
/// 助记符不唯一时，WinForms 会把「Alt+字母」分给第一个匹配控件，
/// 界面上写的字母就成了谎话；所以这里把唯一性钉死。
/// </summary>
public sealed class ShortcutTests
{
    [Fact]
    public void EveryCommandHasAShortcut()
    {
        var kinds = ForgeShortcuts.Buttons.Select(button => button.Kind).ToList();

        Assert.Equal(ForgeCommands.AllOrdered.Count, kinds.Count);
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
        foreach (var descriptor in ForgeCommands.AllOrdered)
        {
            Assert.Contains(descriptor.Kind, kinds);
        }
    }

    [Fact]
    public void CommandMnemonicsAreUnique()
    {
        var mnemonics = ForgeShortcuts.Buttons.Select(button => char.ToUpperInvariant(button.Mnemonic)).ToList();

        Assert.Equal(mnemonics.Count, mnemonics.Distinct().Count());
    }

    [Fact]
    public void UiActionMnemonicsAreUniqueAndDoNotCollideWithCommands()
    {
        var all = ForgeShortcuts.AllMnemonics().Select(char.ToUpperInvariant).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Contains(ForgeShortcuts.OneClick, all);
        Assert.Contains(ForgeShortcuts.Stop, all);
        Assert.Contains(ForgeShortcuts.ClearResult, all);
        Assert.Contains(ForgeShortcuts.ReadResult, all);
    }

    [Fact]
    public void InputMnemonicsAreDigits()
    {
        Assert.Equal(9, ForgeShortcuts.InputMnemonics.Count);
        Assert.All(ForgeShortcuts.InputMnemonics, mnemonic => Assert.True(char.IsDigit(mnemonic)));
    }

    [Fact]
    public void MnemonicOf_MatchesDescriptorTitle()
    {
        Assert.Equal('D', ForgeShortcuts.MnemonicOf(ForgeCommandKind.Detect));
        Assert.Equal('R', ForgeShortcuts.MnemonicOf(ForgeCommandKind.Rollback));
    }

    [Fact]
    public void MnemonicOf_ThrowsForUnknownCommand()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeShortcuts.MnemonicOf((ForgeCommandKind)99));
    }

    [Fact]
    public void OneClick_NeverWritesToGame()
    {
        Assert.NotEmpty(ForgeCommands.OneClickSequence);
        foreach (var kind in ForgeCommands.OneClickSequence)
        {
            var descriptor = ForgeCommands.Of(kind);
            Assert.False(descriptor.WritesToGame, descriptor.Title);
            Assert.False(descriptor.RequiresConfirmation, descriptor.Title);
        }
    }

    [Fact]
    public void WriteCommandsRequireConfirmation()
    {
        var writing = ForgeCommands.AllOrdered.Where(descriptor => descriptor.WritesToGame).ToList();

        Assert.Equal(2, writing.Count);
        Assert.All(writing, descriptor => Assert.True(descriptor.RequiresConfirmation, descriptor.Title));
        Assert.Contains(ForgeCommands.Install, writing);
        Assert.Contains(ForgeCommands.Rollback, writing);
    }

    [Fact]
    public void Of_ThrowsForUnknownCommand()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeCommands.Of((ForgeCommandKind)99));
    }

    [Fact]
    public void RollbackReusesVerifyCliName_ButKeepsItsOwnTitle()
    {
        Assert.Equal("verify", ForgeCommands.Rollback.CliName);
        Assert.Equal("回滚补丁", ForgeCommands.Rollback.Title);
        Assert.Equal(new[] { "--rollback" }, ForgeCommands.Rollback.FixedArguments);
        Assert.Empty(ForgeCommands.Verify.FixedArguments);
    }
}
