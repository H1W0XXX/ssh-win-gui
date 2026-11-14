using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace RsyncShell.Mcp;

/// <summary>
/// Detached remote jobs. Each job lives in a directory under the remote user's
/// cache folder holding its script, merged log, pid and exit status, so it keeps
/// running after the SSH channel closes and any later call can find it again.
/// </summary>
[McpServerToolType]
public sealed partial class JobTools
{
    private const int TimeoutSeconds = 60;
    private const int DefaultLogBytes = 8 * 1024;
    private const int MaximumLogBytes = 64 * 1024;
    private const int ListedJobs = 20;
    private const string JobsDirectory = "\"${XDG_CACHE_HOME:-$HOME/.cache}/ssh-win-gui-mcp/jobs\"";

    /// <summary>Prints "id state exitCode logBytes elapsedSeconds" for one job directory.</summary>
    private const string DescribeFunction =
        """
        describe() {
          jd=$1
          pid=$(cat "$jd/pid" 2>/dev/null || true)
          code=-
          finished=
          now=$(date +%s)
          started=$(cat "$jd/started" 2>/dev/null || true)
          started=${started:-$now}
          elapsed=0
          if [ -f "$jd/exit" ]; then
            state=exited
            read -r code finished < "$jd/exit" || true
            elapsed=$((${finished:-$now} - started))
          elif [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
            state=running
            elapsed=$((now - started))
          elif [ -f "$jd/stopped" ]; then
            state=stopped
            read -r finished < "$jd/stopped" || true
            # Older versions created an empty marker; its mtime is the stop time.
            if [ -z "$finished" ]; then
              finished=$(stat -c %Y "$jd/stopped" 2>/dev/null || stat -f %m "$jd/stopped" 2>/dev/null || echo "$now")
            fi
            elapsed=$((finished - started))
          else
            state=lost
          fi
          size=0
          if [ -f "$jd/log" ]; then size=$(($(wc -c < "$jd/log"))); fi
          echo "${jd##*/} $state ${code:--} $size $elapsed"
        }
        """;

