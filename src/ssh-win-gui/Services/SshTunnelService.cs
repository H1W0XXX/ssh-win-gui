using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Renci.SshNet;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;
using Tmds.Ssh;

namespace RsyncShell.App.Services;

public sealed class SshTunnelService : IDisposable
{
    public ObservableCollection<SshTunnelSession> Sessions { get; } = [];

    public async Task<SshTunnelSession> StartAsync(
        SshTunnelDefinition definition,
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        var session = new SshTunnelSession(definition);
        Sessions.Insert(0, session);
        await session.StartAsync(authentication, route, verifyHostKey).ConfigureAwait(false);
        return session;
    }

    public void StopAll()
    {
        foreach (var session in Sessions.ToArray()) session.Stop();
    }

    public void Dispose()
    {
        StopAll();
        foreach (var session in Sessions.ToArray()) session.Dispose();
    }
}

public sealed class SshTunnelSession : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<string> _logs = [];
    private SshClientSession? _sshNetSession;
    private ForwardedPort? _sshNetForward;
    private Tmds.Ssh.SshClient? _tmdsClient;
    private IDisposable? _tmdsForward;
    private Tmds.Ssh.RemoteListener? _remoteListener;
    private Task? _remoteSocksLoop;
    private int _stopRequested;
    private string _status = LocalizationService.Get("TunnelStarting");
    private bool _isRunning;

    public SshTunnelSession(SshTunnelDefinition definition)
    {
        Definition = definition;
        StartedAt = DateTimeOffset.Now;
        AppendLog($"[{StartedAt:yyyy-MM-dd HH:mm:ss}] {Describe(definition)}");
    }

    public SshTunnelDefinition Definition { get; }
    public string Id => Definition.Id;
    public string SessionName => Definition.Profile.Name;
    public string Mode => Definition.Kind switch
    {
        SshTunnelKind.LocalForward => LocalizationService.Get("TunnelLocalForward"),
        SshTunnelKind.RemoteForward => LocalizationService.Get("TunnelRemoteForward"),
        SshTunnelKind.LocalSocks5 => LocalizationService.Get("TunnelLocalSocks5"),
        SshTunnelKind.RemoteSocks5 => LocalizationService.Get("TunnelRemoteSocks5"),
        _ => Definition.Kind.ToString(),
    };
    public string Route => Definition.Target is null
        ? Definition.Listen.ToString()
        : $"{Definition.Listen} -> {Definition.Target}";
    public DateTimeOffset StartedAt { get; }
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool IsRunning { get => _isRunning; private set => SetField(ref _isRunning, value); }
    public string LogText { get { lock (_gate) return string.Join(Environment.NewLine, _logs); } }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal async Task StartAsync(
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        try
        {
            if (Definition.Profile.ProxyKind == SshProxyKind.Socks5 &&
                Definition.Kind != SshTunnelKind.RemoteSocks5)
            {
                await StartWithSshNetAsync(authentication, route, verifyHostKey).ConfigureAwait(false);
            }
            else
            {
                if (Definition.Profile.ProxyKind == SshProxyKind.Socks5)
                    throw new NotSupportedException("Remote SOCKS5 cannot be nested through an upstream SOCKS5 proxy.");
                await StartWithTmdsAsync(authentication, route, verifyHostKey).ConfigureAwait(false);
            }

            IsRunning = true;
            Status = LocalizationService.Get("TunnelRunning");
            AppendLog($"[{DateTimeOffset.Now:HH:mm:ss}] Started successfully.");
        }
        catch (Exception ex)
        {
            Status = LocalizationService.Get("TunnelFailed");
            AppendLog($"[{DateTimeOffset.Now:HH:mm:ss}] ERROR: {Sanitize(ex)}");
            _lifetime.Cancel();
            ReleaseResources();
            Interlocked.Exchange(ref _stopRequested, 1);
            throw;
        }
    }

    private async Task StartWithSshNetAsync(
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        _sshNetSession = await new SshClientFactory().ConnectAsync(
            Definition.Profile, authentication, verifyHostKey, route, _lifetime.Token).ConfigureAwait(false);
        var listen = Definition.Listen;
        var target = Definition.Target;
        _sshNetForward = Definition.Kind switch
        {
            SshTunnelKind.LocalForward => new ForwardedPortLocal(listen.Host, (uint)listen.Port,
                target!.Host, (uint)target.Port),
            SshTunnelKind.RemoteForward => new ForwardedPortRemote(listen.Host, (uint)listen.Port,
                target!.Host, (uint)target.Port),
            SshTunnelKind.LocalSocks5 => new ForwardedPortDynamic(listen.Host, (uint)listen.Port),
            _ => throw new NotSupportedException("This tunnel mode is not supported by SSH.NET."),
        };
        _sshNetSession.Client.AddForwardedPort(_sshNetForward);
        _sshNetForward.Start();
    }

    private async Task StartWithTmdsAsync(
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        _tmdsClient = JumpSshClientFactory.CreateForRoute(
            Definition.Profile, authentication, route, verifyHostKey);
        await _tmdsClient.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
        var listen = Definition.Listen;
        var target = Definition.Target;
        switch (Definition.Kind)
        {
            case SshTunnelKind.LocalForward:
                _tmdsForward = await _tmdsClient.StartForwardAsync(
                    ResolveBindEndpoint(listen), new RemoteHostEndPoint(target!.Host, target.Port),
                    _lifetime.Token).ConfigureAwait(false);
                break;
            case SshTunnelKind.RemoteForward:
                _tmdsForward = await _tmdsClient.StartRemoteForwardAsync(
                    new RemoteIPListenEndPoint(listen.Host, listen.Port), ResolveLocalEndpoint(target!),
                    _lifetime.Token).ConfigureAwait(false);
                break;
            case SshTunnelKind.LocalSocks5:
                _tmdsForward = _tmdsClient.StartSocksForward(ResolveBindEndpoint(listen), _lifetime.Token);
                break;
            case SshTunnelKind.RemoteSocks5:
                _remoteListener = await _tmdsClient.ListenTcpAsync(
                    listen.Host, listen.Port, _lifetime.Token).ConfigureAwait(false);
                _remoteSocksLoop = RunRemoteSocksLoopAsync(_tmdsClient, _remoteListener, _lifetime.Token);
                break;
        }
    }

    private static EndPoint ResolveLocalEndpoint(TunnelEndpoint endpoint)
    {
        if (IPAddress.TryParse(endpoint.Host, out var address)) return new IPEndPoint(address, endpoint.Port);
        return new DnsEndPoint(endpoint.Host, endpoint.Port);
    }

    private static EndPoint ResolveBindEndpoint(TunnelEndpoint endpoint)
    {
        if (IPAddress.TryParse(endpoint.Host, out var address)) return new IPEndPoint(address, endpoint.Port);
        if (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        var addresses = Dns.GetHostAddresses(endpoint.Host);
        var selected = addresses.FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork)
                       ?? addresses.FirstOrDefault()
                       ?? throw new InvalidOperationException($"Unable to resolve listen host '{endpoint.Host}'.");
        return new IPEndPoint(selected, endpoint.Port);
    }

    private async Task RunRemoteSocksLoopAsync(
        Tmds.Ssh.SshClient client,
        Tmds.Ssh.RemoteListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                if (!connection.HasStream) break;
                var stream = connection.MoveStream();
                _ = HandleRemoteSocksClientAsync(client, stream, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Status = LocalizationService.Get("TunnelFailed");
            IsRunning = false;
            AppendLog($"[{DateTimeOffset.Now:HH:mm:ss}] ERROR: remote SOCKS5 listener stopped: {Sanitize(ex)}");
            _lifetime.Cancel();
            ReleaseResources();
            Interlocked.Exchange(ref _stopRequested, 1);
        }
    }

    private async Task HandleRemoteSocksClientAsync(
        Tmds.Ssh.SshClient client,
        Stream clientStream,
        CancellationToken cancellationToken)
    {
        await using var inbound = clientStream;
        try
        {
            var header = new byte[2];
            await ReadExactlyAsync(inbound, header, cancellationToken).ConfigureAwait(false);
            if (header[0] != 5) throw new InvalidDataException("Only SOCKS5 is supported.");
            var methods = new byte[header[1]];
            await ReadExactlyAsync(inbound, methods, cancellationToken).ConfigureAwait(false);
            if (!methods.Contains((byte)0))
            {
                await inbound.WriteAsync(new byte[] { 5, 255 }, cancellationToken).ConfigureAwait(false);
                return;
            }
            await inbound.WriteAsync(new byte[] { 5, 0 }, cancellationToken).ConfigureAwait(false);

            var request = new byte[4];
            await ReadExactlyAsync(inbound, request, cancellationToken).ConfigureAwait(false);
            if (request[0] != 5 || request[1] != 1) throw new InvalidDataException("Only SOCKS5 CONNECT is supported.");
            var host = request[3] switch
            {
                1 => new IPAddress(await ReadBytesAsync(inbound, 4, cancellationToken).ConfigureAwait(false)).ToString(),
                3 => System.Text.Encoding.UTF8.GetString(await ReadBytesAsync(inbound,
                    (await ReadBytesAsync(inbound, 1, cancellationToken).ConfigureAwait(false))[0], cancellationToken).ConfigureAwait(false)),
                4 => new IPAddress(await ReadBytesAsync(inbound, 16, cancellationToken).ConfigureAwait(false)).ToString(),
                _ => throw new InvalidDataException("Unsupported SOCKS5 address type."),
            };
            var portBytes = await ReadBytesAsync(inbound, 2, cancellationToken).ConfigureAwait(false);
            var port = (portBytes[0] << 8) | portBytes[1];
            await using var outbound = await client.OpenTcpConnectionAsync(host, port, cancellationToken).ConfigureAwait(false);
            await inbound.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken).ConfigureAwait(false);
            await inbound.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAny(
                inbound.CopyToAsync(outbound, cancellationToken),
                outbound.CopyToAsync(inbound, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppendLog($"[{DateTimeOffset.Now:HH:mm:ss}] SOCKS5 connection failed: {Sanitize(ex)}");
            try { await inbound.WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 }, cancellationToken).ConfigureAwait(false); }
            catch { }
        }
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("SOCKS5 client closed the connection.");
            offset += read;
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        _lifetime.Cancel();
        ReleaseResources();
        IsRunning = false;
        Status = LocalizationService.Get("TunnelStopped");
        AppendLog($"[{DateTimeOffset.Now:HH:mm:ss}] Stopped.");
    }

    private void ReleaseResources()
    {
        try { _sshNetForward?.Stop(); } catch { }
        try { _remoteListener?.Stop(); } catch { }
        _sshNetForward?.Dispose();
        _sshNetForward = null;
        _sshNetSession?.Dispose();
        _sshNetSession = null;
        _remoteListener?.Dispose();
        _remoteListener = null;
        _tmdsForward?.Dispose();
        _tmdsForward = null;
        _tmdsClient?.Dispose();
        _tmdsClient = null;
    }

    public void Dispose()
    {
        Stop();
        _lifetime.Dispose();
    }

    private void AppendLog(string message)
    {
        lock (_gate)
        {
            _logs.Add(message);
            if (_logs.Count > 1000) _logs.RemoveRange(0, _logs.Count - 1000);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogText)));
    }

    private static string Describe(SshTunnelDefinition definition) =>
        $"{definition.Profile.DisplayEndpoint} | {definition.Kind} | " +
        (definition.Target is null ? definition.Listen : $"{definition.Listen} -> {definition.Target}");

    private static string Sanitize(Exception ex) => ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
