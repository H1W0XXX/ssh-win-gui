using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RsyncShell.Core.Models;

namespace RsyncShell.Core.Services;

public sealed record RsyncWorkerEvent(
    string Type,
    string? State,
    string? Level,
    string? Message,
    string? Phase,
    long ProtocolReadBytes,
    long ProtocolWrittenBytes);

public sealed class RsyncWorkerException : Exception
{
    public RsyncWorkerException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class RsyncWorkerTransferService
{
    private const int ProtocolVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _workerPath;

    public RsyncWorkerTransferService(string workerPath)
    {
        _workerPath = workerPath;
    }

    public event EventHandler<RsyncWorkerEvent>? EventReceived;

    public async Task TransferAsync(
        RsyncTransferRequest request,
        SshAuthenticationOptions authentication,
        CancellationToken cancellationToken = default)
    {
        authentication.Validate();
        await RunWorkerAsync(
                "transfer",
                requestId => BuildTransferMessage(requestId, request, authentication),
                requireLocalTransferDirections: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task TransferRemoteToRemoteAsync(
        RsyncRemoteTransferRequest request,
        CancellationToken cancellationToken = default)
    {
        request.SourceAuthentication.Validate();
        request.DestinationAuthentication.Validate();
        await RunWorkerAsync(
                "remote_transfer",
                requestId => BuildRemoteTransferMessage(requestId, request),
                requireLocalTransferDirections: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RsyncRemoteRouteProbeResult>> ProbeRemoteRoutesAsync(
        RsyncRemoteRouteProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        request.FirstHopAuthentication.Validate();
        request.TargetAuthentication.Validate();
        var results = new List<RsyncRemoteRouteProbeResult>();
        await RunWorkerAsync(
                "probe_routes",
                requestId => BuildRouteProbeMessage(requestId, request),
                requireLocalTransferDirections: false,
                cancellationToken,
                message =>
                {
                    if (message.Type == "probe_result" && message.Probe is not null)
                    {
                        results.Add(new RsyncRemoteRouteProbeResult
                        {
                            Host = message.Probe.Host,
                            Port = message.Probe.Port,
                            InterfaceName = message.Probe.InterfaceName,
                            IsSavedEndpoint = message.Probe.IsSavedEndpoint,
                            Success = message.Probe.Success,
                            LatencyMilliseconds = message.Probe.LatencyMilliseconds,
                            Fingerprint = message.Probe.Fingerprint,
                            Message = message.Probe.Message,
                        });
                    }
                })
            .ConfigureAwait(false);
        return results;
    }

    private async Task RunWorkerAsync(
        string requiredOperation,
        Func<string, object> buildRequest,
        bool requireLocalTransferDirections,
        CancellationToken cancellationToken,
        Action<WorkerMessage>? observeMessage = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the rsync worker.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        var writeGate = new object();
        string? jobId = null;
        var cancelRequested = false;
        var cancelSent = false;

        void WriteMessage(object message)
        {
            lock (writeGate)
            {
                if (process.HasExited)
                {
                    return;
                }

                process.StandardInput.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
                process.StandardInput.Flush();
            }
        }

        void RequestCancel()
        {
            object? cancelMessage = null;
            lock (writeGate)
            {
                cancelRequested = true;
                if (cancelSent || jobId is null || process.HasExited)
                {
                    return;
                }

                cancelSent = true;
                cancelMessage = new
                {
                    type = "cancel",
                    requestId = "cancel-" + Guid.NewGuid().ToString("N"),
                    jobId,
                };
            }

            _ = Task.Run(() => TryWriteCancellation(cancelMessage));
        }

        void TryWriteCancellation(object message)
        {
            lock (writeGate)
            {
                try
                {
                    if (process.HasExited)
                    {
                        return;
                    }

                    process.StandardInput.WriteLine(JsonSerializer.Serialize(message, JsonOptions));
                    process.StandardInput.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The read loop enforces a bounded grace period and then stops this worker.
                }
            }
        }

        using var registration = cancellationToken.Register(RequestCancel);
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            string helloLine;
            try
            {
                helloLine = await process.StandardOutput.ReadLineAsync(helloTimeout.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The rsync worker exited before its hello message.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The rsync worker did not send its hello message within 8 seconds.");
            }
            var hello = Deserialize(helloLine);
            if (hello.Type != "hello" || hello.ProtocolVersion != ProtocolVersion)
            {
                throw new InvalidOperationException(
                    $"Unsupported rsync worker protocol {hello.ProtocolVersion}; expected {ProtocolVersion}.");
            }
            if (hello.Capabilities is null ||
                !hello.Capabilities.Operations.Contains(requiredOperation, StringComparer.Ordinal) ||
                !hello.Capabilities.Operations.Contains("cancel", StringComparer.Ordinal) ||
                (requireLocalTransferDirections &&
                 (!hello.Capabilities.Directions.Contains("upload", StringComparer.Ordinal) ||
                  !hello.Capabilities.Directions.Contains("download", StringComparer.Ordinal))))
            {
                throw new InvalidOperationException("The rsync worker hello is missing required capabilities.");
            }

            var requestId = "request-" + Guid.NewGuid().ToString("N");
            WriteMessage(buildRequest(requestId));
            var cancellationSignal = cancellationToken.CanBeCanceled
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : NeverCompletingTask;
            DateTimeOffset? cancellationDeadline = null;

            while (true)
            {
                var readResult = await ReadProtocolLineAsync(
                        process.StandardOutput,
                        cancellationToken,
                        cancellationSignal,
                        cancellationDeadline,
                        TimeSpan.FromSeconds(12))
                    .ConfigureAwait(false);
                var line = readResult.Line;
                cancellationDeadline = readResult.CancellationDeadline;
                if (line is null)
                {
                    var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(stderr)
                            ? "The rsync worker exited without a completion event."
                            : "The rsync worker exited: " + stderr);
                }

                var message = Deserialize(line);
                observeMessage?.Invoke(message);
                if (message.Type == "state" && message.State == "queued" && !string.IsNullOrWhiteSpace(message.JobId))
                {
                    if (jobId is not null || !string.Equals(message.RequestId, requestId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The rsync worker queued an unexpected transfer request.");
                    }
                    lock (writeGate)
                    {
                        jobId = message.JobId;
                    }
                    if (cancelRequested)
                    {
                        RequestCancel();
                    }
                }
                else if (jobId is null)
                {
                    if (message.Type == "error" && string.Equals(message.RequestId, requestId, StringComparison.Ordinal))
                    {
                        throw new RsyncWorkerException(
                            message.Error?.Code ?? "worker_error",
                            message.Error?.Message ?? "The rsync worker rejected the request.");
                    }

                    throw new InvalidOperationException("The rsync worker emitted a job event before queueing this request.");
                }
                else if (!string.Equals(message.JobId, jobId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The rsync worker emitted an event for an unexpected job.");
                }

                Publish(message);
                if (message.Type == "error")
                {
                    throw new RsyncWorkerException(
                        message.Error?.Code ?? "worker_error",
                        message.Error?.Message ?? "The rsync worker rejected the request.");
                }

                if (message.Type != "completed")
                {
                    continue;
                }

                if (message.State == "success")
                {
                    break;
                }

                if (message.State == "cancelled" || cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The rsync transfer was cancelled.", cancellationToken);
                }

                throw new RsyncWorkerException(
                    message.Error?.Code ?? "transfer_failed",
                    message.Error?.Message ?? "The rsync transfer failed.");
            }

            process.StandardInput.Close();
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The rsync worker did not exit within 5 seconds after completion.");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The rsync worker exited with code {process.ExitCode}.");
            }
        }
        finally
        {
            await StopWorkerAsync(process).ConfigureAwait(false);
        }
    }

    private static object BuildTransferMessage(
        string requestId,
        RsyncTransferRequest request,
        SshAuthenticationOptions authentication)
    {
        var route = request.Route.Count == 0 ? [request.Profile] : request.Route;
        var remote = BuildRemote(route, 0, authentication);

        return new
        {
            type = "transfer",
            requestId,
            transfer = new
            {
                direction = request.Direction == RsyncTransferDirection.Upload ? "upload" : "download",
                request.LocalPath,
                request.RemotePath,
                request.CopyContents,
                remote,
                options = new
                {
                    request.PreserveTimes,
                    request.PreservePermissions,
                    request.PreserveLinks,
                    request.Delete,
                    request.DryRun,
                    request.Compress,
                    request.Partial,
                    request.BandwidthLimitKbps,
                    request.ExtraArguments,
                },
            },
        };
    }

    private static object BuildRouteProbeMessage(
        string requestId,
        RsyncRemoteRouteProbeRequest request)
    {
        var firstHopRoute = request.FirstHopRoute.Count == 0
            ? [request.FirstHopProfile]
            : request.FirstHopRoute;
        var targetRoute = request.TargetRoute.Count == 0
            ? [request.TargetProfile]
            : request.TargetRoute;
        return new
        {
            type = "probe_routes",
            requestId,
            routeProbe = new
            {
                firstHop = BuildRemote(firstHopRoute, 0, request.FirstHopAuthentication),
                target = BuildRemote(targetRoute, 0, request.TargetAuthentication),
                candidates = request.Candidates.Select(candidate => new
                {
                    candidate.Host,
                    candidate.Port,
                    candidate.InterfaceName,
                    candidate.IsSavedEndpoint,
                }),
            },
        };
    }

    private static object BuildRemoteTransferMessage(
        string requestId,
        RsyncRemoteTransferRequest request)
    {
        var sourceRoute = request.SourceRoute.Count == 0
            ? [request.SourceProfile]
            : request.SourceRoute;
        var destinationRoute = request.DestinationRoute.Count == 0
            ? [request.DestinationProfile]
            : request.DestinationRoute;
        return new
        {
            type = "remote_transfer",
            requestId,
            remoteTransfer = new
            {
                sourcePath = request.SourcePath,
                destinationPath = request.DestinationPath,
                copyContents = request.CopyContents,
                executionSide = request.ExecutionSide switch
                {
                    RsyncRemoteTransferExecutionSide.Source => "source",
                    RsyncRemoteTransferExecutionSide.Destination => "destination",
                    _ => "auto",
                },
                source = BuildRemote(sourceRoute, 0, request.SourceAuthentication),
                destination = BuildRemote(destinationRoute, 0, request.DestinationAuthentication),
                request.SourceTransferHost,
                request.SourceTransferPort,
                request.DestinationTransferHost,
                request.DestinationTransferPort,
                options = new
                {
                    request.PreserveTimes,
                    request.PreservePermissions,
                    request.PreserveLinks,
                    request.Delete,
                    request.DryRun,
                    request.Compress,
                    request.Partial,
                    request.BandwidthLimitKbps,
                    request.ExtraArguments,
                },
            },
        };
    }

    private static object BuildRemote(
        IReadOnlyList<ConnectionProfile> route,
        int index,
        SshAuthenticationOptions targetAuthentication)
    {
        var profile = route[index];
        object auth = index == 0
            ? BuildAuthentication(targetAuthentication)
            : BuildAuthentication(new SshAuthenticationOptions
            {
                Kind = SshAuthenticationKind.PrivateKey,
                PrivateKeyPath = Environment.ExpandEnvironmentVariables(profile.PrivateKeyPath
                    ?? throw new InvalidOperationException($"Jump session '{profile.Name}' must have a saved private key.")),
            });
        object? proxy = profile.ProxyKind switch
        {
            SshProxyKind.None => null,
            SshProxyKind.Socks5 => new { type = "socks5", host = profile.ProxyHost, port = profile.ProxyPort },
            SshProxyKind.JumpHost when index + 1 < route.Count =>
                new { type = "jump", jump = BuildRemote(route, index + 1, targetAuthentication) },
            _ => throw new InvalidOperationException($"The proxy route for '{profile.Name}' is incomplete."),
        };
        return new
        {
            host = profile.Host,
            port = profile.Port,
            user = profile.Username,
            auth,
            hostKey = new { mode = "log_only" },
            proxy,
        };
    }

    private static object BuildAuthentication(SshAuthenticationOptions authentication) =>
        authentication.Kind == SshAuthenticationKind.Password
            ? new { method = "password", password = authentication.Password, privateKeyPath = (string?)null, passphrase = (string?)null }
            : new { method = "private_key", password = (string?)null, privateKeyPath = authentication.PrivateKeyPath, passphrase = authentication.PrivateKeyPassphrase };

    private void Publish(WorkerMessage message)
    {
        if (message.Type == "hello")
        {
            return;
        }

        EventReceived?.Invoke(this, new RsyncWorkerEvent(
            message.Type,
            message.State,
            message.Level,
            message.Message ?? message.Error?.Message,
            message.Phase,
            message.ProtocolReadBytes,
            message.ProtocolWrittenBytes));
    }

    private static WorkerMessage Deserialize(string line) =>
        JsonSerializer.Deserialize<WorkerMessage>(line, JsonOptions)
        ?? throw new InvalidOperationException("The rsync worker emitted an empty JSON message.");

    private static readonly Task NeverCompletingTask =
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

    private static async Task<ProtocolReadResult> ReadProtocolLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken,
        Task cancellationSignal,
        DateTimeOffset? cancellationDeadline,
        TimeSpan cancellationGracePeriod)
    {
        var readTask = reader.ReadLineAsync();
        if (cancellationDeadline is null && !cancellationToken.IsCancellationRequested)
        {
            if (await Task.WhenAny(readTask, cancellationSignal).ConfigureAwait(false) == readTask)
            {
                return new ProtocolReadResult(await readTask.ConfigureAwait(false), null);
            }
        }

        cancellationDeadline ??= DateTimeOffset.UtcNow + cancellationGracePeriod;
        var remaining = cancellationDeadline.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero ||
            await Task.WhenAny(readTask, Task.Delay(remaining)).ConfigureAwait(false) != readTask)
        {
            throw new OperationCanceledException(
                "The rsync worker did not acknowledge cancellation in time.",
                cancellationToken);
        }
        return new ProtocolReadResult(await readTask.ConfigureAwait(false), cancellationDeadline);
    }

    private static async Task StopWorkerAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup must not replace the transfer's original error.
        }
    }

    private sealed record WorkerMessage
    {
        public string Type { get; init; } = string.Empty;
        public int ProtocolVersion { get; init; }
        public string? RequestId { get; init; }
        public string? JobId { get; init; }
        public string? State { get; init; }
        public string? Level { get; init; }
        public string? Message { get; init; }
        public string? Phase { get; init; }
        public long ProtocolReadBytes { get; init; }
        public long ProtocolWrittenBytes { get; init; }
        public WorkerError? Error { get; init; }
        public WorkerCapabilities? Capabilities { get; init; }
        public WorkerRouteProbeResult? Probe { get; init; }
    }

    private sealed record WorkerError
    {
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    private sealed record WorkerCapabilities
    {
        public string[] Operations { get; init; } = [];
        public string[] Directions { get; init; } = [];
    }

    private sealed record WorkerRouteProbeResult
    {
        public string Host { get; init; } = string.Empty;
        public int Port { get; init; }
        public string InterfaceName { get; init; } = string.Empty;
        public bool IsSavedEndpoint { get; init; }
        public bool Success { get; init; }
        public long LatencyMilliseconds { get; init; }
        public string Fingerprint { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    private sealed record ProtocolReadResult(string? Line, DateTimeOffset? CancellationDeadline);
}
