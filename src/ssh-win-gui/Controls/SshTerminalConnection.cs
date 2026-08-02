using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;
using Renci.SshNet;
using Renci.SshNet.Common;
using Tmds.Ssh;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.App.Controls;

public sealed class SshTerminalConnection : ITerminalConnection, IDisposable
{
    private const int InputChunkCharacters = 4096;
    private const int InputQueueCapacity = 512;
    private readonly ConnectionProfile _profile;
    private readonly SshAuthenticationOptions _authentication;
    private readonly Func<SshHostKeyInfo, bool> _verifyHostKey;
    private readonly IReadOnlyList<ConnectionProfile> _route;
    private readonly Dispatcher _dispatcher;
    private readonly string _connectingMessage;
    private readonly string _connectedMessage;
    private readonly string _closedMessage;
    private readonly SshClientFactory _clientFactory = new();
    private readonly TerminalKeywordHighlighter _keywordHighlighter = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<string> _input = Channel.CreateBounded<string>(new BoundedChannelOptions(InputQueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly object _resourceGate = new();
    private readonly TaskCompletionSource _rendererAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SshClientSession? _session;
    private ShellStream? _shell;
    private Tmds.Ssh.SshClient? _jumpClient;
    private RemoteProcess? _jumpProcess;
    private uint _rows = 30;
    private uint _columns = 100;
    private Task? _runTask;
    private int _started;
    private int _closed;
    private int _inputOverflowNotified;

    public SshTerminalConnection(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile> route,
        Dispatcher dispatcher)
    {
        _profile = profile;
        _authentication = authentication;
        _verifyHostKey = verifyHostKey;
        _route = route;
        _dispatcher = dispatcher;
        _connectingMessage = LocalizationService.Format("ConnectingEndpoint", profile.DisplayEndpoint);
        _connectedMessage = LocalizationService.Format("ConnectedEndpoint", profile.DisplayEndpoint);
        _closedMessage = LocalizationService.Format("SessionClosedEndpoint", profile.DisplayEndpoint);
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
    public event EventHandler<TerminalHostStateChangedEventArgs>? StateChanged;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _runTask = RunAsync();
    }

    public void WriteInput(string data)
        => WriteInputCore(data);

    private void WriteInputCore(string data)
    {
        if (Volatile.Read(ref _closed) == 0 && !string.IsNullOrEmpty(data))
        {
            for (var offset = 0; offset < data.Length;)
            {
                var length = Math.Min(InputChunkCharacters, data.Length - offset);
                if (offset + length < data.Length &&
                    char.IsHighSurrogate(data[offset + length - 1]) &&
                    char.IsLowSurrogate(data[offset + length]))
                {
                    length--;
                }

                if (!_input.Writer.TryWrite(data.Substring(offset, length)))
                {
                    NotifyInputOverflow();
                    break;
                }
                offset += length;
            }
        }
    }

    public void Resize(uint rows, uint columns)
    {
        if (rows == 0 || columns == 0)
        {
            return;
        }

        _rows = rows;
        _columns = columns;
        lock (_resourceGate)
        {
            if (_shell is { CanWrite: true })
            {
                try
                {
                    _shell.ChangeWindowSize(columns, rows, 0, 0);
                }
                catch (ObjectDisposedException)
                {
                    // The remote shell closed during a resize notification.
                }
            }
            else if (_jumpProcess is { HasTerminal: true } process)
            {
                try { process.SetTerminalSize(checked((int)columns), checked((int)rows)); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _input.Writer.TryComplete();
        _lifetime.Cancel();
        lock (_resourceGate)
        {
            _shell?.Dispose();
            _shell = null;
            _jumpProcess?.Dispose();
            _jumpProcess = null;
            _jumpClient?.Dispose();
            _jumpClient = null;
            _session?.Dispose();
            _session = null;
        }

        var runTask = _runTask;
        if (runTask is null || runTask.IsCompleted)
        {
            _lifetime.Dispose();
        }
        else
        {
            _ = runTask.ContinueWith(
                _ => _lifetime.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public void Dispose() => Close();

    public void AttachRenderer() => _rendererAttached.TrySetResult();

    private async Task RunAsync()
    {
        PublishState(TerminalHostState.Starting, _connectingMessage);
        var authenticationMaterialLoaded = false;
        try
        {
            if (_profile.ProxyKind == SshProxyKind.JumpHost)
            {
                authenticationMaterialLoaded = true;
                await RunJumpAsync().ConfigureAwait(false);
                return;
            }
            var session = await _clientFactory.ConnectAsync(
                _profile, _authentication, _verifyHostKey, _route, _lifetime.Token).ConfigureAwait(false);
            authenticationMaterialLoaded = true;
            lock (_resourceGate)
            {
                _session = session;
            }

            _lifetime.Token.ThrowIfCancellationRequested();

            var shell = session.Client.CreateShellStream(
                "xterm-256color",
                _columns,
                _rows,
                0,
                0,
                1024 * 1024);
            lock (_resourceGate)
            {
                _shell = shell;
            }

            PublishState(TerminalHostState.Connected, _connectedMessage);
            await _rendererAttached.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            var reader = ReadLoopAsync(shell, _lifetime.Token);
            var writer = WriteLoopAsync(shell, _lifetime.Token);
            await reader.ConfigureAwait(false);
            _input.Writer.TryComplete();
            await writer.ConfigureAwait(false);
            if (Volatile.Read(ref _closed) == 0)
            {
                PublishState(TerminalHostState.Exited, _closedMessage);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing a tab cancels connection, read and write operations.
        }
        catch (Exception ex)
        {
            var authenticationFailed =
                !authenticationMaterialLoaded ||
                ex is SshAuthenticationException;
            PublishState(TerminalHostState.Failed, SanitizeError(ex), authenticationFailed);
        }
        finally
        {
            lock (_resourceGate)
            {
                _shell?.Dispose();
                _shell = null;
                _jumpProcess?.Dispose();
                _jumpProcess = null;
                _jumpClient?.Dispose();
                _jumpClient = null;
                _session?.Dispose();
                _session = null;
            }
        }
    }

    private async Task RunJumpAsync()
    {
        var client = JumpSshClientFactory.Create(_profile, _authentication, _route, _verifyHostKey);
        lock (_resourceGate) _jumpClient = client;
        await client.ConnectAsync(_lifetime.Token).ConfigureAwait(false);
        var process = await client.ExecuteShellAsync(new ExecuteOptions
        {
            AllocateTerminal = true,
            TerminalType = "xterm-256color",
            TerminalWidth = checked((int)_columns),
            TerminalHeight = checked((int)_rows),
        }, _lifetime.Token).ConfigureAwait(false);
        lock (_resourceGate) _jumpProcess = process;
        PublishState(TerminalHostState.Connected, _connectedMessage);
        await _rendererAttached.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        var reader = ReadJumpLoopAsync(process, _lifetime.Token);
        var writer = WriteJumpLoopAsync(process, _lifetime.Token);
        await reader.ConfigureAwait(false);
        _input.Writer.TryComplete();
        await writer.ConfigureAwait(false);
        if (Volatile.Read(ref _closed) == 0)
            PublishState(TerminalHostState.Exited, _closedMessage);
    }

    private async Task ReadJumpLoopAsync(RemoteProcess process, CancellationToken cancellationToken)
    {
        var chars = new char[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var (_, read) = await process.ReadAsync(chars, null, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await PublishOutputAsync(new string(chars, 0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteJumpLoopAsync(RemoteProcess process, CancellationToken cancellationToken)
    {
        await foreach (var input in _input.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await process.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _inputOverflowNotified, 0);
        }
    }

    private async Task ReadLoopAsync(ShellStream shell, CancellationToken cancellationToken)
    {
        var bytes = new byte[32 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await shell.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            decoder.Convert(
                bytes,
                0,
                read,
                chars,
                0,
                chars.Length,
                flush: false,
                out _,
                out var charsUsed,
                out _);
            if (charsUsed > 0)
            {
                await PublishOutputAsync(new string(chars, 0, charsUsed), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task WriteLoopAsync(ShellStream shell, CancellationToken cancellationToken)
    {
        await foreach (var input in _input.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WriteInputChunkAsync(shell, input, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _inputOverflowNotified, 0);
        }
    }

    internal static async Task WriteInputChunkAsync(
        Stream shell,
        string input,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        await shell.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await shell.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void NotifyInputOverflow()
    {
        if (Interlocked.Exchange(ref _inputOverflowNotified, 1) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke(() => TerminalOutput?.Invoke(
            this,
            new TerminalOutputEventArgs(
                "\r\n\x1b[31m[ssh-win-gui: SSH input backlog is full; the remaining paste was not sent.]\x1b[0m\r\n")));
    }

    private async Task PublishOutputAsync(string data, CancellationToken cancellationToken)
    {
        _keywordHighlighter.Configure(LocalizationService.KeywordHighlightingRules);
        var highlighted = _keywordHighlighter.Highlight(data);
        if (LocalizationService.KeywordHighlightingEnabled)
        {
            data = highlighted;
        }
        await _dispatcher.InvokeAsync(
            () => TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data)),
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private void PublishState(
        TerminalHostState state,
        string message,
        bool authenticationFailed = false)
    {
        _dispatcher.BeginInvoke(() =>
            StateChanged?.Invoke(
                this,
                new TerminalHostStateChangedEventArgs(state, message, authenticationFailed)));
    }

    private static string SanitizeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return string.IsNullOrEmpty(message) ? exception.GetType().Name : message;
    }
}
