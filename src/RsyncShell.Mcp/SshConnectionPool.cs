using System.Globalization;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.Mcp;

/// <summary>A resolved session: the target, its jump route, and the expanded private-key path.</summary>
internal sealed record SshTarget(
    ConnectionProfile Profile,
    IReadOnlyList<ConnectionProfile> Route,
    string PrivateKeyPath)
{
    /// <summary>
    /// Identifies the connection by everything that affects where it lands, so
    /// editing a session in the GUI yields a fresh connection instead of a stale one.
    /// </summary>
    public string PoolKey { get; } = string.Join('\n', Route.Select(hop => string.Join(
        '|',
        hop.Id,
        hop.Host,
        hop.Port.ToString(CultureInfo.InvariantCulture),
        hop.Username,
        hop.PrivateKeyPath,
        hop.ProxyKind.ToString(),
        hop.ProxyHost,
        hop.ProxyPort.ToString(CultureInfo.InvariantCulture),
        hop.JumpProfileId)));
}

/// <summary>Thrown when a host key is not trusted; the message is safe to show to the model.</summary>
internal sealed class HostKeyRejectedException(string message, Exception inner)
    : InvalidOperationException(message, inner);

/// <summary>The exec channel could not be opened, so nothing was sent and a retry is safe.</summary>
internal sealed class ChannelOpenException(Exception inner) : Exception(inner.Message, inner);

internal interface ISshConnection : IDisposable
{
    bool IsAlive { get; }

    Task<int> ExecuteAsync(
        string command,
        ReadOnlyMemory<byte> stdin,
        IOutputSink stdout,
        IOutputSink stderr,
        CancellationToken cancellationToken);
}

/// <summary>
/// Keeps one authenticated SSH connection (one TCP stream) per session and runs
/// every tool call as a new exec channel on it. Idle connections are closed
/// after a timeout and the total is capped, so many calls never mean many sockets.
/// </summary>
internal sealed class SshConnectionPool : IDisposable
{
    /// <summary>
    /// OpenSSH allows 10 sessions per connection by default. The server frees a
    /// session slightly after the client sees its command finish, so stay well below.
    /// </summary>
    internal const int MaximumChannelsPerConnection = 6;
    internal const int DefaultMaximumConnections = 64;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<SshTarget, CancellationToken, Task<ISshConnection>> _connect;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _channelRetryWindow;
    private readonly int _maximumConnections;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Timer? _sweeper;
    private bool _disposed;

