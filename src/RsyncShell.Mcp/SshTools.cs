using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;
using Tmds.Ssh;

namespace RsyncShell.Mcp;

[McpServerToolType]
public sealed class SshTools
{
    private const int DefaultOutputBytes = 16 * 1024;
    private const int MaximumOutputBytes = 64 * 1024;

    [McpServerTool(
         Name = "list_sessions",
         ReadOnly = true,
         Idempotent = true,
         OpenWorld = false,
         UseStructuredContent = true,
         OutputSchemaType = typeof(SessionSummary[])),
     Description("Lists SSH sessions configured in ssh-win-gui. Use these session IDs or exact names with run_script. Private-key paths and credentials are never returned.")]
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
     Description("Runs a POSIX shell script on a configured ssh-win-gui session. Prefer this tool over invoking ssh through PowerShell: the script is sent directly to remote `sh -s` over SSH stdin, so local PowerShell quoting and escaping cannot alter it. The saved private key and jump/SOCKS route are reused. Unknown or changed host keys are rejected.")]
    public static async Task<RemoteScriptResult> RunScriptAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("POSIX shell script sent verbatim as UTF-8 to remote `sh -s`. Include `set -e` when fail-fast behavior is wanted.")] string script,
        [Description("Execution timeout in seconds, from 1 to 600. Defaults to 60.")] int timeoutSeconds = 60,
        [Description("Maximum bytes retained separately for stdout and stderr, from 1024 to 65536. Defaults to 16384; excess output is drained but marked truncated.")] int maxOutputBytes = DefaultOutputBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (timeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 600 seconds.");
        if (maxOutputBytes is < 1024 or > MaximumOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes), "Output limit must be between 1024 and 65536 bytes.");

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
        var verifier = new StrictHostKeyVerifier(new KnownHostStore());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var output = profile.ProxyKind == SshProxyKind.JumpHost
                ? await RunThroughNativeClientAsync(
                    profile, authentication, route, verifier.Verify, script, maxOutputBytes, linked.Token).ConfigureAwait(false)
                : await RunThroughSshNetAsync(
                    profile, authentication, route, verifier.Verify, script, maxOutputBytes, linked.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new RemoteScriptResult(
                profile.Id,
                profile.Name,
                output.ExitCode,
                output.StandardOutput,
                output.StandardError,
                output.StandardOutputTruncated,
                output.StandardErrorTruncated,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"SSH script timed out after {timeoutSeconds} seconds on session '{profile.Name}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (verifier.Failure is not null)
        {
            throw new InvalidOperationException(verifier.Failure, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"SSH script failed on session '{profile.Name}': {SanitizeError(ex.Message, profiles)}");
        }
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

    private static async Task<CommandOutput> RunThroughSshNetAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        string script,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        using var session = await new SshClientFactory().ConnectAsync(
            profile, authentication, verifyHostKey, route, cancellationToken).ConfigureAwait(false);
        using var command = session.Client.CreateCommand("sh -s");
        var execution = command.ExecuteAsync(cancellationToken);
        var stdout = ReadBoundedAsync(command.OutputStream, outputLimit, cancellationToken);
        var stderr = ReadBoundedAsync(command.ExtendedOutputStream, outputLimit, cancellationToken);
        await using (var input = command.CreateInputStream())
        {
            await input.WriteAsync(Encoding.UTF8.GetBytes(script), cancellationToken).ConfigureAwait(false);
        }
        await execution.ConfigureAwait(false);
        var stdoutResult = await stdout.ConfigureAwait(false);
        var stderrResult = await stderr.ConfigureAwait(false);
        return new CommandOutput(
            command.ExitStatus ?? -1,
            stdoutResult.Text,
            stderrResult.Text,
            stdoutResult.Truncated,
            stderrResult.Truncated);
    }

    private static async Task<CommandOutput> RunThroughNativeClientAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        IReadOnlyList<ConnectionProfile> route,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        string script,
        int outputLimit,
        CancellationToken cancellationToken)
    {
        using var client = JumpSshClientFactory.Create(profile, authentication, route, verifyHostKey);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var process = await client.ExecuteAsync("sh -s", cancellationToken).ConfigureAwait(false);
        await process.WriteAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.WriteEof();

        var stdout = new BoundedOutput(outputLimit);
        var stderr = new BoundedOutput(outputLimit);
        var stdoutBuffer = new byte[8 * 1024];
        var stderrBuffer = new byte[8 * 1024];
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

        return new CommandOutput(
            await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false),
            stdout.GetText(),
            stderr.GetText(),
            stdout.Truncated,
            stderr.Truncated);
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        Stream stream,
        int limit,
        CancellationToken cancellationToken)
    {
        var output = new BoundedOutput(limit);
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return new BoundedText(output.GetText(), output.Truncated);
            output.Append(buffer.AsSpan(0, read));
        }
    }

    private sealed class StrictHostKeyVerifier(KnownHostStore store)
    {
        public string? Failure { get; private set; }

        public bool Verify(SshHostKeyInfo key)
        {
            var status = store.Check(key, out _);
            if (status == KnownHostStatus.Trusted)
                return true;

            Failure = status switch
            {
                KnownHostStatus.Changed =>
                    $"Refused SSH session because the host key changed for {key.Host}:{key.Port}. Open ssh-win-gui and verify the new fingerprint manually.",
                KnownHostStatus.AdditionalAlgorithm =>
                    $"Refused an unapproved SSH host-key algorithm for {key.Host}:{key.Port}. Open ssh-win-gui and approve it manually.",
                _ =>
                    $"Refused unknown SSH host key for {key.Host}:{key.Port}. Connect once in ssh-win-gui and approve the fingerprint manually.",
            };
            return false;
        }
    }

    private sealed class BoundedOutput(int limit)
    {
        private readonly MemoryStream _stream = new(Math.Min(limit, 16 * 1024));
        public bool Truncated { get; private set; }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            var remaining = limit - checked((int)_stream.Length);
            if (remaining > 0)
                _stream.Write(bytes[..Math.Min(remaining, bytes.Length)]);
            if (bytes.Length > remaining)
                Truncated = true;
        }

        public string GetText() => Encoding.UTF8.GetString(_stream.GetBuffer(), 0, checked((int)_stream.Length));
    }

    private sealed record CommandOutput(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool StandardOutputTruncated,
        bool StandardErrorTruncated);

    private sealed record BoundedText(string Text, bool Truncated);
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
