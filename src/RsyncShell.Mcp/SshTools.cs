using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.Mcp;

[McpServerToolType]
public sealed class SshTools
{
    private const int DefaultOutputBytes = 16 * 1024;
    private const int MaximumOutputBytes = 64 * 1024;
    private const int DefaultMultiOutputBytes = 4 * 1024;
    private const int MaximumMultiOutputBytes = 16 * 1024;
    private const int MaximumMultiSessions = 32;
    private const int DefaultTransferTimeoutSeconds = 600;

    [McpServerTool(
         Name = "list_sessions",
         ReadOnly = true,
         Idempotent = true,
         OpenWorld = false,
         UseStructuredContent = true,
         OutputSchemaType = typeof(SessionSummary[])),
     Description("Lists SSH sessions configured in ssh-win-gui. Use these session IDs or exact names with the other tools. Private-key paths and credentials are never returned.")]
    public static async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = await new SessionRepository().LoadAsync(cancellationToken).ConfigureAwait(false);
        return profiles
            .OrderBy(profile => profile.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new SessionSummary(
                profile.Id,
                profile.Name,
                profile.Group,
                profile.DisplayEndpoint,
                profile.ProxyKind.ToString()))
            .ToArray();
    }

    [McpServerTool(
         Name = "run_script",
         Destructive = true,
         OpenWorld = true,
         UseStructuredContent = true,
         OutputSchemaType = typeof(RemoteScriptResult)),
     Description("Runs a POSIX shell script on a configured ssh-win-gui session. Prefer this tool over invoking ssh through PowerShell: the script is sent directly to remote `sh -s` over SSH stdin, so local PowerShell quoting and escaping cannot alter it. The session's SSH connection is kept open and reused by later calls. Choose which part of long output to keep with outputMode and grep instead of piping through tail or grep, which would hide the script's exit code. Unknown or changed host keys are rejected. For commands that outlive the timeout use start_job.")]
    public static async Task<RemoteScriptResult> RunScriptAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("POSIX shell script sent verbatim as UTF-8 to remote `sh -s`. Include `set -e` when fail-fast behavior is wanted.")] string script,
        [Description("Execution timeout in seconds, from 1 to 600. Defaults to 60.")] int timeoutSeconds = 60,
        [Description("Maximum bytes retained separately for stdout and stderr, from 1024 to 65536. Defaults to 16384; excess output is drained and replaced by an '[... N bytes omitted ...]' marker.")] int maxOutputBytes = DefaultOutputBytes,
        [Description("Which part of over-long output to keep: 'head_tail' (default, first and last halves), 'tail' (the end, best for builds and tests), or 'head' (the start).")] string outputMode = "head_tail",
        [Description("Optional .NET regular expression. When set, only stdout/stderr lines that match are kept, before the byte limit applies. Use (?i) for case-insensitive matching.")] string? grep = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        var options = ScriptOptions.Create(script, timeoutSeconds, maxOutputBytes, MaximumOutputBytes, outputMode, grep);
        var context = await SessionContext.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        return await RunScriptAsync(context, options, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(
         Name = "run_script_multi",
         Destructive = true,
         OpenWorld = true,
         UseStructuredContent = true,
         OutputSchemaType = typeof(MultiScriptResult)),
     Description("Runs the same POSIX shell script on several ssh-win-gui sessions in parallel, one reused SSH connection per session. Sessions whose exit code and output are identical are merged into one group, so a fleet that agrees costs the output of a single host. A host that cannot be reached is reported in its own group instead of failing the call. Arguments behave as in run_script.")]
    public static async Task<MultiScriptResult> RunScriptMultiAsync(
        [Description("1 to 32 session IDs or exact session names from list_sessions.")] string[] sessions,
        [Description("POSIX shell script sent verbatim as UTF-8 to remote `sh -s` on every session.")] string script,
        [Description("Per-session execution timeout in seconds, from 1 to 600. Defaults to 60.")] int timeoutSeconds = 60,
        [Description("Maximum bytes retained per session, separately for stdout and stderr, from 1024 to 16384. Defaults to 4096.")] int maxOutputBytes = DefaultMultiOutputBytes,
        [Description("Which part of over-long output to keep: 'head_tail' (default), 'tail', or 'head'.")] string outputMode = "head_tail",
        [Description("Optional .NET regular expression; only matching stdout/stderr lines are kept.")] string? grep = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Length is < 1 or > MaximumMultiSessions)
            throw new ArgumentException($"Provide between 1 and {MaximumMultiSessions} sessions.", nameof(sessions));
        var options = ScriptOptions.Create(script, timeoutSeconds, maxOutputBytes, MaximumMultiOutputBytes, outputMode, grep);

        // Resolve every name before touching any host, so a typo runs nothing.
        var profiles = await new SessionRepository().LoadAsync(cancellationToken).ConfigureAwait(false);
        var targets = sessions
            .Select(selector => ResolveProfile(profiles, selector ?? string.Empty))
            .DistinctBy(profile => profile.Id, StringComparer.Ordinal)
            .ToArray();
        return await RunScriptMultiAsync(
            targets,
            profile => SessionContext.Create(profiles, profile),
            options,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<RemoteScriptResult> RunScriptAsync(
        SessionContext context,
        ScriptOptions options,
        CancellationToken cancellationToken)
    {
        var stdout = new OutputCapture(options.MaxOutputBytes, options.Mode);
        var stderr = new OutputCapture(options.MaxOutputBytes, options.Mode);
        var stdoutFilter = options.Grep is null ? null : new LineFilterSink(options.Grep, stdout);
        var stderrFilter = options.Grep is null ? null : new LineFilterSink(options.Grep, stderr);
        var stopwatch = Stopwatch.StartNew();
        var exitCode = await context.RunScriptAsync(
            "SSH script",
            options.Script,
            (IOutputSink?)stdoutFilter ?? stdout,
            (IOutputSink?)stderrFilter ?? stderr,
            options.TimeoutSeconds,
            cancellationToken).ConfigureAwait(false);
        stdoutFilter?.Complete();
        stderrFilter?.Complete();
        stopwatch.Stop();
        return new RemoteScriptResult(
            context.Profile.Id,
            context.Profile.Name,
            exitCode,
            stdout.GetText(),
            stderr.GetText(),
            stdout.Truncated,
            stderr.Truncated,
            stopwatch.ElapsedMilliseconds);
    }

    internal static async Task<MultiScriptResult> RunScriptMultiAsync(
        IReadOnlyList<ConnectionProfile> targets,
        Func<ConnectionProfile, SessionContext> createContext,
        ScriptOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcomes = await Task.WhenAll(targets.Select(async profile =>
        {
            try
            {
                var result = await RunScriptAsync(createContext(profile), options, cancellationToken)
                    .ConfigureAwait(false);
                return new MultiScriptGroup(
                    [profile.Name],
                    result.ExitCode,
                    result.StandardOutput,
                    result.StandardError,
                    result.StandardOutputTruncated,
                    result.StandardErrorTruncated,
                    null);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                return new MultiScriptGroup([profile.Name], null, string.Empty, string.Empty, false, false, error.Message);
            }
        })).ConfigureAwait(false);
        stopwatch.Stop();

        var groups = outcomes
            .GroupBy(outcome => (
                outcome.ExitCode,
                outcome.StandardOutput,
                outcome.StandardError,
                outcome.StandardOutputTruncated,
                outcome.StandardErrorTruncated,
                outcome.Error))
            .Select(group => group.First() with { Sessions = group.SelectMany(outcome => outcome.Sessions).ToArray() })
            .ToArray();
        return new MultiScriptResult(groups, targets.Count, stopwatch.ElapsedMilliseconds);
    }

    [McpServerTool(
         Name = "rsync_upload",
         Destructive = true,
         OpenWorld = true,
         UseStructuredContent = true,
         OutputSchemaType = typeof(RsyncPathTransferResult)),
     Description("Uploads a local file or directory to an exact remote destination path with ssh-win-gui's bundled rsync worker. The destination may use a different name. Directories are copied recursively. Compression is enabled and fingerprints are logged. Existing files are not replaced and existing directories are not merged unless overwrite=true.")]
    public static Task<RsyncPathTransferResult> RsyncUploadAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("Absolute path of the existing local source file or directory.")] string localSourcePath,
        [Description("Exact absolute remote destination path. This includes the desired file or directory name, so it may rename the source.")] string remoteDestinationPath,
        [Description("Whether an existing remote file may be replaced or an existing remote directory may be merged. Defaults to false.")] bool overwrite = false,
        [Description("Transfer timeout in seconds, from 1 to 600. Defaults to 600.")] int timeoutSeconds = DefaultTransferTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        TransferPathAsync(session, localSourcePath, remoteDestinationPath, upload: true, overwrite, timeoutSeconds, cancellationToken);

    [McpServerTool(
         Name = "rsync_download",
         Destructive = true,
         OpenWorld = true,
         UseStructuredContent = true,
         OutputSchemaType = typeof(RsyncPathTransferResult)),
     Description("Downloads a remote file or directory to an exact local destination path with ssh-win-gui's bundled rsync worker. The destination may use a different name. Directories are copied recursively. Compression is enabled and fingerprints are logged. Existing files are not replaced and existing directories are not merged unless overwrite=true.")]
    public static Task<RsyncPathTransferResult> RsyncDownloadAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("Absolute path of the existing remote source file or directory.")] string remoteSourcePath,
        [Description("Exact absolute local destination path. This includes the desired file or directory name, so it may rename the source.")] string localDestinationPath,
        [Description("Whether an existing local file may be replaced or an existing local directory may be merged. Defaults to false.")] bool overwrite = false,
        [Description("Transfer timeout in seconds, from 1 to 600. Defaults to 600.")] int timeoutSeconds = DefaultTransferTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        TransferPathAsync(session, localDestinationPath, remoteSourcePath, upload: false, overwrite, timeoutSeconds, cancellationToken);

    private static async Task<RsyncPathTransferResult> TransferPathAsync(
        string session,
        string localPath,
        string remotePath,
        bool upload,
        bool overwrite,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        var localParameterName = upload ? "localSourcePath" : "localDestinationPath";
        var remoteParameterName = upload ? "remoteDestinationPath" : "remoteSourcePath";
        if (string.IsNullOrWhiteSpace(localPath))
            throw new ArgumentException("Local path is required.", localParameterName);
        if (string.IsNullOrWhiteSpace(remotePath))
            throw new ArgumentException("Remote path is required.", remoteParameterName);
        if (timeoutSeconds is < 1 or > DefaultTransferTimeoutSeconds)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 600 seconds.");
        ValidateAbsoluteRemotePath(remotePath, remoteParameterName);

        if (!upload)
            ValidateExactLocalDestinationPath(localPath, localParameterName);
        var fullLocalPath = NormalizeAbsoluteLocalPath(localPath, localParameterName);
        bool? sourceIsDirectory = null;
        if (upload)
        {
            sourceIsDirectory = Directory.Exists(fullLocalPath)
                ? true
                : File.Exists(fullLocalPath) ? false : null;
            if (sourceIsDirectory is null)
                throw new FileNotFoundException("The local source file or directory does not exist.", fullLocalPath);
            if ((File.GetAttributes(fullLocalPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Local symbolic-link or reparse-point sources are not supported by this MCP transfer tool.");
            if (remotePath == "/")
                throw new ArgumentException("The remote destination must include a file or directory name; refusing the filesystem root.", remoteParameterName);
        }
        else
        {
            var localParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullLocalPath));
            if (string.IsNullOrWhiteSpace(localParent) || !Directory.Exists(localParent))
                throw new DirectoryNotFoundException("The local destination parent directory does not exist.");
        }

        var profiles = await new SessionRepository().LoadAsync(cancellationToken).ConfigureAwait(false);
        var profile = ResolveProfile(profiles, session);
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            throw new InvalidOperationException(
                $"Session '{profile.Name}' has no saved private key. Password authentication is intentionally unavailable to the MCP server.");

        var privateKeyPath = Environment.ExpandEnvironmentVariables(profile.PrivateKeyPath);
        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException($"The saved private key for session '{profile.Name}' does not exist.");

        var authentication = new SshAuthenticationOptions
        {
            Kind = SshAuthenticationKind.PrivateKey,
            PrivateKeyPath = privateKeyPath,
        };
        var route = SshRouteResolver.Resolve(profile, profiles);
        var workerPath = ResolveRsyncWorkerPath();
        if (workerPath is null)
            throw new FileNotFoundException("The bundled rsync worker was not found next to the MCP server.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopwatch = Stopwatch.StartNew();
        long transferredBytes = 0;
        var log = new OutputCapture(16 * 1024);

        try
        {
            bool LogHostKey(SshHostKeyInfo key)
            {
                log.Append(Encoding.UTF8.GetBytes(
                    $"[host-key] {key.Host}:{key.Port} {key.Algorithm} {key.FingerprintSha256}{Environment.NewLine}"));
                return true;
            }

            if (upload)
            {
                var existingDestination = await FindRemoteEntryAsync(
                    profile, authentication, route, remotePath, LogHostKey, linked.Token).ConfigureAwait(false);
                if (existingDestination?.IsSymbolicLink == true)
                    throw new IOException("The remote destination is a symbolic link; refusing to overwrite through it.");
                ValidateDestinationConflict(
                    existingDestination is not null,
                    existingDestination?.IsDirectory,
                    sourceIsDirectory!.Value,
                    overwrite,
                    "remote");
            }
            else
            {
                if (remotePath == "/")
                {
                    sourceIsDirectory = true;
                }
                else
                {
                    var source = await FindRemoteEntryAsync(
                        profile, authentication, route, remotePath, LogHostKey, linked.Token).ConfigureAwait(false)
                        ?? throw new FileNotFoundException("The remote source file or directory does not exist.");
                    if (source.IsSymbolicLink)
                        throw new IOException("Symbolic-link sources are not supported by this MCP transfer tool.");
                    sourceIsDirectory = source.IsDirectory;
                }

                var localFileExists = File.Exists(fullLocalPath);
                var localDirectoryExists = Directory.Exists(fullLocalPath);
                if ((localFileExists || localDirectoryExists) &&
                    (File.GetAttributes(fullLocalPath) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The local destination is a symbolic link or reparse point; refusing to overwrite through it.");
                ValidateDestinationConflict(
                    localFileExists || localDirectoryExists,
                    localDirectoryExists ? true : localFileExists ? false : null,
                    sourceIsDirectory.Value,
                    overwrite,
                    "local");
            }

            var service = new RsyncWorkerTransferService(workerPath);
            service.EventReceived += (_, transferEvent) =>
            {
                transferredBytes = Math.Max(transferredBytes, GetTransferProgressBytes(transferEvent));
                if (!string.IsNullOrWhiteSpace(transferEvent.Message))
                    log.Append(Encoding.UTF8.GetBytes(transferEvent.Message + Environment.NewLine));
            };
            await service.TransferAsync(new RsyncTransferRequest
            {
                Direction = upload ? RsyncTransferDirection.Upload : RsyncTransferDirection.Download,
                Profile = profile,
                Route = route,
                LocalPath = fullLocalPath,
                RemotePath = remotePath,
                CopyContents = sourceIsDirectory!.Value,
                ExactDestination = !upload && !sourceIsDirectory.Value,
                PreservePermissions = false,
                PreserveLinks = false,
                Compress = true,
            }, authentication, linked.Token).ConfigureAwait(false);

            stopwatch.Stop();
            long? dataBytes = sourceIsDirectory.Value
                ? null
                : upload
                    ? new FileInfo(fullLocalPath).Length
                    : File.Exists(fullLocalPath) ? new FileInfo(fullLocalPath).Length : null;
            return new RsyncPathTransferResult(
                profile.Id,
                profile.Name,
                upload ? "upload" : "download",
                sourceIsDirectory.Value ? "directory" : "file",
                fullLocalPath,
                remotePath,
                dataBytes,
                transferredBytes,
                overwrite,
                true,
                log.GetText().TrimEnd(),
                log.Truncated,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"rsync transfer timed out after {timeoutSeconds} seconds on session '{profile.Name}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"rsync {(upload ? "upload" : "download")} failed on session '{profile.Name}': {SanitizeError(ex.Message, profiles)}");
        }
    }

    private static async Task<RemoteFileEntry?> FindRemoteEntryAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        string path,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        CancellationToken cancellationToken)
    {
        var (parent, name) = SplitRemotePath(path);
        var listing = await new RemoteFileService().ListAsync(
            profile, authentication, verifyHostKey, parent, route, cancellationToken).ConfigureAwait(false);
        var entry = listing.Entries.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.Ordinal));
        if (entry is null && listing.IsTruncated)
            throw new IOException("The remote parent directory listing is truncated, so the requested path cannot be checked safely.");
        return entry;
    }

    private static void ValidateDestinationConflict(
        bool exists,
        bool? destinationIsDirectory,
        bool sourceIsDirectory,
        bool overwrite,
        string side)
    {
        if (!exists)
            return;
        if (!overwrite)
            throw new IOException($"The {side} destination already exists. Set overwrite=true to replace a file or merge a directory.");
        if (destinationIsDirectory != sourceIsDirectory)
            throw new IOException($"The {side} destination has a different type from the source; refusing to replace a file with a directory or a directory with a file.");
    }

    internal static string? ResolveRsyncWorkerPath(string? baseDirectory = null)
    {
        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var candidates = new[]
        {
            Path.Combine(root, "tools", "rsync", "rsyncworker.exe"),
            Path.GetFullPath(Path.Combine(root, "..", "rsync", "rsyncworker.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static long GetTransferProgressBytes(RsyncWorkerEvent transferEvent) =>
        Math.Max(
            transferEvent.TransferredBytes ?? 0,
            Math.Max(transferEvent.ProtocolReadBytes, transferEvent.ProtocolWrittenBytes));

    private static string NormalizeAbsoluteLocalPath(string path, string parameterName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (!Path.IsPathFullyQualified(expanded))
            throw new ArgumentException("Local path must be absolute.", parameterName);
        return Path.GetFullPath(expanded);
    }

    private static void ValidateExactLocalDestinationPath(string path, string parameterName)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (Path.EndsInDirectorySeparator(expanded))
            throw new ArgumentException("Local destination must be an exact file or directory path without a trailing separator.", parameterName);
    }

    internal static (string Parent, string Name) SplitRemotePath(string path)
    {
        ValidateAbsoluteRemotePath(path, nameof(path));
        if (path == "/")
            throw new ArgumentException("The filesystem root has no parent entry.", nameof(path));
        var separator = path.LastIndexOf('/');
        return (separator == 0 ? "/" : path[..separator], path[(separator + 1)..]);
    }

    private static void ValidateAbsoluteRemotePath(string path, string parameterName)
    {
        if (path.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Remote path cannot contain NUL or line-break characters.", parameterName);
        if (!path.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Remote path must be an absolute POSIX path.", parameterName);
        if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("Remote path must be an exact file or directory path without a trailing slash.", parameterName);
        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name is "." or "..")
            throw new ArgumentException("Remote path must not end with '.' or '..'.", parameterName);
    }

    internal static ConnectionProfile ResolveProfile(
        IReadOnlyList<ConnectionProfile> profiles,
        string selector)
    {
        var value = selector.Trim();
        var byId = profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, value, StringComparison.OrdinalIgnoreCase));
        if (byId is not null)
            return byId;

        var byName = profiles.Where(profile =>
            string.Equals(profile.Name, value, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        return byName.Length switch
        {
            1 => byName[0],
            0 => throw new KeyNotFoundException(
                $"No configured SSH session matches '{value}'. Call list_sessions and use a returned ID or exact name."),
            _ => throw new InvalidOperationException(
                $"More than one SSH session is named '{value}'. Call list_sessions and select by ID."),
        };
    }

    internal static string SanitizeError(string message, IReadOnlyList<ConnectionProfile> profiles)
    {
        var sanitized = message;
        foreach (var privateKeyPath in profiles
                     .Select(profile => profile.PrivateKeyPath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(path => Environment.ExpandEnvironmentVariables(path!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            sanitized = sanitized.Replace(privateKeyPath, "<private-key>", StringComparison.OrdinalIgnoreCase);
        }
        return sanitized;
    }
}

public sealed record SessionSummary(
    string Id,
    string Name,
    string Group,
    string Endpoint,
    string Route);

public sealed record RemoteScriptResult(
    string SessionId,
    string SessionName,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    long DurationMilliseconds);

public sealed record RsyncPathTransferResult(
    string SessionId,
    string SessionName,
    string Direction,
    string SourceType,
    string LocalPath,
    string RemotePath,
    long? DataBytes,
    long ProtocolBytesTransferred,
    bool Overwrite,
    bool Compress,
    string Log,
    bool LogTruncated,
    long DurationMilliseconds);

public sealed record MultiScriptResult(
    IReadOnlyList<MultiScriptGroup> Groups,
    int SessionCount,
    long DurationMilliseconds);

public sealed record MultiScriptGroup(
    IReadOnlyList<string> Sessions,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated,
    string? Error);

internal sealed record ScriptOptions(
    string Script,
    int TimeoutSeconds,
    int MaxOutputBytes,
    OutputMode Mode,
    Regex? Grep)
{
    public static ScriptOptions Create(
        string script,
        int timeoutSeconds,
        int maxOutputBytes,
        int maximumOutputBytes,
        string outputMode,
        string? grep)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (timeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 600 seconds.");
        if (maxOutputBytes < 1024 || maxOutputBytes > maximumOutputBytes)
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputBytes), $"Output limit must be between 1024 and {maximumOutputBytes} bytes.");
        return new ScriptOptions(script, timeoutSeconds, maxOutputBytes, ParseMode(outputMode), ParseGrep(grep));
    }

    public static OutputMode ParseMode(string? outputMode) =>
        (outputMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "" or "head_tail" or "headtail" or "both" => OutputMode.HeadTail,
            "head" => OutputMode.Head,
            "tail" => OutputMode.Tail,
            _ => throw new ArgumentException("outputMode must be 'head', 'tail', or 'head_tail'.", nameof(outputMode)),
        };

    public static Regex? ParseGrep(string? grep)
    {
        if (string.IsNullOrEmpty(grep))
            return null;
        try
        {
            return LineFilterSink.CreatePattern(grep);
        }
        catch (ArgumentException error)
        {
            throw new ArgumentException($"grep is not a valid regular expression: {error.Message}", nameof(grep));
        }
    }
}
