using System.Net;
using System.Net.Sockets;
using System.Text;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;
using Tmds.Ssh;

namespace RsyncShell.App.Tests;

[Collection("SSH tunnel live tests")]
public sealed class SshTunnelLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task FourTunnelModesWorkAcrossDirectAndJumpSessions_WhenConfigured()
    {
        var keyPath = Environment.GetEnvironmentVariable("SSH_TUNNEL_TEST_KEY");
        if (string.IsNullOrWhiteSpace(keyPath)) return;

        var direct = Profile("oracle-1", Environment.GetEnvironmentVariable("SSH_TUNNEL_TEST_HOST1")!, keyPath);
        var jump = direct with { Id = "jump", Name = "oracle-jump" };
        var target = Profile("oracle-2", Environment.GetEnvironmentVariable("SSH_TUNNEL_TEST_HOST2")!, keyPath) with
        {
            ProxyKind = SshProxyKind.JumpHost,
            JumpProfileId = jump.Id,
        };
        Assert.False(string.IsNullOrWhiteSpace(direct.Host));
        Assert.False(string.IsNullOrWhiteSpace(target.Host));

        var authentication = new SshAuthenticationOptions
        {
            Kind = SshAuthenticationKind.PrivateKey,
            PrivateKeyPath = keyPath,
        };
        using var service = new SshTunnelService();

        var localForwardPort = FreeLocalPort();
        var localForward = await service.StartAsync(Definition(direct, SshTunnelKind.LocalForward,
            localForwardPort, new TunnelEndpoint("127.0.0.1", 22)), authentication, [direct], _ => true);
        Assert.StartsWith("SSH-", await ReadBannerAsync("127.0.0.1", localForwardPort));
        localForward.Stop();

        var localSocksPort = FreeLocalPort();
        var localSocks = await service.StartAsync(Definition(direct, SshTunnelKind.LocalSocks5,
            localSocksPort, null), authentication, [direct], _ => true);
        Assert.StartsWith("SSH-", await ReadThroughSocksAsync(localSocksPort, "127.0.0.1", 22));
        localSocks.Stop();

        var remoteForwardPort = await FindRemotePortAsync(direct, authentication, [direct]);
        var localTargetPort = FreeLocalPort();
        using var localServer = StartOneShotServer(localTargetPort, "forward-ok");
        var remoteForward = await service.StartAsync(Definition(direct, SshTunnelKind.RemoteForward,
            remoteForwardPort, new TunnelEndpoint("127.0.0.1", localTargetPort)), authentication, [direct], _ => true);
        Assert.Equal("forward-ok", await ExecuteAsync(direct, authentication, [direct],
            $"python3 -c \"import socket; s=socket.create_connection(('127.0.0.1',{remoteForwardPort}),5); print(s.recv(32).decode())\""));
        remoteForward.Stop();

        var jumpRoute = new[] { target, jump };
        var remoteSocksPort = await FindRemotePortAsync(target, authentication, jumpRoute);
        var remoteSocks = await service.StartAsync(Definition(target, SshTunnelKind.RemoteSocks5,
            remoteSocksPort, null), authentication, jumpRoute, _ => true);
        var script = "import socket; s=socket.create_connection(('127.0.0.1',PORT),5); " +
                     "s.sendall(bytes([5,1,0])); assert s.recv(2)==bytes([5,0]); " +
                     "s.sendall(bytes([5,1,0,1,127,0,0,1,0,22])); assert s.recv(10)[1]==0; " +
                     "print(s.recv(64).decode().strip())";
        Assert.StartsWith("SSH-", await ExecuteAsync(target, authentication, jumpRoute,
            $"python3 -c \"{script.Replace("PORT", remoteSocksPort.ToString())}\""));
        remoteSocks.Stop();
    }

    private static ConnectionProfile Profile(string id, string host, string keyPath) => new()
    {
        Id = id,
        Name = id,
        Host = host,
        Port = 22,
        Username = "ubuntu",
        PrivateKeyPath = keyPath,
    };

    private static SshTunnelDefinition Definition(
        ConnectionProfile profile, SshTunnelKind kind, int listenPort, TunnelEndpoint? target) =>
        new(Guid.NewGuid().ToString("N"), profile, kind,
            new TunnelEndpoint("127.0.0.1", listenPort), target);

    private static int FreeLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IDisposable StartOneShotServer(int port, string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(response));
            listener.Stop();
        });
        return listener;
    }

    private static async Task<string> ReadBannerAsync(string host, int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port).WaitAsync(TimeSpan.FromSeconds(10));
        var bytes = new byte[128];
        var read = await client.GetStream().ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.ASCII.GetString(bytes, 0, read);
    }

    private static async Task<string> ReadThroughSocksAsync(int proxyPort, string host, int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort).WaitAsync(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var response = new byte[2];
        await stream.ReadExactlyAsync(response);
        Assert.Equal(new byte[] { 5, 0 }, response);
        var address = IPAddress.Parse(host).GetAddressBytes();
        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, address[0], address[1], address[2], address[3],
            (byte)(port >> 8), (byte)port });
        var connect = new byte[10];
        await stream.ReadExactlyAsync(connect);
        Assert.Equal(0, connect[1]);
        var banner = new byte[128];
        var read = await stream.ReadAsync(banner).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return Encoding.ASCII.GetString(banner, 0, read);
    }

    private static async Task<int> FindRemotePortAsync(
        ConnectionProfile profile, SshAuthenticationOptions authentication, IReadOnlyList<ConnectionProfile> route)
    {
        var output = await ExecuteAsync(profile, authentication, route,
            "python3 -c \"import socket; s=socket.socket(); s.bind(('127.0.0.1',0)); print(s.getsockname()[1]); s.close()\"");
        return int.Parse(output);
    }

    private static async Task<string> ExecuteAsync(
        ConnectionProfile profile, SshAuthenticationOptions authentication, IReadOnlyList<ConnectionProfile> route,
        string command)
    {
        using var client = JumpSshClientFactory.CreateForRoute(profile, authentication, route, _ => true);
        await client.ConnectAsync();
        using var process = await client.ExecuteAsync(command);
        process.WriteEof();
        var output = new StringBuilder();
        while (true)
        {
            var (_, line) = await process.ReadLineAsync();
            if (line is null) break;
            output.AppendLine(line);
        }
        Assert.Equal(0, await process.GetExitCodeAsync());
        return output.ToString().Trim();
    }
}

[CollectionDefinition("SSH tunnel live tests", DisableParallelization = true)]
public sealed class SshTunnelLiveTestCollection;
