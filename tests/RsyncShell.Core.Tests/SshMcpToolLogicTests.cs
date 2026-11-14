using System.Text;
using RsyncShell.Core.Models;
using RsyncShell.Mcp;

namespace RsyncShell.Core.Tests;

public sealed class SshMcpOutputCaptureTests
{
    private static OutputCapture Capture(int limit, OutputMode mode, params string[] chunks)
    {
        var capture = new OutputCapture(limit, mode);
        foreach (var chunk in chunks)
            capture.Append(Encoding.UTF8.GetBytes(chunk));
        return capture;
    }

    [Theory]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("head_tail")]
    public void OutputWithinTheLimitIsReturnedUnchanged(string mode)
    {
        var capture = Capture(10, ScriptOptions.ParseMode(mode), "abc", "de\n", "fghi");

        Assert.False(capture.Truncated);
        Assert.Equal("abcde\nfghi", capture.GetText());
        Assert.Equal(10, capture.ToArray().Length);
    }

    [Fact]
    public void EachModeKeepsItsPartAndMarksTheGap()
    {
        string[] chunks = ["0123", "456789", "abcdef", "ghij"];

        Assert.Equal("01234567\n[... 12 bytes omitted ...]", Capture(8, OutputMode.Head, chunks).GetText());
        Assert.Equal("[... 12 bytes omitted ...]\ncdefghij", Capture(8, OutputMode.Tail, chunks).GetText());
        Assert.Equal("0123\n[... 12 bytes omitted ...]\nghij", Capture(8, OutputMode.HeadTail, chunks).GetText());
        Assert.True(Capture(8, OutputMode.HeadTail, chunks).Truncated);
    }

    [Fact]
    public void TailSurvivesManySmallAppendsAndOneLargeAppend()
    {
        var small = new OutputCapture(5, OutputMode.Tail);
        for (var value = 0; value < 1000; value++)
            small.Append(Encoding.ASCII.GetBytes((value % 10).ToString()));
        Assert.EndsWith("\n56789", small.GetText());

        var large = Capture(5, OutputMode.Tail, "xy", new string('-', 100) + "END!!");
        Assert.EndsWith("\nEND!!", large.GetText());
    }

    [Fact]
    public void TruncationNeverSplitsAMultiByteCharacter()
    {
        // Each character is three bytes, so a 7-byte half cuts inside one.
        var capture = Capture(14, OutputMode.HeadTail, string.Concat(Enumerable.Repeat("中", 20)));

        var text = capture.GetText();
        Assert.DoesNotContain('�', text);
        Assert.StartsWith("中中\n[... 46 bytes omitted ...]\n中中", text);
    }

    [Fact]
    public void LineFilterMatchesWholeLinesAcrossChunkBoundaries()
    {
        var capture = new OutputCapture(1024);
        var filter = new LineFilterSink(LineFilterSink.CreatePattern("(?i)error|^last"), capture);
        foreach (var chunk in new[] { "ok\nfatal ER", "ROR: disk\r\nwarn", "ing\nlast line without newline" })
            filter.Append(Encoding.UTF8.GetBytes(chunk));
        filter.Complete();

        Assert.Equal("fatal ERROR: disk\r\nlast line without newline", capture.GetText());
    }

    [Fact]
    public void ScriptOptionsValidateModeGrepAndLimits()
    {
        Assert.Equal(OutputMode.HeadTail, ScriptOptions.ParseMode(null));
        Assert.Equal(OutputMode.Tail, ScriptOptions.ParseMode(" TAIL "));
        Assert.Throws<ArgumentException>(() => ScriptOptions.ParseMode("middle"));
        Assert.Throws<ArgumentException>(() => ScriptOptions.ParseGrep("("));
        Assert.Null(ScriptOptions.ParseGrep(""));
        // Lookbehind is unsupported by the linear engine and must fall back, not fail.
        Assert.Matches(ScriptOptions.ParseGrep("(?<=a)b")!, "ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptOptions.Create("true", 0, 4096, 65536, "head", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptOptions.Create("true", 60, 32768, 16384, "head", null));
    }
}

public sealed class SshMcpFileToolTests
{
    private static string Edit(string content, string oldString, string newString, bool replaceAll = false) =>
        Encoding.UTF8.GetString(FileTools.ApplyEdit(Encoding.UTF8.GetBytes(content), oldString, newString, replaceAll).Content);

