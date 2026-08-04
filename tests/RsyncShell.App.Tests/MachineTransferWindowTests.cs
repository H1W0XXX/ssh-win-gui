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

    [Fact]
    public void OverwriteWarningRequiresAnActualSameNameEntry()
    {
        var collisions = MachineTransferWindow.FindTopLevelCollisions(
            ["GLM-5.2-FP8"],
            ["DeepSeek-V4-Flash-DSpark", "GLM-5.2-NVFP4", "vllm-sm120.tar"]);

        Assert.Empty(collisions);
    }

    [Fact]
    public void OverwriteWarningListsSameNameEntries()
    {
        var collisions = MachineTransferWindow.FindTopLevelCollisions(
            ["GLM-5.2-FP8", "vllm-sm120.tar"],
            ["GLM-5.2-FP8", "other-file"]);

        Assert.Equal(["GLM-5.2-FP8"], collisions);
    }
}
