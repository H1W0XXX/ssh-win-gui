using RsyncShell.App.Dialogs;

namespace RsyncShell.App.Tests;

public sealed class MachineTransferWindowTests
{
    [Fact]
    public void ExtraArgumentsPreserveQuotedValuesWithoutShellText()
    {
        var success = MachineTransferWindow.TrySplitArguments(
            "--exclude='cache files' \"--filter=keep this\" --progress",
            out var arguments,
            out var error);

        Assert.True(success, error);
        Assert.Equal(["--exclude=cache files", "--filter=keep this", "--progress"], arguments);
    }

    [Fact]
    public void ExtraArgumentsRejectUnclosedQuotes()
    {
        var success = MachineTransferWindow.TrySplitArguments(
            "--exclude='unfinished",
            out var arguments,
            out var error);

        Assert.False(success);
        Assert.Empty(arguments);
        Assert.NotEmpty(error);
    }
}
