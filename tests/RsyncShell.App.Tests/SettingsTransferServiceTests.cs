using RsyncShell.App.Services;
using RsyncShell.Core.Models;

namespace RsyncShell.App.Tests;

public sealed class SettingsTransferServiceTests
{
    [Fact]
    public async Task ExportOmitsPasswordsPrivateKeysAndPrivateKeyPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ssh-win-gui-settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var profile = new ConnectionProfile
            {
                Id = "session-1",
                Name = "Test session",
                Host = "192.0.2.10",
                Port = 2222,
                Username = "ubuntu",
                Group = "Servers",
                PrivateKeyPath = @"D:\secret\id_ed25519",
                ProxyKind = SshProxyKind.Socks5,
                ProxyHost = "127.0.0.1",
                ProxyPort = 1080,
            };

            await SettingsTransferService.ExportAsync(directory, [profile]);

            var path = SettingsTransferService.GetExportPath(directory);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("id_ed25519", json, StringComparison.OrdinalIgnoreCase);

            var imported = await SettingsTransferService.ImportAsync(directory);
            var session = Assert.Single(imported.Sessions);
            Assert.Null(session.PrivateKeyPath);
            Assert.Equal(profile.Name, session.Name);
            Assert.Equal(profile.Host, session.Host);
            Assert.Equal(profile.ProxyKind, session.ProxyKind);
            Assert.Equal(profile.ProxyHost, session.ProxyHost);
        }
        finally
        {
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
