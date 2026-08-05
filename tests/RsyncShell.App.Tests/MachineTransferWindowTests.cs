using RsyncShell.App.Dialogs;
using RsyncShell.Core.Models;

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

    [Fact]
    public void RouteCandidatesIncludeSavedDirectAndJumpRoutesWithoutDuplicates()
    {
        var profile = new ConnectionProfile
        {
            Id = "target",
            Name = "target",
            Host = "203.0.113.10",
            Port = 60022,
            Username = "ubuntu",
            ProxyKind = SshProxyKind.JumpHost,
            JumpProfileId = "jump",
        };
        var inventory = new RemoteNetworkInventory
        {
            HostName = "target",
            SshLocalPort = 60022,
            Addresses =
            [
                new RemoteNetworkAddress
                {
                    InterfaceName = "ens3",
                    Address = "10.0.0.12",
                    AddressFamily = 4,
                    PrefixLength = 24,
                },
                new RemoteNetworkAddress
                {
                    InterfaceName = "duplicate-public",
                    Address = "203.0.113.10",
                    AddressFamily = 4,
                    PrefixLength = 32,
                },
            ],
        };

        var candidates = MachineTransferWindow.BuildRouteCandidates(profile, inventory);

        Assert.Equal(3, candidates.Count);
        Assert.Single(candidates, candidate =>
            candidate.Host == profile.Host && candidate.Port == profile.Port &&
            candidate.IsSavedEndpoint && !candidate.UseTargetProxy);
        Assert.Single(candidates, candidate =>
            candidate.Host == profile.Host && candidate.Port == profile.Port &&
            candidate.IsSavedEndpoint && candidate.UseTargetProxy);
        Assert.Single(candidates, candidate => candidate.Host == "10.0.0.12");
    }
}