    [McpServerTool(Name = "start_job", Destructive = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(JobStartResult)),
     Description("Starts a POSIX shell script as a detached background job on a configured ssh-win-gui session and returns immediately. Use it for builds, tests, training runs, or downloads that may outlive run_script's 600-second limit. The job survives the SSH connection; stdout and stderr are merged into a log file on the remote host. Poll it with job_status and cancel it with stop_job.")]
    public static async Task<JobStartResult> StartJobAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("POSIX shell script run by remote `sh` from the home directory. Start with `cd` to work elsewhere.")] string script,
        [Description("Optional short label used as the job ID prefix: 1 to 24 letters, digits, '_' or '-'.")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (script.Contains('\0'))
            throw new ArgumentException("script cannot contain NUL characters.", nameof(script));
        var jobId = CreateJobId(name, DateTimeOffset.UtcNow);
        var context = await SessionContext.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        return await StartJobAsync(context, jobId, script, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "job_status", ReadOnly = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(JobStatusResult)),
     Description("Reports a background job started with start_job: state (running, exited, stopped, or lost), exit code, and a bounded part of its log. Pass the previous nextOffset as sinceOffset to receive only output written since the last poll. Omit jobId to list the session's most recent jobs. The log is a plain remote file, so run_script can grep it for anything more specific.")]
    public static async Task<JobStatusResult> JobStatusAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("Job ID returned by start_job. Omit to list recent jobs without log output.")] string? jobId = null,
        [Description("Log byte offset to read from. Defaults to 0; pass the previous nextOffset to get only new output.")] long sinceOffset = 0,
        [Description("Maximum log bytes to return, from 0 to 65536. Defaults to 8192; 0 returns status only.")] int maxOutputBytes = DefaultLogBytes,
        [Description("Which part of an over-long log range to keep: 'head_tail' (default), 'tail', or 'head'.")] string outputMode = "head_tail",
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(jobId))
            ValidateJobId(jobId);
        ArgumentOutOfRangeException.ThrowIfNegative(sinceOffset);
        if (maxOutputBytes is < 0 or > MaximumLogBytes)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes), $"maxOutputBytes must be between 0 and {MaximumLogBytes}.");
        var mode = ScriptOptions.ParseMode(outputMode);

        var context = await SessionContext.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(jobId)
            ? await ListJobsAsync(context, cancellationToken).ConfigureAwait(false)
            : await JobStatusAsync(context, jobId.Trim(), sinceOffset, maxOutputBytes, mode, cancellationToken)
                .ConfigureAwait(false);
    }

    [McpServerTool(Name = "stop_job", Destructive = true, Idempotent = true, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(JobInfo)),
     Description("Stops a background job started with start_job: sends SIGTERM to the job's process group, then SIGKILL if it is still alive after 5 seconds. Returns the job's final state; a job that already finished is left as it is.")]
    public static async Task<JobInfo> StopJobAsync(
        [Description("Session ID from list_sessions, or an exact configured session name.")] string session,
        [Description("Job ID returned by start_job.")] string jobId,
        CancellationToken cancellationToken = default)
    {
        ValidateJobId(jobId);
        var context = await SessionContext.OpenAsync(session, cancellationToken).ConfigureAwait(false);
        return await StopJobAsync(context, jobId.Trim(), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<JobStartResult> StartJobAsync(
        SessionContext context,
        string jobId,
        string script,
        CancellationToken cancellationToken)
    {
        // A quoted here-document passes the script through byte for byte; the random
        // delimiter only has to be absent from the script itself.
        string delimiter;
        do
        {
            delimiter = "MCP_JOB_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        }
        while (script.Contains(delimiter, StringComparison.Ordinal));

        var launcher = $$"""
            set -eu
            base={{JobsDirectory}}
            d="$base/{{jobId}}"
            mkdir -p "$base"
            mkdir "$d"
            cat > "$d/script.sh" <<'{{delimiter}}'
            {{script.ReplaceLineEndings("\n").TrimEnd('\n')}}
            {{delimiter}}
            date +%s > "$d/started"
            : > "$d/log"
            run='echo $$ > "$1/pid"; sh "$1/script.sh" > "$1/log" 2>&1 < /dev/null; rc=$?; echo "$rc $(date +%s)" > "$1/exit.tmp"; mv "$1/exit.tmp" "$1/exit"'
            # A new session gives the job its own process group, so stop_job can end the whole tree.
            if command -v setsid > /dev/null 2>&1; then
              setsid nohup sh -c "$run" sh "$d" > /dev/null 2>&1 < /dev/null &
            else
              nohup sh -c "$run" sh "$d" > /dev/null 2>&1 < /dev/null &
            fi
            i=0
            while [ ! -s "$d/pid" ] && [ "$i" -lt 20 ]; do
              sleep 0.1 2> /dev/null || sleep 1
              i=$((i + 1))
            done
            echo "$d/log"
            """;
        var stdout = new OutputCapture(4096);
        await RunAsync(context, "Starting the job", launcher, stdout, cancellationToken).ConfigureAwait(false);
        return new JobStartResult(jobId, stdout.GetText().Trim());
    }

    internal static async Task<JobStatusResult> JobStatusAsync(
        SessionContext context,
        string jobId,
        long sinceOffset,
        int maxOutputBytes,
        OutputMode mode,
        CancellationToken cancellationToken)
    {
        // The remote side slices the log, so a multi-gigabyte log costs the same as a small one.
        var script = $$"""
            {{DescribeFunction}}
            {{FindJob(jobId)}}
            describe "$d"
            o={{sinceOffset}}
            m={{maxOutputBytes}}
            if [ "$o" -gt "$size" ]; then o=$size; fi
            avail=$((size - o))
            l1=$avail; s1=$o; l2=0; s2=0
            if [ "$avail" -gt "$m" ]; then
              case {{mode}} in
                Head) l1=$m ;;
                Tail) l1=$m; s1=$((size - m)) ;;
                *) l1=$((m / 2)); l2=$((m - l1)); s2=$((size - l2)) ;;
              esac
            fi
            echo "$avail $l1 $l2"
            if [ "$l1" -gt 0 ]; then tail -c "+$((s1 + 1))" "$d/log" | head -c "$l1"; fi
            if [ "$l2" -gt 0 ]; then tail -c "+$((s2 + 1))" "$d/log" | head -c "$l2"; fi
            """;
        var stdout = new OutputCapture(maxOutputBytes + 1024);
        await RunAsync(context, "Reading the job status", script, stdout, cancellationToken).ConfigureAwait(false);

        return ParseStatus(stdout.ToArray(), mode, includeLog: maxOutputBytes > 0);
    }

    internal static JobStatusResult ParseStatus(Span<byte> bytes, OutputMode mode, bool includeLog = true)
    {
        var job = ParseJob(TakeLine(ref bytes));
        var slice = TakeLine(ref bytes).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (slice.Length != 3 ||
            !long.TryParse(slice[0], NumberStyles.None, CultureInfo.InvariantCulture, out var available) ||
            !int.TryParse(slice[1], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
            !int.TryParse(slice[2], NumberStyles.None, CultureInfo.InvariantCulture, out var second))
            throw new InvalidOperationException("The job status response was malformed.");

        first = Math.Min(first, bytes.Length);
        second = Math.Min(second, bytes.Length - first);
        var log = includeLog
            ? FormatLog(bytes[..first], bytes.Slice(first, second), available - first - second, mode)
            : string.Empty;
        return new JobStatusResult([job], log, job.LogBytes);
    }

    internal static string FormatLog(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, long omitted, OutputMode mode)
    {
        if (omitted <= 0)
            return Encoding.UTF8.GetString(first) + Encoding.UTF8.GetString(second);

        var marker = $"[... {omitted} bytes omitted ...]";
        var head = mode == OutputMode.Tail ? string.Empty : Encoding.UTF8.GetString(OutputCapture.TrimIncompleteEnd(first));
        var tail = Encoding.UTF8.GetString(OutputCapture.TrimContinuationStart(mode == OutputMode.Tail ? first : second));
        var text = new StringBuilder(head.Length + tail.Length + marker.Length + 2);
        if (head.Length > 0)
        {
            text.Append(head);
            if (head[^1] != '\n')
                text.Append('\n');
        }
        text.Append(marker);
        if (tail.Length > 0)
            text.Append('\n').Append(tail);
        return text.ToString();
    }

    internal static async Task<JobStatusResult> ListJobsAsync(SessionContext context, CancellationToken cancellationToken)
    {
        var script = $$"""
            {{DescribeFunction}}
            base={{JobsDirectory}}
            if [ -d "$base" ]; then
              for id in $(ls -1t "$base" | head -n {{ListedJobs}}); do
                if [ -d "$base/$id" ]; then describe "$base/$id"; fi
              done
            fi
            """;
        var stdout = new OutputCapture(16 * 1024);
        await RunAsync(context, "Listing jobs", script, stdout, cancellationToken).ConfigureAwait(false);
        var jobs = stdout.GetText()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseJob)
            .ToArray();
        return new JobStatusResult(jobs, string.Empty, 0);
    }

    internal static async Task<JobInfo> StopJobAsync(SessionContext context, string jobId, CancellationToken cancellationToken)
    {
        var script = $$"""
            {{DescribeFunction}}
            {{FindJob(jobId)}}
            pid=$(cat "$d/pid" 2>/dev/null || true)
            alive() { kill -s 0 -- "-$pid" 2>/dev/null || kill -s 0 "$pid" 2>/dev/null; }
            signal() {
              # Prefer the whole process group; without setsid fall back to the job and its children.
              if ! kill -s "$1" -- "-$pid" 2>/dev/null; then
                if command -v pkill > /dev/null 2>&1; then pkill "-$1" -P "$pid" 2>/dev/null || true; fi
                kill -s "$1" "$pid" 2>/dev/null || true
              fi
            }
            if [ ! -f "$d/exit" ] && [ -n "$pid" ] && alive; then
              date +%s > "$d/stopped"
              signal TERM
              i=0
              while alive && [ "$i" -lt 5 ]; do sleep 1; i=$((i + 1)); done
              if alive; then signal KILL; sleep 1; fi
              date +%s > "$d/stopped"
            fi
            describe "$d"
            """;
        var stdout = new OutputCapture(4096);
        await RunAsync(context, "Stopping the job", script, stdout, cancellationToken).ConfigureAwait(false);
        return ParseJob(stdout.GetText().Trim());
    }

    private static string FindJob(string jobId) =>
        $$"""
        d={{JobsDirectory}}/{{jobId}}
        if [ ! -d "$d" ]; then echo "No such job on this session: {{jobId}}. Call job_status without jobId to list jobs." >&2; exit 44; fi
        """;

    private static async Task RunAsync(
        SessionContext context,
        string operation,
        string script,
        OutputCapture stdout,
        CancellationToken cancellationToken)
    {
        var stderr = new OutputCapture(4096);
        var exitCode = await context.RunScriptAsync(
            operation, script, stdout, stderr, TimeoutSeconds, cancellationToken).ConfigureAwait(false);
        if (exitCode == 0)
            return;
        var message = stderr.GetText().Trim();
        throw new InvalidOperationException(message.Length > 0
            ? message
            : $"{operation} failed with exit code {exitCode} on session '{context.Profile.Name}'.");
    }

    private static string TakeLine(ref Span<byte> bytes)
    {
        var newline = bytes.IndexOf((byte)'\n');
        var line = Encoding.UTF8.GetString(newline < 0 ? bytes : bytes[..newline]);
        bytes = newline < 0 ? Span<byte>.Empty : bytes[(newline + 1)..];
        return line;
    }

    internal static JobInfo ParseJob(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 ||
            !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var logBytes) ||
            !long.TryParse(fields[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var elapsed))
            throw new InvalidOperationException("The job status response was malformed.");
        return new JobInfo(
            fields[0],
            fields[1],
            int.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exitCode) ? exitCode : null,
            logBytes,
            Math.Max(0, elapsed));
    }

    internal static string CreateJobId(string? name, DateTimeOffset now)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "job" : name.Trim();
        if (!JobNamePattern().IsMatch(label))
            throw new ArgumentException("name must be 1 to 24 letters, digits, '_' or '-'.", nameof(name));
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();
        return $"{label}-{now.UtcDateTime.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture)}-{suffix}";
    }

    internal static void ValidateJobId(string jobId)
    {
        // The ID becomes a directory name inside a shell script; keep it to inert characters.
        if (string.IsNullOrWhiteSpace(jobId) || !JobIdPattern().IsMatch(jobId.Trim()))
            throw new ArgumentException("jobId is not a valid job ID returned by start_job.", nameof(jobId));
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,24}$")]
    private static partial Regex JobNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex JobIdPattern();
}

public sealed record JobStartResult(string JobId, string LogPath);

public sealed record JobInfo(
    string JobId,
    string State,
    int? ExitCode,
    long LogBytes,
    long ElapsedSeconds);

public sealed record JobStatusResult(
    IReadOnlyList<JobInfo> Jobs,
    string Log,
    long NextOffset);