    [Fact]
    public void EditReplacesAUniqueMatchAndReportsItsLine()
    {
        var result = FileTools.ApplyEdit("a\nb\nc\n"u8, "b\n", "B\nB2\n", replaceAll: false);

        Assert.Equal("a\nB\nB2\nc\n", Encoding.UTF8.GetString(result.Content));
        Assert.Equal([2], result.Lines);
        Assert.Equal("ac", Edit("abc", "b", ""));
    }

    [Fact]
    public void EditRefusesAmbiguousAndMissingMatches()
    {
        var ambiguous = Assert.Throws<InvalidOperationException>(() => Edit("x=1\ny=2\nx=1\n", "x=1", "x=2"));
        Assert.Contains("2 places (lines 1, 3)", ambiguous.Message);

        Assert.Equal("x=2\ny=2\nx=2\n", Edit("x=1\ny=2\nx=1\n", "x=1", "x=2", replaceAll: true));
        Assert.Contains("not found", Assert.Throws<InvalidOperationException>(() => Edit("abc", "abd", "x")).Message);
        Assert.Throws<ArgumentException>(() => Edit("abc", "", "x"));
        Assert.Throws<ArgumentException>(() => Edit("abc", "b", "b"));
    }

    [Fact]
    public void EditKeepsCrlfLineEndingsWhenTheCallerWritesLf()
    {
        Assert.Equal("a\r\nB\r\nnew\r\nc\r\n", Edit("a\r\nb\r\nc\r\n", "a\nb\n", "a\nB\nnew\n"));
    }

    [Fact]
    public void EditLeavesBytesOutsideTheMatchUntouched()
    {
        // 0xFF is not valid UTF-8; a decode/encode round trip would corrupt it.
        byte[] content = [0xFF, (byte)'k', (byte)'=', (byte)'1', 0xFE, 0x00];

        var result = FileTools.ApplyEdit(content, "k=1", "k=22", replaceAll: false);

        Assert.Equal([0xFF, (byte)'k', (byte)'=', (byte)'2', (byte)'2', 0xFE, 0x00], result.Content);
    }

    [Fact]
    public void ReadNumbersLinesAndSaysWhereToContinue()
    {
        var text = FileTools.FormatRead("/etc/app.conf", "b\r\nc\n"u8, offset: 2, size: 9, totalLines: 5, maxBytes: 1024, lineNumbers: true);
        Assert.Equal("[/etc/app.conf: lines 2-3 of 5; continue with offset=4]\n2\tb\n3\tc", text);

        var plain = FileTools.FormatRead("/f", "only"u8, offset: 1, size: 4, totalLines: 1, maxBytes: 1024, lineNumbers: false);
        Assert.Equal("[/f: lines 1-1 of 1]\nonly", plain);
    }

    [Fact]
    public void ReadCutsAtALineBoundaryWhenTheByteLimitIsHit()
    {
        var body = Encoding.UTF8.GetBytes("aaaa\nbbbb\ncccc\n");

        var text = FileTools.FormatRead("/f", body, offset: 1, size: 15, totalLines: 3, maxBytes: 12, lineNumbers: false);

        Assert.Equal("[/f: lines 1-2 of 3; continue with offset=3]\naaaa\nbbbb", text);
    }

    [Fact]
    public void ReadExplainsEmptyPastEndAndBinaryFiles()
    {
        Assert.Equal("[/f: empty file]", FileTools.FormatRead("/f", [], 1, 0, 0, 1024, true));
        Assert.Contains("past the end", FileTools.FormatRead("/f", [], 9, 4, 2, 1024, true));
        Assert.Throws<InvalidOperationException>(() => FileTools.FormatRead("/f", [1, 0, 2], 1, 3, 1, 1024, true));
    }

    [Theory]
    [InlineData("/srv/app/main.py", "'/srv/app/main.py'")]
    [InlineData("/tmp/it's here", @"'/tmp/it'\''s here'")]
    [InlineData("~/src/$HOME `x`", "\"$HOME\"/'src/$HOME `x`'")]
    public void RemotePathsBecomeInertShellWords(string path, string expected)
    {
        Assert.Equal(expected, PosixShell.PathWord(path, "path"));
    }

    [Theory]
    [InlineData("relative/file")]
    [InlineData("~user/file")]
    [InlineData("/tmp/dir/")]
    [InlineData("/tmp/a\nb")]
    [InlineData(" ")]
    public void RemotePathsMustBeExactAbsoluteOrHomeRelative(string path)
    {
        Assert.Throws<ArgumentException>(() => PosixShell.PathWord(path, "path"));
    }
}

