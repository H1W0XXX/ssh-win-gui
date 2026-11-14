using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace RsyncShell.Mcp;

[McpServerToolType]
public sealed partial class FileTools
{
    internal const int MaximumFileBytes = 8 * 1024 * 1024;
    private const int TimeoutSeconds = 120;
    private const int MaximumReadBytes = 256 * 1024;
    private const int MaximumReadLines = 5000;

    /// <summary>
    /// Runs a script that arrives on stdin and leaves the rest of stdin to it.
    /// The command line is constant, so no path or content is ever quoted for the
    /// remote login shell; the byte-exact reads keep the script and the payload apart.
    /// </summary>
    private const string StdinScriptCommand =
        "sh -c 'IFS= read -r n; s=$(dd bs=1 count=\"$n\" 2>/dev/null); eval \"$s\"'";

    [McpServerTool(Name = "read_file", ReadOnly = true, Idempotent = true, OpenWorld = true),
     Description("Reads a range of lines from a text file on a configured ssh-win-gui session. Cheaper than run_script with cat: only the requested lines travel, and the header tells you the total line count and the offset to continue from.")]
    public static async Task<string> ReadFileAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("Remote file path, absolute or starting with '~/'.")] string path,
        [Description("1-based line to start from. Defaults to 1.")] int offset = 1,
        [Description("Maximum number of lines to return, from 1 to 5000. Defaults to 400.")] int limit = 400,
        [Description("Maximum bytes of file content to return, from 1024 to 262144. Defaults to 32768.")] int maxBytes = 32 * 1024,
        [Description("Prefix each line with its line number and a tab. Defaults to true.")] bool lineNumbers = true,
        CancellationToken cancellationToken = default)
    {
        var pathWord = PosixShell.PathWord(path, nameof(path));
        if (offset < 1)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset is a 1-based line number.");
        if (limit is < 1 or > MaximumReadLines)
            throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {MaximumReadLines} lines.");
        if (maxBytes is < 1024 or > MaximumReadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), $"maxBytes must be between 1024 and {MaximumReadBytes}.");

        var context = await SessionContext.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        return await ReadFileAsync(context, path, pathWord, offset, limit, maxBytes, lineNumbers, cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool(Name = "edit_file", Destructive = true, OpenWorld = true),
     Description("Edits a text file on a configured ssh-win-gui session by replacing an exact string, without resending the file. oldString must match the file exactly (including indentation) and be unique unless replaceAll is true. The file is replaced atomically and keeps its permissions; nothing is written if it changed in the meantime.")]
    public static async Task<string> EditFileAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("Remote file path, absolute or starting with '~/'.")] string path,
        [Description("Exact text to replace. Include enough surrounding lines to match only one place.")] string oldString,
        [Description("Replacement text. May be empty to delete oldString.")] string newString,
        [Description("Replace every occurrence instead of requiring a unique match. Defaults to false.")] bool replaceAll = false,
        CancellationToken cancellationToken = default)
    {
        var pathWord = PosixShell.PathWord(path, nameof(path));
        ValidateEdit(oldString, newString);
        var context = await SessionContext.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        return await EditFileAsync(context, path, pathWord, oldString, newString, replaceAll, cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool(Name = "write_file", Destructive = true, OpenWorld = true),
     Description("Creates a text file on a configured ssh-win-gui session with the given content, creating parent directories as needed. An existing file is replaced only when overwrite is true; to change part of an existing file use edit_file instead. For binary or large files use rsync_upload.")]
    public static async Task<string> WriteFileAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("Remote file path, absolute or starting with '~/'.")] string path,
        [Description("Complete file content, written verbatim as UTF-8.")] string content,
        [Description("Whether an existing file may be replaced. Defaults to false.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var pathWord = PosixShell.PathWord(path, nameof(path));
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaximumFileBytes)
            throw new ArgumentException("content is larger than 8 MiB; use rsync_upload instead.", nameof(content));

        var context = await SessionContext.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        return await WriteFileAsync(context, path, pathWord, bytes, overwrite, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> WriteFileAsync(
        SessionContext context,
        string path,
        string pathWord,
        byte[] content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var existed = await WriteAsync(
            context,
            pathWord,
            content,
            overwrite ? WriteMode.Overwrite : WriteMode.Create,
            expectedChecksum: null,
            cancellationToken).ConfigureAwait(false);
        return $"{(existed ? "Replaced" : "Created")} {path} ({content.Length} bytes).";
    }

    internal static async Task<string> ReadFileAsync(
        SessionContext context,
        string path,
        string pathWord,
        int offset,
        int limit,
        int maxBytes,
        bool lineNumbers,
        CancellationToken cancellationToken)
    {
        var last = (long)offset + limit - 1;
        var script = $$"""
            p={{pathWord}}
            {{RegularFileChecks}}
            size=$(($(wc -c < "$p")))
            lines=$(($(wc -l < "$p")))
            if [ "$size" -gt 0 ] && [ "$(($(tail -c 1 "$p" | wc -l)))" -eq 0 ]; then lines=$((lines + 1)); fi
            echo "$size $lines"
            sed -n '{{offset}},{{last}}p;{{last}}q' "$p"
            """;
        // One byte beyond the limit distinguishes "exactly full" from "cut off".
        var stdout = new OutputCapture(maxBytes + 64 + 1);
        await RunAsync(context, "Remote file read", "sh -s", Encoding.UTF8.GetBytes(script), stdout, cancellationToken)
            .ConfigureAwait(false);

        var (header, body) = SplitFirstLine(stdout.ToArray());
        var fields = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 ||
            !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var totalLines))
            throw new InvalidOperationException("Remote file read returned an unexpected header.");

        return FormatRead(path, body, offset, size, totalLines, maxBytes, lineNumbers);
    }

    internal static string FormatRead(
        string path,
        ReadOnlySpan<byte> body,
        int offset,
        long size,
        long totalLines,
        int maxBytes,
        bool lineNumbers)
    {
        if (size == 0)
            return $"[{path}: empty file]";
        if (offset > totalLines)
            return $"[{path}: {totalLines} lines; offset {offset} is past the end]";
        if (body.IsEmpty)
            return $"[{path}: {totalLines} lines; nothing returned from offset {offset}, the file may have changed]";
        if (body.IndexOf((byte)0) >= 0)
            throw new InvalidOperationException($"{path} looks like a binary file; use run_script or rsync_download.");

        var cutLine = false;
        if (body.Length > maxBytes)
        {
            body = body[..maxBytes];
            var lastNewline = body.LastIndexOf((byte)'\n');
            if (lastNewline >= 0)
            {
                body = body[..(lastNewline + 1)];
            }
            else
            {
                body = OutputCapture.TrimIncompleteEnd(body);
                cutLine = true;
            }
        }

        var text = Encoding.UTF8.GetString(body);
        var lines = text.Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        var end = offset + count - 1;

        var result = new StringBuilder(text.Length + count * 6 + 96);
        result.Append('[').Append(path).Append(": lines ").Append(offset).Append('-').Append(end)
            .Append(" of ").Append(totalLines);
        if (cutLine)
            result.Append("; line ").Append(end).Append(" is longer than maxBytes and was cut");
        if (end < totalLines)
            result.Append("; continue with offset=").Append(end + 1);
        result.Append("]\n");
        for (var index = 0; index < count; index++)
        {
            if (lineNumbers)
                result.Append(offset + index).Append('\t');
            result.Append(lines[index].TrimEnd('\r')).Append('\n');
        }
        return result.ToString().TrimEnd('\n');
    }

    internal static async Task<string> EditFileAsync(
        SessionContext context,
        string path,
        string pathWord,
        string oldString,
        string newString,
        bool replaceAll,
        CancellationToken cancellationToken)
    {
        var script = $$"""
            p={{pathWord}}
            {{RegularFileChecks}}
            if [ "$(($(wc -c < "$p")))" -gt {{MaximumFileBytes}} ]; then
              echo "File is larger than 8 MiB; change it with run_script instead: $p" >&2
              exit 27
            fi
            cksum < "$p" 2>/dev/null || echo
            cat "$p"
            """;
        var stdout = new OutputCapture(MaximumFileBytes + 4096);
        await RunAsync(context, "Remote file edit", "sh -s", Encoding.UTF8.GetBytes(script), stdout, cancellationToken)
            .ConfigureAwait(false);
        if (stdout.Truncated)
            throw new InvalidOperationException($"{path} grew beyond 8 MiB while it was being read.");

        var (checksum, original) = SplitFirstLine(stdout.ToArray());
        var edit = ApplyEdit(original, oldString, newString, replaceAll);
        await WriteAsync(
            context,
            pathWord,
            edit.Content,
            WriteMode.Edit,
            // Without a usable checksum the write still happens; the exact-match
            // requirement above already guards the edited region itself.
            ChecksumPattern().IsMatch(checksum) ? checksum : null,
            cancellationToken).ConfigureAwait(false);

        var shown = string.Join(", ", edit.Lines.Take(10));
        var more = edit.Lines.Count > 10 ? ", ..." : string.Empty;
        return edit.Lines.Count == 1
            ? $"Replaced 1 occurrence in {path} at line {shown}."
            : $"Replaced {edit.Lines.Count} occurrences in {path} at lines {shown}{more}.";
    }

    internal static void ValidateEdit(string oldString, string newString)
    {
        if (string.IsNullOrEmpty(oldString))
            throw new ArgumentException("oldString must not be empty. Use write_file to create a file.", nameof(oldString));
        ArgumentNullException.ThrowIfNull(newString);
        if (string.Equals(oldString, newString, StringComparison.Ordinal))
            throw new ArgumentException("oldString and newString are identical; nothing to change.", nameof(newString));
    }

    /// <summary>
    /// Replaces on raw bytes, so text in any ASCII-compatible encoding survives untouched
    /// outside the edited region.
    /// </summary>
    internal static EditResult ApplyEdit(ReadOnlySpan<byte> content, string oldString, string newString, bool replaceAll)
    {
        ValidateEdit(oldString, newString);
        var search = Encoding.UTF8.GetBytes(oldString);
        var replacement = Encoding.UTF8.GetBytes(newString);
        var matches = FindAll(content, search);
        if (matches.Count == 0 && content.IndexOf("\r\n"u8) >= 0 && !oldString.Contains('\r'))
        {
            // The file uses CRLF line endings but the caller wrote plain LF.
            search = Encoding.UTF8.GetBytes(oldString.Replace("\n", "\r\n", StringComparison.Ordinal));
            replacement = Encoding.UTF8.GetBytes(newString
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal));
            matches = FindAll(content, search);
        }

        if (matches.Count == 0)
            throw new InvalidOperationException(
                "oldString was not found. It must match the file exactly, including whitespace and indentation; read the file again.");

        var lines = new List<int>(matches.Count);
        var line = 1;
        var scanned = 0;
        foreach (var match in matches)
        {
            line += content[scanned..match].Count((byte)'\n');
            scanned = match;
            lines.Add(line);
        }
        if (matches.Count > 1 && !replaceAll)
            throw new InvalidOperationException(
                $"oldString matches {matches.Count} places (lines {string.Join(", ", lines.Take(10))}{(lines.Count > 10 ? ", ..." : string.Empty)}). " +
                "Add surrounding lines to make it unique, or set replaceAll=true.");

        var result = new byte[content.Length + (long)matches.Count * (replacement.Length - search.Length)];
        var source = 0;
        var target = 0;
        foreach (var match in matches)
        {
            content[source..match].CopyTo(result.AsSpan(target));
            target += match - source;
            replacement.CopyTo(result.AsSpan(target));
            target += replacement.Length;
            source = match + search.Length;
        }
        content[source..].CopyTo(result.AsSpan(target));
        return new EditResult(result, lines);
    }

    private static List<int> FindAll(ReadOnlySpan<byte> content, ReadOnlySpan<byte> search)
    {
        var matches = new List<int>();
        var start = 0;
        while (start <= content.Length - search.Length)
        {
            var index = content[start..].IndexOf(search);
            if (index < 0)
                break;
            matches.Add(start + index);
            start += index + search.Length;
        }
        return matches;
    }

    /// <summary>Uploads <paramref name="content"/> and swaps it into place; returns whether the file existed.</summary>
    private static async Task<bool> WriteAsync(
        SessionContext context,
        string pathWord,
        byte[] content,
        WriteMode mode,
        string? expectedChecksum,
        CancellationToken cancellationToken)
    {
        var modeCheck = mode switch
        {
            WriteMode.Create =>
                """if [ "$existed" = 1 ]; then echo "File already exists: $p. Set overwrite=true to replace it." >&2; exit 17; fi""",
            WriteMode.Edit =>
                """if [ "$existed" = 0 ]; then echo "File no longer exists: $p" >&2; exit 66; fi""",
            _ => ":",
        };
        var unchangedCheck = expectedChecksum is null
            ? ":"
            : $$"""if [ "$(cksum < "$p")" != '{{expectedChecksum}}' ]; then echo "The file changed while it was being edited; nothing was written. Read it again and retry." >&2; exit 75; fi""";
        var script = $$"""
            set -eu
            p={{pathWord}}
            if [ -L "$p" ]; then
              r=$(readlink -f "$p" 2>/dev/null || realpath "$p" 2>/dev/null || true)
              if [ -z "$r" ]; then echo "Cannot resolve symbolic link: $p" >&2; exit 20; fi
              p=$r
            fi
            if [ -d "$p" ]; then echo "Path is a directory: $p" >&2; exit 21; fi
            existed=0
            if [ -e "$p" ]; then
              existed=1
              if [ ! -f "$p" ]; then echo "Not a regular file: $p" >&2; exit 22; fi
            fi
            {{modeCheck}}
            d=$(dirname "$p")
            mkdir -p "$d"
            if [ -w "$d" ]; then
              t="$d/.$(basename "$p").mcp$$"
              trap 'rm -f "$t"' EXIT
              if [ "$existed" = 1 ]; then cp -p "$p" "$t"; fi
              cat > "$t"
              got=$(($(wc -c < "$t")))
              if [ "$got" -ne {{content.Length}} ]; then
                echo "Upload was incomplete ($got of {{content.Length}} bytes); the file was not changed." >&2
                exit 74
              fi
              {{unchangedCheck}}
              mv -f "$t" "$p"
              trap - EXIT
            else
              {{unchangedCheck}}
              cat > "$p"
              got=$(($(wc -c < "$p")))
              if [ "$got" -ne {{content.Length}} ]; then
                echo "Upload was incomplete ($got of {{content.Length}} bytes); $p may be truncated." >&2
                exit 74
              fi
            fi
            echo "$existed"
            """;

        var scriptBytes = Encoding.UTF8.GetBytes(script);
        var prefix = Encoding.ASCII.GetBytes(scriptBytes.Length.ToString(CultureInfo.InvariantCulture) + "\n");
        var stdin = new byte[prefix.Length + scriptBytes.Length + content.Length];
        prefix.CopyTo(stdin, 0);
        scriptBytes.CopyTo(stdin, prefix.Length);
        content.CopyTo(stdin, prefix.Length + scriptBytes.Length);

        var stdout = new OutputCapture(1024);
        await RunAsync(context, "Remote file write", StdinScriptCommand, stdin, stdout, cancellationToken)
            .ConfigureAwait(false);
        return stdout.GetText().Trim() == "1";
    }

    private static async Task RunAsync(
        SessionContext context,
        string operation,
        string command,
        byte[] stdin,
        OutputCapture stdout,
        CancellationToken cancellationToken)
    {
        var stderr = new OutputCapture(4096);
        var exitCode = await context.ExecuteAsync(
            operation, command, stdin, stdout, stderr, TimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0)
            return;
        var message = stderr.GetText().Trim();
        throw new InvalidOperationException(message.Length > 0
            ? message
            : $"{operation} failed with exit code {exitCode} on session '{context.Profile.Name}'.");
    }

    private static (string FirstLine, byte[] Remainder) SplitFirstLine(byte[] bytes)
    {
        var newline = Array.IndexOf(bytes, (byte)'\n');
        return newline < 0
            ? (Encoding.UTF8.GetString(bytes).Trim(), [])
            : (Encoding.UTF8.GetString(bytes, 0, newline).Trim(), bytes[(newline + 1)..]);
    }

    private const string RegularFileChecks =
        """
        if [ ! -e "$p" ]; then echo "No such file: $p" >&2; exit 66; fi
        if [ ! -f "$p" ]; then echo "Not a regular file: $p" >&2; exit 67; fi
        if [ ! -r "$p" ]; then echo "Permission denied: $p" >&2; exit 77; fi
        """;

    [GeneratedRegex("^[0-9]+ [0-9]+$")]
    private static partial Regex ChecksumPattern();

    private enum WriteMode
    {
        Create,
        Overwrite,
        Edit,
    }

    internal sealed record EditResult(byte[] Content, IReadOnlyList<int> Lines);
}
