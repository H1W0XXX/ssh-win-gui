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

    [Fact]
    public async Task RsyncUploadRejectsRelativeLocalSourceBeforeLoadingSessions()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            SshTools.RsyncUploadAsync("unused", "relative.txt", "/tmp/renamed.txt"));

        Assert.Equal("localSourcePath", error.ParamName);
    }

    [Fact]
    public async Task RsyncDownloadRejectsRelativeLocalDestinationBeforeLoadingSessions()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            SshTools.RsyncDownloadAsync("unused", "/tmp/example.txt", "relative.txt"));

        Assert.Equal("localDestinationPath", error.ParamName);
    }

    [Fact]
    public async Task RsyncTransferRejectsRemotePathContainingNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                SshTools.RsyncDownloadAsync("unused", "/tmp/bad\0name", path));

            Assert.Equal("remoteSourcePath", error.ParamName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RsyncUploadRejectsFilesystemRootAsDestination()
    {
        var path = Path.GetTempFileName();
        try
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                SshTools.RsyncUploadAsync("unused", path, "/"));

            Assert.Equal("remoteDestinationPath", error.ParamName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RsyncDownloadRejectsTrailingLocalDestinationSeparator()
    {
        var destination = Path.Combine(Path.GetTempPath(), "renamed") + Path.DirectorySeparatorChar;
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            SshTools.RsyncDownloadAsync("unused", "/tmp/example", destination));

        Assert.Equal("localDestinationPath", error.ParamName);
    }

    [Theory]
    [InlineData("/name", "/", "name")]
    [InlineData("/data/models/Qwen-New", "/data/models", "Qwen-New")]
    public void SplitRemotePathReturnsExactParentAndName(string path, string parent, string name)
    {
        Assert.Equal((parent, name), SshTools.SplitRemotePath(path));
    }

    [Fact]
    public void ResolveRsyncWorkerFindsSiblingOfPublishedMcpDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ssh-win-gui-mcp-" + Guid.NewGuid().ToString("N"));
        var mcpDirectory = Path.Combine(root, "tools", "mcp");
        var workerDirectory = Path.Combine(root, "tools", "rsync");
        var workerPath = Path.Combine(workerDirectory, "rsyncworker.exe");
        Directory.CreateDirectory(mcpDirectory);
        Directory.CreateDirectory(workerDirectory);
        File.WriteAllBytes(workerPath, []);
        try
        {
            Assert.Equal(workerPath, SshTools.ResolveRsyncWorkerPath(mcpDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConnectionProfile Profile(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Host = "example.invalid",
        Username = "test",
    };
}
