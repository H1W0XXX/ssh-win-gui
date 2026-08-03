using System.Text;
using RsyncShell.App.Controls;
using RsyncShell.App.Services;

namespace RsyncShell.App.Tests;

public sealed class SshTerminalConnectionTests
{
    [Fact]
    public async Task WriteInputChunkAsync_WritesUtf8AndFlushesTheShell()
    {
        await using var shell = new RecordingStream();

        await SshTerminalConnection.WriteInputChunkAsync(
            shell,
            "printf '中文'\r",
            CancellationToken.None);

        Assert.Equal("printf '中文'\r", Encoding.UTF8.GetString(shell.WrittenBytes));
        Assert.Equal(1, shell.FlushCount);
    }

    [Fact]
    public void PasswordPrompt_DoesNotEchoAndSubmitsEditedPassword()
    {
        var prompt = new PasswordPromptTerminalConnection("Password: ");
        var output = new StringBuilder();
        string? submitted = null;
        prompt.TerminalOutput += (_, args) => output.Append(args.Data);
        prompt.PasswordSubmitted += (_, password) => submitted = password;

        prompt.AttachRenderer();
        prompt.WriteInput("secx\bret\r");

        Assert.Equal("secret", submitted);
        Assert.Equal("Password: \r\n", output.ToString());
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordPrompt_RequiresNonEmptyPassword()
    {
        var prompt = new PasswordPromptTerminalConnection("Password: ");
        var submissions = 0;
        prompt.PasswordSubmitted += (_, _) => submissions++;

        prompt.AttachRenderer();
        prompt.WriteInput("\r");
        prompt.WriteInput("ok\r");

        Assert.Equal(1, submissions);
    }

    [Theory]
    [InlineData(0x0207, TerminalMousePasteButton.Middle, true, false)]
    [InlineData(0x0208, TerminalMousePasteButton.Middle, true, true)]
    [InlineData(0x0204, TerminalMousePasteButton.Right, true, false)]
    [InlineData(0x0205, TerminalMousePasteButton.Right, true, true)]
    [InlineData(0x0205, TerminalMousePasteButton.Middle, true, false)]
    [InlineData(0x0208, TerminalMousePasteButton.Right, true, false)]
    public void ConfiguredMouseButtonConsumesDownAndPastesOnUp(
        int message,
        TerminalMousePasteButton button,
        bool expectedHandled,
        bool expectedPaste)
    {
        var handled = SshTerminalHost.TryGetPasteMouseAction(message, button, out var paste);

        Assert.Equal(expectedHandled, handled);
        Assert.Equal(expectedPaste, paste);
    }

    [Fact]
    public void AlternateScreenTrackerHandlesSplitEnterAndExitSequences()
    {
        var tracker = new AlternateScreenTracker();

        tracker.Append("before\x1b[?10");
        Assert.False(tracker.IsActive);
        tracker.Append("49hinside");
        Assert.True(tracker.IsActive);
        tracker.Append("\x1b[?1049lafter");
        Assert.False(tracker.IsActive);
    }

    [Theory]
    [InlineData("\x1b[?47h", true)]
    [InlineData("\x1b[?1047h", true)]
    [InlineData("\x1b[?1;1049h", true)]
    [InlineData("\x1b[?1049h\x1b[?1049l", false)]
    public void AlternateScreenTrackerRecognizesSupportedModes(string output, bool expected)
    {
        var tracker = new AlternateScreenTracker();

        tracker.Append(output);

        Assert.Equal(expected, tracker.IsActive);
    }

    [Theory]
    [InlineData(120, 0x26, 3)]
    [InlineData(-120, 0x28, 3)]
    [InlineData(240, 0x26, 6)]
    public void AlternateScreenWheelRequestsNativeCursorKeys(
        int delta,
        int expectedVirtualKey,
        int expectedRepeatCount)
    {
        var remainder = 0;

        var handled = SshTerminalHost.TryGetAlternateScreenWheelNavigation(
            0x020A,
            WheelWParam(delta),
            alternateScreen: true,
            hasModifier: false,
            ref remainder,
            out var virtualKey,
            out var repeatCount);

        Assert.True(handled);
        Assert.Equal(expectedVirtualKey, virtualKey);
        Assert.Equal(expectedRepeatCount, repeatCount);
        Assert.Equal(0, remainder);
    }

    [Fact]
    public void AlternateScreenWheelAccumulatesPrecisionWheelDeltas()
    {
        var remainder = 0;

        Assert.True(SshTerminalHost.TryGetAlternateScreenWheelNavigation(
            0x020A, WheelWParam(60), true, false, ref remainder, out var firstKey, out var firstCount));
        Assert.Equal(0, firstKey);
        Assert.Equal(0, firstCount);
        Assert.Equal(60, remainder);
        Assert.True(SshTerminalHost.TryGetAlternateScreenWheelNavigation(
            0x020A, WheelWParam(60), true, false, ref remainder, out var secondKey, out var secondCount));
        Assert.Equal(0x26, secondKey);
        Assert.Equal(3, secondCount);
        Assert.Equal(0, remainder);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WheelRemainsNativeOutsideEligibleAlternateScreen(bool alternateScreen, bool hasModifier)
    {
        var remainder = 60;

        var handled = SshTerminalHost.TryGetAlternateScreenWheelNavigation(
            0x020A,
            WheelWParam(120),
            alternateScreen,
            hasModifier,
            ref remainder,
            out var virtualKey,
            out var repeatCount);

        Assert.False(handled);
        Assert.Equal(0, virtualKey);
        Assert.Equal(0, repeatCount);
        Assert.Equal(0, remainder);
    }

    private static IntPtr WheelWParam(int delta) =>
        new(unchecked((long)(ushort)(short)delta << 16));

    private sealed class RecordingStream : MemoryStream
    {
        public int FlushCount { get; private set; }
        public byte[] WrittenBytes => ToArray();

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return Task.CompletedTask;
        }
    }
}
