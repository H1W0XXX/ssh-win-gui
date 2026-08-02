using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using RsyncShell.Core.Models;

namespace RsyncShell.Core.Services;

public sealed class RemoteFileService
{
    internal const int DirectoryEntryLimit = 5_000;
    internal const int ResponseByteLimit = 8 * 1024 * 1024;
    internal const string Marker = "__RSYNCSHELL_JSON__";
    private const int ResponseFramingAllowance = 64 * 1024;
    private const int ErrorByteLimit = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SshClientFactory _clientFactory = new();

    public async Task<RemoteDirectoryListing> ListAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        string path,
        IReadOnlyList<ConnectionProfile>? route = null,
        CancellationToken cancellationToken = default)
    {
        var python = BuildListScript(path);

        if (profile.ProxyKind == SshProxyKind.JumpHost)
        {
            return await ListThroughJumpAsync(
                profile, authentication, verifyHostKey, route ?? [profile], python, cancellationToken)
                .ConfigureAwait(false);
        }

        using var session = await _clientFactory.ConnectAsync(
            profile, authentication, verifyHostKey, route, cancellationToken).ConfigureAwait(false);
        using var command = session.Client.CreateCommand("python3 -c " + QuoteForPosixShell(python));

        using var outputLimitCancellation = new CancellationTokenSource();
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            outputLimitCancellation.Token);
        var limitSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = command.ExecuteAsync(commandCancellation.Token);
        await RejectPreCanceledExecutionAsync(execution).ConfigureAwait(false);
        var stdoutRead = ReadBoundedAsync(
            command.OutputStream,
            ResponseByteLimit + ResponseFramingAllowance,
            "stdout",
            limitSignal);
        var stderrRead = ReadBoundedAsync(
            command.ExtendedOutputStream,
            ErrorByteLimit,
            "stderr",
            limitSignal);

        var firstCompletion = await Task.WhenAny(
            execution,
            limitSignal.Task,
            stdoutRead,
            stderrRead).ConfigureAwait(false);
        if (limitSignal.Task.IsCompleted ||
            (firstCompletion != execution && !execution.IsCompleted))
        {
            outputLimitCancellation.Cancel();
        }

        Exception? executionError = null;
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            executionError = ex;
        }

        var stdoutResult = await stdoutRead.ConfigureAwait(false);
        var stderrResult = await stderrRead.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stdoutResult.LimitExceeded)
        {
            throw new InvalidOperationException(
                "Remote directory stdout exceeded the 8 MiB safety limit; the command was cancelled.");
        }
        if (stderrResult.LimitExceeded)
        {
            throw new InvalidOperationException(
                "Remote directory stderr exceeded the 16 KiB safety limit; the command was cancelled.");
        }
        var readErrors = new[] { stdoutResult.Error, stderrResult.Error }
            .Where(error => error is not null)
            .Cast<Exception>()
            .ToArray();
        if (readErrors.Length > 0)
        {
            var inner = readErrors.Length == 1 ? readErrors[0] : new AggregateException(readErrors);
            throw new InvalidOperationException("Failed to read bounded remote directory output.", inner);
        }
        if (executionError is not null)
        {
            ExceptionDispatchInfo.Capture(executionError).Throw();
        }

        var stdout = Encoding.UTF8.GetString(stdoutResult.Content);
        var stderr = Encoding.UTF8.GetString(stderrResult.Content);
        if (command.ExitStatus is not 0)
        {
            var error = stderr.Trim();
            if (error.Length > 4_096)
            {
                error = error[..4_096] + "...";
            }
            throw new InvalidOperationException(
                $"Remote directory command failed with exit code {command.ExitStatus}: {error}");
        }

        return ParseResponse(stdout, cancellationToken);
    }

    public Task RenameAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        string sourcePath,
        string newName,
        IReadOnlyList<ConnectionProfile>? route = null,
        CancellationToken cancellationToken = default) =>
        ExecuteMutationAsync(
            profile,
            authentication,
            verifyHostKey,
            route ?? [profile],
            BuildRenameScript(sourcePath, newName),
            cancellationToken);

    public Task DeleteAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyCollection<string> paths,
        IReadOnlyList<ConnectionProfile>? route = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one remote path is required.", nameof(paths));
        }

        return ExecuteMutationAsync(
            profile,
            authentication,
            verifyHostKey,
            route ?? [profile],
            BuildDeleteScript(paths),
            cancellationToken);
    }

    private async Task ExecuteMutationAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile> route,
        string python,
        CancellationToken cancellationToken)
    {
        if (profile.ProxyKind == SshProxyKind.JumpHost)
        {
            await ExecuteMutationThroughJumpAsync(
                profile, authentication, verifyHostKey, route, python, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var session = await _clientFactory.ConnectAsync(
            profile, authentication, verifyHostKey, route, cancellationToken).ConfigureAwait(false);
        using var command = session.Client.CreateCommand("python3 -c " + QuoteForPosixShell(python));
        using var outputLimitCancellation = new CancellationTokenSource();
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            outputLimitCancellation.Token);
        var limitSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = command.ExecuteAsync(commandCancellation.Token);
        await RejectPreCanceledExecutionAsync(execution).ConfigureAwait(false);
        var stdoutRead = ReadBoundedAsync(command.OutputStream, ErrorByteLimit, "stdout", limitSignal);
        var stderrRead = ReadBoundedAsync(command.ExtendedOutputStream, ErrorByteLimit, "stderr", limitSignal);
        var firstCompletion = await Task.WhenAny(execution, limitSignal.Task, stdoutRead, stderrRead).ConfigureAwait(false);
        if (limitSignal.Task.IsCompleted || firstCompletion != execution && !execution.IsCompleted)
        {
            outputLimitCancellation.Cancel();
        }

        Exception? executionError = null;
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            executionError = error;
        }

        var stdoutResult = await stdoutRead.ConfigureAwait(false);
        var stderrResult = await stderrRead.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stdoutResult.LimitExceeded || stderrResult.LimitExceeded)
        {
            throw new InvalidOperationException("Remote file operation output exceeded the 16 KiB safety limit.");
        }
        var readError = stdoutResult.Error ?? stderrResult.Error;
        if (readError is not null)
        {
            throw new InvalidOperationException("Failed to read remote file operation output.", readError);
        }
        if (executionError is not null)
        {
            ExceptionDispatchInfo.Capture(executionError).Throw();
        }
        if (command.ExitStatus is not 0)
        {
            var error = Encoding.UTF8.GetString(stderrResult.Content).Trim();
            throw new InvalidOperationException(
                $"Remote file operation failed with exit code {command.ExitStatus}: {error}");
        }
    }

    private static async Task ExecuteMutationThroughJumpAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile> route,
        string python,
        CancellationToken cancellationToken)
    {
        using var client = JumpSshClientFactory.Create(profile, authentication, route, verifyHostKey);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var process = await client.ExecuteAsync(
            "python3 -c " + QuoteForPosixShell(python), cancellationToken).ConfigureAwait(false);
        process.WriteEof();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var stdoutBuffer = new byte[4 * 1024];
        var stderrBuffer = new byte[4 * 1024];
        while (true)
        {
            var (isError, read) = await process.ReadAsync(
                stdoutBuffer, stderrBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            var destination = isError ? stderr : stdout;
            var source = isError ? stderrBuffer : stdoutBuffer;
            if (destination.Length + read > ErrorByteLimit)
            {
                throw new InvalidOperationException("Remote file operation output exceeded the 16 KiB safety limit.");
            }
            destination.Write(source, 0, read);
        }
        var exitCode = await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            var error = Encoding.UTF8.GetString(stderr.ToArray()).Trim();
            throw new InvalidOperationException(
                $"Remote file operation failed with exit code {exitCode}: {error}");
        }
    }

    private static async Task<RemoteDirectoryListing> ListThroughJumpAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile> route,
        string python,
        CancellationToken cancellationToken)
    {
        using var client = JumpSshClientFactory.Create(profile, authentication, route, verifyHostKey);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var process = await client.ExecuteAsync(
            "python3 -c " + QuoteForPosixShell(python), cancellationToken).ConfigureAwait(false);
        process.WriteEof();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var stdoutBuffer = new byte[16 * 1024];
        var stderrBuffer = new byte[16 * 1024];
        while (true)
        {
            var (isError, read) = await process.ReadAsync(
                stdoutBuffer, stderrBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var destination = isError ? stderr : stdout;
            var source = isError ? stderrBuffer : stdoutBuffer;
            var limit = isError ? ErrorByteLimit : ResponseByteLimit + ResponseFramingAllowance;
            if (destination.Length + read > limit)
                throw new InvalidOperationException(isError
                    ? "Remote directory stderr exceeded the 16 KiB safety limit."
                    : "Remote directory stdout exceeded the 8 MiB safety limit.");
            destination.Write(source, 0, read);
        }
        var exitCode = await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
        var standardError = Encoding.UTF8.GetString(stderr.ToArray()).Trim();
        if (exitCode != 0)
            throw new InvalidOperationException($"Remote directory command failed with exit code {exitCode}: {standardError}");
        return ParseResponse(Encoding.UTF8.GetString(stdout.ToArray()), cancellationToken);
    }

    internal static string BuildListScript(string path)
    {
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var python = $$"""
                       import base64
                       import json
                       import os
                       import stat
                       import sys

                       p = os.path.abspath(os.path.expanduser(base64.b64decode('{{encodedPath}}').decode('utf-8')))
                       items = []
                       truncated = False
                       try:
                           with os.scandir(p) as iterator:
                               for entry in iterator:
                                   if len(items) >= {{DirectoryEntryLimit}}:
                                       truncated = True
                                       break
                                   try:
                                       details = entry.stat(follow_symlinks=False)
                                       is_directory = entry.is_dir(follow_symlinks=False)
                                       items.append({
                                           'name': entry.name,
                                           'path': os.path.join(p, entry.name),
                                           'isDirectory': is_directory,
                                           'isSymbolicLink': entry.is_symlink(),
                                           'size': 0 if is_directory else int(details.st_size),
                                           'modifiedUnix': int(details.st_mtime),
                                           'mode': stat.filemode(details.st_mode),
                                       })
                                   except OSError:
                                       pass
                       except OSError as error:
                           sys.stderr.write(f'Cannot list directory: {error}\n')
                           sys.exit(72)

                       items.sort(key=lambda item: (not item['isDirectory'], item['name'].casefold()))
                       payload = json.dumps({
                           'path': p,
                           'entries': items,
                           'isTruncated': truncated,
                           'entryLimit': {{DirectoryEntryLimit}},
                       }, ensure_ascii=False, separators=(',', ':'))
                       if len(payload.encode('utf-8')) > {{ResponseByteLimit}}:
                           sys.stderr.write('Directory response exceeds the 8 MiB safety limit; enter a narrower path.\n')
                           sys.exit(73)
                       print('{{Marker}}' + payload)
                       """;
        return python;
    }

    internal static string BuildRenameScript(string sourcePath, string newName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("The source path is required.", nameof(sourcePath));
        }
        newName = newName.Trim();
        if (newName.Length == 0 || newName is "." or ".." || newName.Contains('/') || newName.Contains('\0'))
        {
            throw new ArgumentException("The new name is not a valid single path component.", nameof(newName));
        }

        var encodedSource = Convert.ToBase64String(Encoding.UTF8.GetBytes(sourcePath));
        var encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(newName));
        return $$"""
                 import base64
                 import os
                 import sys

                 source = os.path.abspath(os.path.expanduser(base64.b64decode('{{encodedSource}}').decode('utf-8')))
                 name = base64.b64decode('{{encodedName}}').decode('utf-8')
                 if source == '/':
                     sys.stderr.write('Refusing to rename the root directory.\n')
                     sys.exit(64)
                 target = os.path.join(os.path.dirname(source), name)
                 if not os.path.lexists(source):
                     sys.stderr.write('The source path no longer exists.\n')
                     sys.exit(66)
                 if os.path.lexists(target):
                     sys.stderr.write('A file or directory with that name already exists.\n')
                     sys.exit(17)
                 os.rename(source, target)
                 """;
    }

    internal static string BuildDeleteScript(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one remote path is required.", nameof(paths));
        }
        var encodedPaths = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paths)));
        return $$"""
                 import base64
                 import json
                 import os
                 import shutil
                 import sys

                 paths = json.loads(base64.b64decode('{{encodedPaths}}').decode('utf-8'))
                 paths = [os.path.abspath(os.path.expanduser(path)) for path in paths]
                 if any(path == '/' for path in paths):
                     sys.stderr.write('Refusing to delete the root directory.\n')
                     sys.exit(64)
                 for path in paths:
                     if not os.path.lexists(path):
                         sys.stderr.write(f'The path no longer exists: {path}\n')
                         sys.exit(66)
                 for path in paths:
                     if os.path.islink(path) or not os.path.isdir(path):
                         os.unlink(path)
                     else:
                         shutil.rmtree(path)
                 """;
    }

    internal static RemoteDirectoryListing ParseResponse(
        string stdout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Encoding.UTF8.GetByteCount(stdout) > ResponseByteLimit + ResponseFramingAllowance)
        {
            throw new InvalidOperationException(
                "Remote directory response exceeded the 8 MiB safety limit; enter a narrower path.");
        }

        var markerIndex = stdout.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException("Remote directory response did not contain the JSON marker.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(
            stdout.AsMemory(markerIndex + Marker.Length),
            new JsonDocumentOptions
            {
                MaxDepth = 16,
            });
        var listing = document.RootElement.Deserialize<RemoteDirectoryListing>(JsonOptions)
                      ?? throw new InvalidOperationException("Remote directory response was empty.");
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(listing.Path) || !listing.Path.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Remote directory response did not contain an absolute POSIX path.");
        }
        if (listing.Entries is null)
        {
            throw new InvalidOperationException("Remote directory response did not contain an entry list.");
        }
        if (listing.Entries.Count > DirectoryEntryLimit)
        {
            throw new InvalidOperationException("Remote directory response exceeded the entry safety limit.");
        }
        if (listing.EntryLimit != DirectoryEntryLimit)
        {
            throw new InvalidOperationException("Remote directory response used an unexpected entry limit.");
        }

        return listing;
    }

    internal static async Task<BoundedStreamRead> ReadBoundedAsync(
        Stream stream,
        int byteLimit,
        string streamName,
        TaskCompletionSource<string> limitSignal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLimit);
        using var content = new MemoryStream(Math.Min(byteLimit, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var limitExceeded = false;

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var remaining = byteLimit - (int)content.Length;
                if (remaining > 0)
                {
                    content.Write(buffer, 0, Math.Min(remaining, read));
                }
                if (read > remaining && !limitExceeded)
                {
                    limitExceeded = true;
                    limitSignal.TrySetResult(streamName);
                }
            }

            return new BoundedStreamRead(content.ToArray(), limitExceeded, null);
        }
        catch (Exception error)
        {
            limitSignal.TrySetResult(streamName);
            return new BoundedStreamRead(Array.Empty<byte>(), limitExceeded, error);
        }
    }

    internal static async Task RejectPreCanceledExecutionAsync(Task execution)
    {
        // SSH.NET returns Task.FromCanceled before it initializes command lifetime state.
        // Starting PipeStream readers in that branch would leave them waiting forever.
        if (execution.IsCanceled)
        {
            await execution.ConfigureAwait(false);
        }
    }

    internal sealed record BoundedStreamRead(byte[] Content, bool LimitExceeded, Exception? Error);

    private static string QuoteForPosixShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
