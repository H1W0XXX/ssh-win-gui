using System.Text.Json;
using Renci.SshNet.Common;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.Core.Tests;

public sealed class CoreContractsTests
{
    [Theory]
    [InlineData("alice@example.com:2200", "alice", "example.com", 2200)]
    [InlineData("ssh://alice@example.com:2200", "alice", "example.com", 2200)]
    [InlineData("alice@[2001:db8::1]:2200", "alice", "2001:db8::1", 2200)]
    public void QuickConnectParsesSupportedEndpoints(
        string value,
        string expectedUser,
        string expectedHost,
        int expectedPort)
    {
        Assert.True(ConnectionProfile.TryParseQuickConnect(
            value,
            "fallback",
            out var profile,
            out var error), error);
        Assert.NotNull(profile);
        Assert.Equal(expectedUser, profile.Username);
        Assert.Equal(expectedHost, profile.Host);
        Assert.Equal(expectedPort, profile.Port);
    }

    [Theory]
    [InlineData("ssh://alice:secret@example.com")]
    [InlineData("ssh://alice%3Asecret@example.com")]
    public void QuickConnectRejectsInlinePasswords(string value)
    {
        Assert.False(ConnectionProfile.TryParseQuickConnect(
            value,
            "fallback",
            out var profile,
            out var error));
        Assert.Null(profile);
        Assert.StartsWith("Inline SSH passwords", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationToStringRedactsSecrets()
    {
        var authentication = new SshAuthenticationOptions
        {
            Kind = SshAuthenticationKind.PrivateKey,
            PrivateKeyPath = "C:\\keys\\id_ed25519",
            PrivateKeyPassphrase = "do-not-print",
            Password = "also-do-not-print",
        };

        var text = authentication.ToString();
        Assert.Contains("<redacted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-print", text, StringComparison.Ordinal);
        Assert.DoesNotContain("also-do-not-print", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionProfileSerializationOmitsComputedEndpoint()
    {
        var profile = new ConnectionProfile
        {
            Id = "profile-1",
            Name = "Example",
            Host = "example.com",
            Username = "alice",
        };

        var json = JsonSerializer.Serialize(profile);

        Assert.DoesNotContain("DisplayEndpoint", json, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionRepositoryRoundTripsEditableProfileFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RsyncShell.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "sessions.json");
        var repository = new SessionRepository(path);
        var profile = new ConnectionProfile
        {
            Id = "saved-session",
            Name = "Production",
            Host = "server.example",
            Port = 2200,
            Username = "alice",
            Group = "Linux",
            PrivateKeyPath = "C:\\keys\\id_ed25519",
            Favorite = true,
            ProxyKind = SshProxyKind.Socks5,
            ProxyHost = "127.0.0.1",
            ProxyPort = 1086,
        };

        try
        {
            await repository.SaveAsync([profile]);
            var loaded = Assert.Single(await repository.LoadAsync());

            Assert.Equal(profile, loaded);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void JumpRouteResolvesInTargetToOuterJumpOrder()
    {
        var outer = Profile("outer");
        var inner = Profile("inner") with { ProxyKind = SshProxyKind.JumpHost, JumpProfileId = outer.Id };
        var target = Profile("target") with { ProxyKind = SshProxyKind.JumpHost, JumpProfileId = inner.Id };

        var route = SshRouteResolver.Resolve(target, [target, inner, outer]);

        Assert.Equal(["target", "inner", "outer"], route.Select(profile => profile.Id));
    }

    [Fact]
    public void JumpRouteRejectsCycles()
    {
        var first = Profile("first") with { ProxyKind = SshProxyKind.JumpHost, JumpProfileId = "second" };
        var second = Profile("second") with { ProxyKind = SshProxyKind.JumpHost, JumpProfileId = "first" };

        Assert.Throws<InvalidOperationException>(() => SshRouteResolver.Resolve(first, [first, second]));
        Assert.True(SshRouteResolver.WouldCreateCycle(first.Id, second.Id, [first, second]));
    }

    private static ConnectionProfile Profile(string id) => new()
    {
        Id = id,
        Name = id,
        Host = id + ".example",
        Username = "user",
        PrivateKeyPath = @"C:\keys\id_ed25519",
    };

    [Fact]
    [Trait("Category", "RemoteIntegration")]
    public async Task JumpClientUsesDirectTcpipWithoutLocalListener()
    {
        var host = Environment.GetEnvironmentVariable("RSYNCSHELL_TEST_JUMP_HOST");
        var targetHost = Environment.GetEnvironmentVariable("RSYNCSHELL_TEST_TARGET_HOST") ?? "127.0.0.1";
        var keyPath = Environment.GetEnvironmentVariable("RSYNCSHELL_TEST_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(keyPath)) return;

        var jump = new ConnectionProfile
        {
            Id = "remote-jump",
            Name = "remote-jump",
            Host = host,
            Username = "ubuntu",
            PrivateKeyPath = keyPath,
        };
        var target = new ConnectionProfile
        {
            Id = "loopback-target",
            Name = "loopback-target",
            Host = targetHost,
            Username = "ubuntu",
            PrivateKeyPath = keyPath,
            ProxyKind = SshProxyKind.JumpHost,
            JumpProfileId = jump.Id,
        };
        var authentication = new SshAuthenticationOptions
        {
            Kind = SshAuthenticationKind.PrivateKey,
            PrivateKeyPath = keyPath,
        };

        using var client = JumpSshClientFactory.Create(target, authentication, [target, jump], _ => true);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(30));
        using var process = await client.ExecuteAsync("printf rsyncshell-jump-ok");
        process.WriteEof();
        var (stdout, stderr) = await process.ReadToEndAsStringAsync();
        Assert.Equal("rsyncshell-jump-ok", stdout);
        Assert.Empty(stderr);
        Assert.Equal(0, await process.GetExitCodeAsync());
    }

    [Fact]
    public void DirectoryResponseCarriesItsTruncationBoundary()
    {
        var response = RemoteFileService.Marker +
                       "{\"path\":\"/srv/data\",\"entries\":[],\"isTruncated\":true,\"entryLimit\":5000}";

        var listing = RemoteFileService.ParseResponse(response);

        Assert.True(listing.IsTruncated);
        Assert.Equal(RemoteFileService.DirectoryEntryLimit, listing.EntryLimit);
    }

    [Fact]
    public void DirectoryResponseRejectsExcessiveOutput()
    {
        var oversized = new string(
            'x',
            RemoteFileService.ResponseByteLimit + (64 * 1024) + 1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => RemoteFileService.ParseResponse(oversized));

        Assert.Contains("safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedStreamReaderSignalsAndDiscardsBeyondItsLimit()
    {
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var stream = new PipeStream();
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var read = RemoteFileService.ReadBoundedAsync(stream, 8, "stdout", signal);
        await stream.WriteAsync(bytes);
        Assert.Equal("stdout", await signal.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        stream.Dispose();
        var result = await read.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.LimitExceeded);
        Assert.Equal(bytes[..8], result.Content);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task BoundedStreamReaderSignalsAndCapturesReadFailure()
    {
        var stream = new MemoryStream();
        stream.Dispose();
        var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await RemoteFileService.ReadBoundedAsync(stream, 8, "stderr", signal);

        Assert.False(result.LimitExceeded);
        Assert.Empty(result.Content);
        Assert.IsType<ObjectDisposedException>(result.Error);
        Assert.Equal("stderr", await signal.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task PreCanceledCommandIsRejectedBeforeOutputReadersStart()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var execution = Task.FromCanceled(cancellation.Token);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RemoteFileService.RejectPreCanceledExecutionAsync(execution));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void RemoteRenameScriptEncodesPathsAndRefusesOverwrite()
    {
        const string source = "/home/user/中文 ' old.txt";
        const string newName = "新 name.txt";

        var script = RemoteFileService.BuildRenameScript(source, newName);

        Assert.DoesNotContain(source, script, StringComparison.Ordinal);
        Assert.DoesNotContain(newName, script, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source)), script, StringComparison.Ordinal);
        Assert.Contains("os.path.lexists(target)", script, StringComparison.Ordinal);
        Assert.Contains("os.rename(source, target)", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("folder/name")]
    public void RemoteRenameRejectsInvalidNames(string name)
    {
        Assert.Throws<ArgumentException>(() => RemoteFileService.BuildRenameScript("/tmp/source", name));
    }

    [Fact]
    public void RemoteDeleteScriptEncodesTargetsAndGuardsRoot()
    {
        string[] paths = ["/tmp/a file", "/tmp/中文-folder"];

        var script = RemoteFileService.BuildDeleteScript(paths);

        Assert.DoesNotContain(paths[0], script, StringComparison.Ordinal);
        Assert.DoesNotContain(paths[1], script, StringComparison.Ordinal);
        Assert.Contains("path == '/'", script, StringComparison.Ordinal);
        Assert.Contains("shutil.rmtree(path)", script, StringComparison.Ordinal);
        Assert.Contains("os.path.islink(path)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoteMonitoringUsesProcfsAndNvidiaSmiWithoutTop()
    {
        var script = RemoteMonitoringService.BuildSampleScript();

        Assert.Contains("/proc/stat", script, StringComparison.Ordinal);
        Assert.Contains("/proc/meminfo", script, StringComparison.Ordinal);
        Assert.Contains("/sys/class/net", script, StringComparison.Ordinal);
        Assert.Contains("/proc/self/mountinfo", script, StringComparison.Ordinal);
        Assert.Contains("os.statvfs('/')", script, StringComparison.Ordinal);
        Assert.Contains("nvidia-smi", script, StringComparison.Ordinal);
        Assert.DoesNotContain(" top ", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoteMonitoringParsesEightGpusAndCalculatesCpuAndNetworkRates()
    {
        var gpus = string.Join(',', Enumerable.Range(0, 8).Select(index =>
            $"{{\"index\":{index},\"coreUtilizationPercent\":{index * 10},\"memoryUsedBytes\":1073741824,\"memoryTotalBytes\":25769803776}}"));
        var output = RemoteMonitoringService.Marker + $$"""
            {"sampleMonotonicNanoseconds":3000000000,"cpuTotal":1200,"cpuIdle":450,"memoryTotalBytes":8589934592,"memoryAvailableBytes":4294967296,"diskTotalBytes":107374182400,"diskAvailableBytes":53687091200,"disks":[{"mountPoint":"/","source":"/dev/sda1","fileSystemType":"ext4","totalBytes":107374182400,"availableBytes":53687091200},{"mountPoint":"/data","source":"/dev/sdb1","fileSystemType":"xfs","totalBytes":214748364800,"availableBytes":161061273600}],"defaultNetworkInterface":"eth0","networkInterfaces":[{"name":"eth0","isUp":true,"receivedBytes":3072,"transmittedBytes":6144}],"gpus":[{{gpus}}]}
            """;
        var current = RemoteMonitoringService.ParseResponse(output);
        var previous = current with
        {
            SampleMonotonicNanoseconds = 1_000_000_000,
            CpuTotal = 1_000,
            CpuIdle = 400,
            NetworkInterfaces =
            [
                new RemoteNetworkInterfaceCounter
                {
                    Name = "eth0",
                    IsUp = true,
                    ReceivedBytes = 1_024,
                    TransmittedBytes = 2_048,
                },
            ],
        };

        var cpu = RemoteMonitoringService.CalculateCpuUtilization(previous, current);
        var network = RemoteMonitoringService.CalculateNetworkRate(previous, current, "eth0");

        Assert.Equal(8, current.Gpus.Count);
        Assert.Equal(["/", "/data"], current.Disks.Select(disk => disk.MountPoint));
        Assert.Equal(75, cpu, precision: 6);
        Assert.Equal(1_024, network.ReceivedBytesPerSecond, precision: 6);
        Assert.Equal(2_048, network.TransmittedBytesPerSecond, precision: 6);
    }

    [Fact]
    public void KnownHostStoreDistinguishesNewAlgorithmAndChangedKey()
    {
        var path = Path.Combine(Path.GetTempPath(), "RsyncShell.Tests", Guid.NewGuid().ToString("N"), "known_hosts.json");
        var store = new KnownHostStore(path);
        var ed25519 = new SshHostKeyInfo("server.example", 22, "ssh-ed25519", 256, "fingerprint-a");
        var ecdsa = new SshHostKeyInfo("server.example", 22, "ecdsa-sha2-nistp256", 256, "fingerprint-b");

        Assert.Equal(KnownHostStatus.Unknown, store.Check(ed25519, out _));
        store.Trust(ed25519);
        Assert.Equal(KnownHostStatus.Trusted, store.Check(ed25519, out _));
        Assert.Equal(KnownHostStatus.AdditionalAlgorithm, store.Check(ecdsa, out _));
        store.Trust(ecdsa);
        Assert.Equal(2, store.FindTrustedAll("SERVER.EXAMPLE", 22).Count);

        var changed = ed25519 with { FingerprintSha256 = "fingerprint-changed" };
        Assert.Equal(KnownHostStatus.Changed, store.Check(changed, out var existing));
        Assert.Equal("fingerprint-a", existing?.FingerprintSha256);
    }

    [Fact]
    public void RsyncTransfersEnableCompressionByDefault()
    {
        var request = new RsyncTransferRequest
        {
            Direction = RsyncTransferDirection.Upload,
            Profile = new ConnectionProfile
            {
                Id = "test",
                Name = "test",
                Host = "server.example",
                Username = "user",
            },
            LocalPath = @"C:\data",
            RemotePath = "/srv/data/",
        };

        Assert.True(request.Compress);
        Assert.False(request.ExactDestination);
    }

    [Fact]
    public void RsyncWorkerMessageCarriesExplicitExactDestinationMode()
    {
        var request = new RsyncTransferRequest
        {
            Direction = RsyncTransferDirection.Download,
            Profile = new ConnectionProfile
            {
                Id = "test",
                Name = "test",
                Host = "server.example",
                Username = "user",
            },
            LocalPath = @"C:\Downloads\renamed.txt",
            RemotePath = "/srv/original.txt",
            ExactDestination = true,
        };
        var authentication = new SshAuthenticationOptions
        {
            Kind = SshAuthenticationKind.Password,
            Password = "not-serialized",
        };

        var message = RsyncWorkerTransferService.BuildTransferMessage("request-test", request, authentication);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message));
        var transfer = document.RootElement.GetProperty("transfer");

        Assert.True(transfer.GetProperty("ExactDestination").GetBoolean());
        Assert.False(transfer.GetProperty("CopyContents").GetBoolean());
    }

    [Fact]
    public void NetworkDiscoveryFiltersContainerAndLoopbackInterfaces()
    {
        var script = RemoteNetworkDiscoveryService.BuildInventoryScript();

        Assert.Contains("'docker'", script, StringComparison.Ordinal);
        Assert.Contains("'veth'", script, StringComparison.Ordinal);
        Assert.Contains("'cni'", script, StringComparison.Ordinal);
        Assert.Contains("'flannel'", script, StringComparison.Ordinal);
        Assert.Contains("'cali'", script, StringComparison.Ordinal);
        Assert.Contains("'cilium'", script, StringComparison.Ordinal);
        Assert.Contains("lowered == 'lo'", script, StringComparison.Ordinal);
        Assert.Contains("10.0.0.0/8", script, StringComparison.Ordinal);
        Assert.Contains("172.16.0.0/12", script, StringComparison.Ordinal);
        Assert.Contains("192.168.0.0/16", script, StringComparison.Ordinal);
        Assert.Contains("SSH_CONNECTION", script, StringComparison.Ordinal);
        Assert.Contains("['ip', '-j', 'address', 'show']", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkDiscoveryParsesInterfaceAndActualSshPort()
    {
        var response = RemoteNetworkDiscoveryService.Marker +
                       "{\"hostName\":\"node-a\",\"sshLocalAddress\":\"10.0.0.11\",\"sshLocalPort\":22," +
                       "\"addresses\":[{\"interfaceName\":\"eno1\",\"address\":\"10.0.0.11\"," +
                       "\"addressFamily\":4,\"prefixLength\":24}]}";

        var inventory = RemoteNetworkDiscoveryService.ParseResponse(response);

        Assert.Equal("node-a", inventory.HostName);
        Assert.Equal(22, inventory.SshLocalPort);
        var address = Assert.Single(inventory.Addresses);
        Assert.Equal("eno1", address.InterfaceName);
        Assert.Equal("10.0.0.11", address.Address);
    }
}
