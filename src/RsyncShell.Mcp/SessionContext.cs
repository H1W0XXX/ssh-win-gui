using System.Text;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.Mcp;

/// <summary>One resolved session plus the pooled connection used to reach it.</summary>
internal sealed record SessionContext(
    SshConnectionPool Pool,
    IReadOnlyList<ConnectionProfile> Profiles,
    ConnectionProfile Profile,
    SshTarget Target)
{
    public static async Task<SessionContext> OpenAsync(string session, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        var profiles = await new SessionRepository().LoadAsync(cancellationToken).ConfigureAwait(false);
        return Create(profiles, SshTools.ResolveProfile(profiles, session));
    }

    public static SessionContext Create(
        IReadOnlyList<ConnectionProfile> profiles,
        ConnectionProfile profile,
        SshConnectionPool? pool = null)
    {
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
            throw new InvalidOperationException(
                $"Session '{profile.Name}' has no saved private key. Password authentication is intentionally unavailable to the MCP server.");

        var privateKeyPath = Environment.ExpandEnvironmentVariables(profile.PrivateKeyPath);
        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException($"The saved private key for session '{profile.Name}' does not exist.");

        return new SessionContext(
            pool ?? SshConnectionPool.Shared,
            profiles,
            profile,
            new SshTarget(profile, SshRouteResolver.Resolve(profile, profiles), privateKeyPath));
    }

    /// <summary>Feeds a POSIX script to remote <c>sh -s</c> over stdin.</summary>
    public Task<int> RunScriptAsync(
        string operation,
        string script,
        IOutputSink stdout,
        IOutputSink stderr,
        int timeoutSeconds,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            operation, "sh -s", Encoding.UTF8.GetBytes(script), stdout, stderr, timeoutSeconds, cancellationToken);

    public async Task<int> ExecuteAsync(
        string operation,
        string command,
        ReadOnlyMemory<byte> stdin,
        IOutputSink stdout,
        IOutputSink stderr,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await Pool.ExecuteAsync(Target, command, stdin, stdout, stderr, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"{operation} timed out after {timeoutSeconds} seconds on session '{Profile.Name}'.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HostKeyRejectedException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"{operation} failed on session '{Profile.Name}': {SshTools.SanitizeError(error.Message, Profiles)}");
        }
    }
}

internal static class PosixShell
{
    public static string Quote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Validates a remote file path and returns it as a shell word. Absolute paths
    /// and <c>~/</c>-relative paths are accepted; the latter expand through $HOME.
    /// </summary>
    public static string PathWord(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Remote path is required.", parameterName);
        if (path.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("Remote path cannot contain NUL or line-break characters.", parameterName);
        if (path.EndsWith('/'))
            throw new ArgumentException("Remote path must name a file, not end with '/'.", parameterName);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return "\"$HOME\"/" + Quote(path[2..]);
        if (!path.StartsWith('/'))
            throw new ArgumentException("Remote path must be absolute or start with '~/'.", parameterName);
        return Quote(path);
    }
}