    public SshConnectionPool(
        Func<KnownHostStore>? knownHosts = null,
        TimeSpan? idleTimeout = null,
        int maximumConnections = DefaultMaximumConnections,
        Func<SshTarget, CancellationToken, Task<ISshConnection>>? connect = null,
        TimeSpan? channelRetryWindow = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConnections);
        var stores = knownHosts ?? (() => new KnownHostStore());
        _connect = connect ?? ((target, cancellationToken) => ConnectAsync(target, stores, cancellationToken));
        _idleTimeout = idleTimeout ?? ReadIdleTimeout();
        _channelRetryWindow = channelRetryWindow ?? TimeSpan.FromSeconds(5);
        _maximumConnections = maximumConnections;
        if (_idleTimeout > TimeSpan.Zero)
        {
            var period = TimeSpan.FromSeconds(Math.Clamp(_idleTimeout.TotalSeconds / 4, 1, 30));
            _sweeper = new Timer(_ => CloseIdleConnections(), null, period, period);
        }
    }

    public static SshConnectionPool Shared { get; } = new();

    internal int ConnectionCount
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public async Task<int> ExecuteAsync(
        SshTarget target,
        string command,
        ReadOnlyMemory<byte> stdin,
        IOutputSink stdout,
        IOutputSink stderr,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var lease = await AcquireAsync(target, cancellationToken).ConfigureAwait(false);
            var retire = false;
            try
            {
                return await OpenChannelAndExecuteAsync(
                    lease, command, stdin, stdout, stderr, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelOpenException error)
            {
                // Nothing reached the remote shell, so a connection that went stale
                // while idle can be replaced transparently, once.
                retire = true;
                if (lease.Reused && attempt == 0)
                    continue;
                throw new IOException(error.Message, error.InnerException);
            }
            catch
            {
                retire = !lease.Connection.IsAlive;
                throw;
            }
            finally
            {
                Release(lease.Entry, retire);
            }
        }
    }

    /// <summary>
    /// Runs one command in a channel slot of the leased connection. A live
    /// connection that refuses a channel is at the server's session limit, so the
    /// call waits on this connection; replacing it would only open more sockets.
    /// </summary>
    private async Task<int> OpenChannelAndExecuteAsync(
        Lease lease,
        string command,
        ReadOnlyMemory<byte> stdin,
        IOutputSink stdout,
        IOutputSink stderr,
        CancellationToken cancellationToken)
    {
        var entry = lease.Entry;
        var retryBudget = (long)_channelRetryWindow.TotalMilliseconds;
        var delay = 50;
        while (true)
        {
            await entry.Channels.WaitAsync(cancellationToken).ConfigureAwait(false);
            var forfeited = false;
            try
            {
                return await lease.Connection.ExecuteAsync(command, stdin, stdout, stderr, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelOpenException) when (lease.Connection.IsAlive)
            {
                lock (_gate)
                {
                    // Refused while other commands hold slots: the server allows fewer
                    // sessions than assumed (sshd MaxSessions). Give this slot up for
                    // good and queue for one of the remaining slots.
                    var others = entry.ChannelLimit - entry.Channels.CurrentCount - 1;
                    if (others >= 1 && entry.ChannelLimit > others)
                    {
                        entry.ChannelLimit--;
                        forfeited = true;
                    }
                }
                if (!forfeited)
                {
                    // Refused with nothing else running: sessions that just ended are
                    // still being torn down. Wait briefly, but not forever.
                    if (retryBudget <= 0)
                        throw;
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    retryBudget -= delay;
                    delay = Math.Min(delay * 2, 500);
                }
            }
            finally
            {
                if (!forfeited)
                    entry.Channels.Release();
            }
        }
    }

    private async Task<Lease> AcquireAsync(SshTarget target, CancellationToken cancellationToken)
    {
        while (true)
        {
            Entry entry;
            bool reused;
            List<Entry>? evicted = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                reused = _entries.TryGetValue(target.PoolKey, out entry!);
                if (!reused)
                {
                    evicted = MakeRoomLocked();
                    entry = new Entry(target.PoolKey);
                    _entries[target.PoolKey] = entry;
                    var created = entry;
                    entry.Connection = Task.Run(() => OpenAsync(target, created));
                }
                entry.Leases++;
            }
            evicted?.ForEach(CloseWhenConnected);

            ISshConnection connection;
            try
            {
                connection = await entry.Connection!.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A failed connect retires its own entry; a cancelled wait just lets go.
                Release(entry, retire: false);
                throw;
            }

            if (!connection.IsAlive)
            {
                Release(entry, retire: true);
                continue;
            }
            return new Lease(entry, connection, reused);
        }
    }

    private async Task<ISshConnection> OpenAsync(SshTarget target, Entry entry)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            return await _connect(target, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Drop the entry so the next call reconnects instead of replaying this failure.
            lock (_gate)
                RetireLocked(entry);
            if (error is OperationCanceledException && !_shutdown.IsCancellationRequested)
                throw new TimeoutException(
                    $"Connecting to session '{target.Profile.Name}' timed out after {ConnectTimeout.TotalSeconds:0} seconds.");
            throw;
        }
    }

    private void Release(Entry entry, bool retire)
    {
        var close = false;
        lock (_gate)
        {
            entry.Leases--;
            entry.LastUsed = Environment.TickCount64;
            if (retire || (_idleTimeout <= TimeSpan.Zero && entry.Leases == 0))
                RetireLocked(entry);
            if (entry.Retired && entry.Leases == 0 && !entry.Closed)
                close = entry.Closed = true;
        }
        if (close)
            CloseWhenConnected(entry);
    }

    /// <summary>Removes the entry from the pool; it is closed once its last call finishes.</summary>
    private void RetireLocked(Entry entry)
    {
        entry.Retired = true;
        if (_entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
            _entries.Remove(entry.Key);
    }

    private List<Entry>? MakeRoomLocked()
    {
        if (_entries.Count < _maximumConnections)
            return null;
        var oldest = _entries.Values
            .Where(entry => entry.Leases == 0)
            .OrderBy(entry => entry.LastUsed)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"All {_maximumConnections} pooled SSH connections are busy. Wait for running calls to finish.");
        RetireLocked(oldest);
        oldest.Closed = true;
        return [oldest];
    }

    private void CloseIdleConnections()
    {
        List<Entry> idle;
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = Environment.TickCount64;
            idle = _entries.Values
                .Where(entry => entry.Leases == 0 && now - entry.LastUsed >= _idleTimeout.TotalMilliseconds)
                .ToList();
            foreach (var entry in idle)
            {
                RetireLocked(entry);
                entry.Closed = true;
            }
        }
        idle.ForEach(CloseWhenConnected);
    }

    private static void CloseWhenConnected(Entry entry) =>
        entry.Connection!.ContinueWith(
            static connect =>
            {
                if (connect.IsCompletedSuccessfully)
                    connect.Result.Dispose();
                else
                    _ = connect.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public void Dispose()
    {
        List<Entry> entries;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
            foreach (var entry in entries)
                entry.Retired = entry.Closed = true;
        }
        _sweeper?.Dispose();
        _shutdown.Cancel();
        entries.ForEach(CloseWhenConnected);
    }

    private static TimeSpan ReadIdleTimeout()
    {
        var configured = Environment.GetEnvironmentVariable("SSH_WIN_GUI_MCP_IDLE_SECONDS");
        return int.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 3600))
            : TimeSpan.FromMinutes(10);
    }

    private static async Task<ISshConnection> ConnectAsync(
        SshTarget target,
        Func<KnownHostStore> knownHosts,
        CancellationToken cancellationToken)
    {
        var authentication = new SshAuthenticationOptions
        {
            Kind = SshAuthenticationKind.PrivateKey,
            PrivateKeyPath = target.PrivateKeyPath,
        };
        // A fresh store per connect picks up fingerprints approved in the GUI meanwhile.
        var verifier = new StrictHostKeyVerifier(knownHosts());
        try
        {
            if (target.Profile.ProxyKind == SshProxyKind.JumpHost)
            {
                var client = JumpSshClientFactory.Create(
                    target.Profile, authentication, target.Route, verifier.Verify);
                try
                {
                    await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                return new NativeConnection(client);
            }

            return new SshNetConnection(await new SshClientFactory().ConnectAsync(
                target.Profile, authentication, verifier.Verify, target.Route, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception error) when (verifier.Failure is not null && error is not OperationCanceledException)
        {
            throw new HostKeyRejectedException(verifier.Failure, error);
        }
    }

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public SemaphoreSlim Channels { get; } = new(MaximumChannelsPerConnection);

        /// <summary>Slots still in circulation; shrinks when the server proves to allow fewer.</summary>
        public int ChannelLimit { get; set; } = MaximumChannelsPerConnection;
        public Task<ISshConnection>? Connection { get; set; }
        public int Leases { get; set; }
        public long LastUsed { get; set; } = Environment.TickCount64;
        public bool Retired { get; set; }
        public bool Closed { get; set; }
    }

    private readonly record struct Lease(Entry Entry, ISshConnection Connection, bool Reused);

    private sealed class StrictHostKeyVerifier(KnownHostStore store)
    {
        private string? _failure;

        public string? Failure => Volatile.Read(ref _failure);

        public bool Verify(SshHostKeyInfo key)
        {
            var status = store.Check(key, out _);
            if (status == KnownHostStatus.Trusted)
                return true;

            Volatile.Write(ref _failure, status switch
            {
                KnownHostStatus.Changed =>
                    $"Refused SSH session because the host key changed for {key.Host}:{key.Port}. Open ssh-win-gui and verify the new fingerprint manually.",
                KnownHostStatus.AdditionalAlgorithm =>
                    $"Refused an unapproved SSH host-key algorithm for {key.Host}:{key.Port}. Open ssh-win-gui and approve it manually.",
                _ =>
                    $"Refused unknown SSH host key for {key.Host}:{key.Port}. Connect once in ssh-win-gui and approve the fingerprint manually.",
            });
            return false;
        }
    }

    /// <summary>SSH.NET connection, used for direct and SOCKS5 sessions.</summary>
    private sealed class SshNetConnection(SshClientSession session) : ISshConnection
    {
        public bool IsAlive
        {
            get
            {
                try
                {
                    return session.Client.IsConnected;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public async Task<int> ExecuteAsync(
            string commandText,
            ReadOnlyMemory<byte> stdin,
            IOutputSink stdout,
            IOutputSink stderr,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Renci.SshNet.SshCommand command;
            Task execution;
            try
            {
                command = session.Client.CreateCommand(commandText);
                try
                {
                    // SSH.NET opens the channel synchronously inside ExecuteAsync.
                    execution = await Task.Factory.StartNew(
                        () => command.ExecuteAsync(cancellationToken),
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        TaskScheduler.Default).ConfigureAwait(false);
                }
                catch
                {
                    command.Dispose();
                    throw;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new ChannelOpenException(error);
            }

            Task[] transfers = [];
            try
            {
                // SSH.NET returns a cancelled task before it creates the output
                // streams; reading them in that state would never finish.
                if (execution.IsCanceled)
                    await execution.ConfigureAwait(false);

                // The pipe streams block, so each gets a dedicated thread rather
                // than starving the thread pool during multi-host runs.
                var output = command.OutputStream;
                var errorOutput = command.ExtendedOutputStream;
                transfers =
                [
                    RunBlocking(() => Pump(output, stdout)),
                    RunBlocking(() => Pump(errorOutput, stderr)),
                    RunBlocking(() =>
                    {
                        using var input = command.CreateInputStream();
                        if (!stdin.IsEmpty)
                            input.Write(stdin.Span);
                    }),
                ];
                await execution.ConfigureAwait(false);
                await Task.WhenAll(transfers[0], transfers[1]).ConfigureAwait(false);
                return command.ExitStatus ?? -1;
            }
            finally
            {
                // Closing the channel unblocks any transfer that is still waiting.
                command.Dispose();
                try
                {
                    await Task.WhenAll(transfers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch
                {
                    // A process that exits without reading stdin fails the input
                    // transfer; the exit status already tells that story.
                }
            }
        }

        private static Task RunBlocking(Action action) => Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        private static void Pump(Stream stream, IOutputSink sink)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    sink.Append(buffer.AsSpan(0, read));
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => session.Dispose();
    }

    /// <summary>Tmds.Ssh connection, used for jump-host routes.</summary>
    private sealed class NativeConnection(Tmds.Ssh.SshClient client) : ISshConnection
    {
        public bool IsAlive => !client.Disconnected.IsCancellationRequested;

        public async Task<int> ExecuteAsync(
            string commandText,
            ReadOnlyMemory<byte> stdin,
            IOutputSink stdout,
            IOutputSink stderr,
            CancellationToken cancellationToken)
        {
            Tmds.Ssh.RemoteProcess process;
            try
            {
                process = await client.ExecuteAsync(commandText, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new ChannelOpenException(error);
            }

            using (process)
            {
                // Write and read concurrently so a large stdin cannot deadlock
                // against a process that is blocked writing output.
                var input = WriteInputAsync(process, stdin, cancellationToken);
                _ = input.ContinueWith(
                    static write => _ = write.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                var stdoutBuffer = new byte[16 * 1024];
                var stderrBuffer = new byte[16 * 1024];
                while (true)
                {
                    var (isError, read) = await process.ReadAsync(
                        stdoutBuffer, stderrBuffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (isError)
                        stderr.Append(stderrBuffer.AsSpan(0, read));
                    else
                        stdout.Append(stdoutBuffer.AsSpan(0, read));
                }
                return await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task WriteInputAsync(
            Tmds.Ssh.RemoteProcess process,
            ReadOnlyMemory<byte> stdin,
            CancellationToken cancellationToken)
        {
            if (!stdin.IsEmpty)
                await process.WriteAsync(stdin, cancellationToken).ConfigureAwait(false);
            process.WriteEof();
        }

        public void Dispose() => client.Dispose();
    }
}
