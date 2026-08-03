using RsyncShell.Core.Models;
using RsyncShell.Mcp;

namespace RsyncShell.Core.Tests;

public sealed class SshMcpProfileResolutionTests
{
    [Fact]
    public void ResolveProfileAcceptsIdAndExactName()
    {
        var first = Profile("first-id", "Build host");
        var second = Profile("second-id", "Storage host");
        ConnectionProfile[] profiles = [first, second];

        Assert.Same(first, SshTools.ResolveProfile(profiles, " FIRST-ID "));
        Assert.Same(second, SshTools.ResolveProfile(profiles, "storage HOST"));
    }

    [Fact]
    public void ResolveProfileRejectsMissingAndAmbiguousNames()
    {
        ConnectionProfile[] profiles = [
            Profile("first-id", "Duplicate"),
            Profile("second-id", "Duplicate"),
        ];

        Assert.Throws<KeyNotFoundException>(() => SshTools.ResolveProfile(profiles, "missing"));
        Assert.Throws<InvalidOperationException>(() => SshTools.ResolveProfile(profiles, "Duplicate"));
    }

    [Fact]
    public void SanitizeErrorRedactsEverySavedPrivateKeyPath()
    {
        var first = Profile("first-id", "First") with { PrivateKeyPath = @"D:\keys\first.key" };
        var second = Profile("second-id", "Second") with { PrivateKeyPath = @"D:\keys\second.key" };

        var sanitized = SshTools.SanitizeError(
            @"Could not load D:\keys\FIRST.key through D:\keys\second.key.",
            [first, second]);

        Assert.Equal("Could not load <private-key> through <private-key>.", sanitized);
    }

    private static ConnectionProfile Profile(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Host = "example.invalid",
        Username = "test",
    };
}
