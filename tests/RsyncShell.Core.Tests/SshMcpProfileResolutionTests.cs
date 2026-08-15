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
    public async Task UploadFileRejectsRelativeLocalPathBeforeLoadingSessions()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            SshTools.UploadFileAsync("unused", "relative.txt", "/tmp/relative.txt"));

        Assert.Equal("localPath", error.ParamName);
    }

    [Fact]
    public async Task DownloadFileRejectsExistingDestinationByDefaultBeforeLoadingSessions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ssh-win-gui-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "example.txt");
        File.WriteAllText(path, "existing");
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                SshTools.DownloadFileAsync("unused", "/tmp/example.txt", directory));

            Assert.Contains("overwrite=true", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileTransferRejectsRemotePathContainingNull()
    {
        var path = Path.GetTempFileName();
        try
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() =>
                SshTools.DownloadFileAsync("unused", "/tmp/bad\0name", path));

            Assert.Equal("remotePath", error.ParamName);
        }
        finally
        {
            File.Delete(path);
        }
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