public sealed class SshMcpJobToolTests
{
    [Fact]
    public void JobIdsAreSortableLabelsMadeOfInertCharacters()
    {
        var id = JobTools.CreateJobId("build_x86", new DateTimeOffset(2026, 3, 9, 4, 5, 6, TimeSpan.Zero));

        Assert.Matches("^build_x86-0309-040506-[0-9a-f]{4}$", id);
        JobTools.ValidateJobId(id);
        Assert.StartsWith("job-", JobTools.CreateJobId(null, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => JobTools.CreateJobId("has space", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("a b")]
    [InlineData("$(reboot)")]
    [InlineData("-rf")]
    [InlineData("a;b")]
    [InlineData("")]
    public void JobIdsThatCouldEscapeTheJobDirectoryAreRejected(string jobId)
    {
        Assert.Throws<ArgumentException>(() => JobTools.ValidateJobId(jobId));
    }

    [Fact]
    public void StatusLinesParseRunningAndFinishedJobs()
    {
        Assert.Equal(new JobInfo("build-1", "running", null, 120, 35), JobTools.ParseJob("build-1 running - 120 35"));
        Assert.Equal(new JobInfo("build-1", "exited", 2, 4096, 61), JobTools.ParseJob("build-1 exited 2 4096 61"));
        Assert.Throws<InvalidOperationException>(() => JobTools.ParseJob("garbage"));
    }

    [Fact]
    public void StatusReturnsTheLogSliceAndTheOffsetToResumeFrom()
    {
        var whole = JobTools.ParseStatus(Encoding.UTF8.GetBytes("j exited 0 11 3\n11 11 0\nhello\nworld"), OutputMode.HeadTail);
        Assert.Equal("hello\nworld", whole.Log);
        Assert.Equal(11, whole.NextOffset);
        Assert.Equal(0, Assert.Single(whole.Jobs).ExitCode);

        var cut = JobTools.ParseStatus(Encoding.UTF8.GetBytes("j running - 500 9\n500 4 4\nheadtail"), OutputMode.HeadTail);
        Assert.Equal("head\n[... 492 bytes omitted ...]\ntail", cut.Log);

        var tail = JobTools.ParseStatus(Encoding.UTF8.GetBytes("j running - 500 9\n500 4 0\ntail"), OutputMode.Tail);
        Assert.Equal("[... 496 bytes omitted ...]\ntail", tail.Log);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("head_tail")]
    public void StatusOnlyOmitsTheLogAndTruncationMarkerButKeepsTheResumeOffset(string mode)
    {
        var status = JobTools.ParseStatus(
            Encoding.UTF8.GetBytes("j stopped - 3013 14\n3013 0 0\n"), ScriptOptions.ParseMode(mode), includeLog: false);

        Assert.Empty(status.Log);
        Assert.Equal(3013, status.NextOffset);
        Assert.Equal(new JobInfo("j", "stopped", null, 3013, 14), Assert.Single(status.Jobs));
    }
}

public sealed class SshMcpConnectionPoolTests
{
    private static readonly IOutputSink Discard = new OutputCapture(16);

    private static SshTarget Target(string id = "a", string host = "host-a")
    {
        var profile = new ConnectionProfile { Id = id, Name = id, Host = host, Username = "tester", PrivateKeyPath = "key" };
        return new SshTarget(profile, [profile], "key");
    }

    private sealed class FakeConnection : ISshConnection
    {
        private int _running;
        public int Calls;
        public int MaximumConcurrency;
        public bool Disposed;
        public Func<FakeConnection, Task>? OnExecute;

        public bool IsAlive { get; set; } = true;

        public async Task<int> ExecuteAsync(
            string command, ReadOnlyMemory<byte> stdin, IOutputSink stdout, IOutputSink stderr, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref MaximumConcurrency, running);
            try
            {
                if (OnExecute is not null)
                    await OnExecute(this);
                return 0;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void Dispose() => Disposed = true;

        private static void InterlockedMax(ref int location, int value)
        {
            int current;
            while ((current = Volatile.Read(ref location)) < value &&
                   Interlocked.CompareExchange(ref location, value, current) != current)
            {
            }
        }
    }

    private sealed class Harness : IDisposable
    {
        public readonly List<FakeConnection> Opened = [];
        public Func<FakeConnection>? Create;
        public readonly SshConnectionPool Pool;

        public Harness(TimeSpan? idleTimeout = null, int maximumConnections = 8)
        {
            Pool = new SshConnectionPool(
                idleTimeout: idleTimeout ?? TimeSpan.FromMinutes(5),
                maximumConnections: maximumConnections,
                channelRetryWindow: TimeSpan.FromMilliseconds(300),
                connect: (_, _) =>
                {
                    var connection = Create?.Invoke() ?? new FakeConnection();
                    lock (Opened)
                        Opened.Add(connection);
                    return Task.FromResult<ISshConnection>(connection);
                });
        }

        public Task<int> Run(SshTarget target) =>
            Pool.ExecuteAsync(target, "sh -s", default, Discard, Discard, CancellationToken.None);

        public void Dispose() => Pool.Dispose();
    }

    [Fact]
    public async Task EveryCallToOneSessionSharesASingleConnection()
    {
        using var harness = new Harness();
        var target = Target();

        for (var call = 0; call < 20; call++)
            await harness.Run(target);

        var connection = Assert.Single(harness.Opened);
        Assert.Equal(20, connection.Calls);
        Assert.Equal(1, harness.Pool.ConnectionCount);
    }

    [Fact]
    public async Task ParallelCallsShareTheConnectionWithinTheChannelLimit()
    {
        using var harness = new Harness
        {
            Create = () => new FakeConnection { OnExecute = _ => Task.Delay(40) },
        };
        var target = Target();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => harness.Run(target)));

        var connection = Assert.Single(harness.Opened);
        Assert.Equal(40, connection.Calls);
        Assert.InRange(connection.MaximumConcurrency, 2, SshConnectionPool.MaximumChannelsPerConnection);
    }

    [Fact]
    public async Task DifferentSessionsAndEditedSessionsGetTheirOwnConnection()
    {
        using var harness = new Harness();

        await harness.Run(Target("a"));
        await harness.Run(Target("b"));
        await harness.Run(Target("a", host: "moved-host"));
        await harness.Run(Target("a"));

        Assert.Equal(3, harness.Opened.Count);
    }

    [Fact]
    public async Task AConnectionThatDiedWhileIdleIsReplacedBeforeUse()
    {
        using var harness = new Harness();
        var target = Target();
        await harness.Run(target);
        harness.Opened[0].IsAlive = false;

        await harness.Run(target);

        Assert.Equal(2, harness.Opened.Count);
        Assert.True(harness.Opened[0].Disposed);
        Assert.Equal(1, harness.Opened[1].Calls);
        Assert.Equal(1, harness.Pool.ConnectionCount);
    }

    [Fact]
    public async Task AConnectionThatDropsAtChannelOpenIsReplacedOnce()
    {
        using var harness = new Harness();
        var target = Target();
        await harness.Run(target);
        harness.Opened[0].OnExecute = connection =>
        {
            connection.IsAlive = false;
            throw new ChannelOpenException(new IOException("connection reset"));
        };

        await harness.Run(target);

        Assert.Equal(2, harness.Opened.Count);
        Assert.True(harness.Opened[0].Disposed);
    }

    [Fact]
    public async Task AServerAtItsSessionLimitIsWaitedOutOnTheSameConnection()
    {
        // sshd frees a session slot a moment after the client sees the command end,
        // so a busy connection can briefly refuse a new channel while staying healthy.
        using var harness = new Harness();
        var target = Target();
        await harness.Run(target);
        var refusals = 3;
        harness.Opened[0].OnExecute = _ => refusals-- > 0
            ? throw new ChannelOpenException(new IOException("open failed: no more sessions"))
            : Task.CompletedTask;

        await harness.Run(target);

        var connection = Assert.Single(harness.Opened);
        Assert.Equal(5, connection.Calls);
        Assert.False(connection.Disposed);
    }

    [Fact]
    public async Task ALowServerSessionLimitIsLearnedInsteadOfOpeningMoreConnections()
    {
        // Behaves like sshd with MaxSessions 2: a third concurrent channel is refused.
        var sessions = 0;
        var mostSessions = 0;
        using var harness = new Harness
        {
            Create = () => new FakeConnection
            {
                OnExecute = async _ =>
                {
                    if (Interlocked.Increment(ref sessions) > 2)
                    {
                        Interlocked.Decrement(ref sessions);
                        throw new ChannelOpenException(new IOException("open failed: no more sessions"));
                    }
                    mostSessions = Math.Max(mostSessions, Volatile.Read(ref sessions));
                    await Task.Delay(30);
                    Interlocked.Decrement(ref sessions);
                },
            },
        };
        var target = Target();

        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => harness.Run(target)));
        var refusedWhileLearning = Assert.Single(harness.Opened).Calls - 30;
        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => harness.Run(target)));

        var connection = Assert.Single(harness.Opened);
        Assert.Equal(2, mostSessions);
        Assert.InRange(refusedWhileLearning, 1, 12);
        // Once the limit is known, further bursts are queued without being refused at all.
        Assert.Equal(60 + refusedWhileLearning, connection.Calls);
    }

    [Fact]
    public async Task AServerThatKeepsRefusingChannelsIsReportedNotLooped()
    {
        using var harness = new Harness
        {
            Create = () => new FakeConnection
            {
                OnExecute = _ => throw new ChannelOpenException(new IOException("administratively prohibited")),
            },
        };

        var error = await Assert.ThrowsAsync<IOException>(() => harness.Run(Target()));

        Assert.Equal("administratively prohibited", error.Message);
        Assert.Single(harness.Opened);
        Assert.Equal(0, harness.Pool.ConnectionCount);
    }

    [Fact]
    public async Task AScriptFailureKeepsTheConnectionAndIsNeverRetried()
    {
        using var harness = new Harness();
        var target = Target();
        await harness.Run(target);
        harness.Opened[0].OnExecute = _ => throw new InvalidOperationException("mid-script failure");

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Run(target));

        var connection = Assert.Single(harness.Opened);
        Assert.Equal(2, connection.Calls);
        Assert.False(connection.Disposed);
    }

    [Fact]
    public async Task AFailedConnectIsNotCached()
    {
        var attempts = 0;
        using var pool = new SshConnectionPool(connect: (_, _) =>
            ++attempts == 1
                ? Task.FromException<ISshConnection>(new IOException("refused"))
                : Task.FromResult<ISshConnection>(new FakeConnection()));
        var target = Target();

        await Assert.ThrowsAsync<IOException>(() =>
            pool.ExecuteAsync(target, "sh -s", default, Discard, Discard, CancellationToken.None));
        await pool.ExecuteAsync(target, "sh -s", default, Discard, Discard, CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task IdleConnectionsAreClosed()
    {
        using var harness = new Harness(idleTimeout: TimeSpan.FromMilliseconds(200));
        await harness.Run(Target());

        for (var wait = 0; wait < 100 && !harness.Opened[0].Disposed; wait++)
            await Task.Delay(50);

        Assert.True(harness.Opened[0].Disposed);
        Assert.Equal(0, harness.Pool.ConnectionCount);
    }

    [Fact]
    public async Task TheLeastRecentlyUsedIdleConnectionMakesRoomAtTheCap()
    {
        using var harness = new Harness(maximumConnections: 2);
        await harness.Run(Target("a"));
        await Task.Delay(30);
        await harness.Run(Target("b"));
        await Task.Delay(30);
        await harness.Run(Target("a"));

        await harness.Run(Target("c"));

        Assert.Equal(2, harness.Pool.ConnectionCount);
        Assert.False(harness.Opened[0].Disposed);
        Assert.True(harness.Opened[1].Disposed);
    }

    [Fact]
    public async Task TheCapIsAHardLimitWhileEveryConnectionIsBusy()
    {
        var release = new TaskCompletionSource();
        using var harness = new Harness(maximumConnections: 2)
        {
            Create = () => new FakeConnection { OnExecute = _ => release.Task },
        };
        var busy = new[] { harness.Run(Target("a")), harness.Run(Target("b")) };
        while (harness.Opened.Count < 2 || harness.Opened.Any(connection => connection.Calls == 0))
            await Task.Delay(10);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Run(Target("c")));

        Assert.Contains("busy", error.Message);
        release.SetResult();
        await Task.WhenAll(busy);
        Assert.Equal(2, harness.Opened.Count);
    }

    [Fact]
    public async Task DisposingThePoolClosesItsConnections()
    {
        var harness = new Harness();
        await harness.Run(Target("a"));
        await harness.Run(Target("b"));

        harness.Dispose();

        Assert.All(harness.Opened, connection => Assert.True(connection.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Run(Target("a")));
    }
}
