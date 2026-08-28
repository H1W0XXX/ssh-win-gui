using System.Text;
using RsyncShell.App.Controls;
using RsyncShell.App.Services;
using System.Windows.Input;

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
    [InlineData(Key.Tab, ModifierKeys.None, true)]
    [InlineData(Key.Up, ModifierKeys.None, true)]
    [InlineData(Key.Down, ModifierKeys.None, true)]
    [InlineData(Key.Left, ModifierKeys.Control, true)]
    [InlineData(Key.Right, ModifierKeys.Alt, true)]
    [InlineData(Key.Home, ModifierKeys.None, true)]
    [InlineData(Key.End, ModifierKeys.None, true)]
    [InlineData(Key.PageUp, ModifierKeys.None, true)]
    [InlineData(Key.PageDown, ModifierKeys.None, true)]
    [InlineData(Key.Delete, ModifierKeys.None, true)]
    [InlineData(Key.Insert, ModifierKeys.None, true)]
    [InlineData(Key.Insert, ModifierKeys.Shift, false)]
    [InlineData(Key.A, ModifierKeys.None, false)]
    [InlineData(Key.Enter, ModifierKeys.None, false)]
    public void WpfFocusNavigationKeysAreForwardedAsNativeKeys(
        Key key,
        ModifierKeys modifiers,
        bool expected)
    {
        Assert.Equal(expected, SshTerminalHost.ShouldForwardNativeNavigationKey(key, modifiers));
    }

    [Theory]
    [InlineData(Key.LeftAlt, true)]
    [InlineData(Key.RightAlt, true)]
    [InlineData(Key.F10, false)]
    [InlineData(Key.A, false)]
    public void OnlyAltKeysSuppressWpfMenuActivation(Key key, bool expected)
    {
        Assert.Equal(expected, SshTerminalHost.ShouldSuppressWpfMenuActivation(key));
    }

    [Theory]
    [InlineData(0x0100, 0x2D, true, false, false, true, true)]
    [InlineData(0x0101, 0x2D, true, false, false, true, false)]
    [InlineData(0x0100, 0x2D, false, false, false, false, false)]
    [InlineData(0x0100, 0x2D, true, true, false, false, false)]
    [InlineData(0x0100, 0x26, true, false, false, false, false)]
    public void ShiftInsertConsumesNativeKeyAndPastesOnlyOnKeyDown(
        int message,
        int virtualKey,
        bool shift,
        bool control,
        bool alt,
        bool expectedHandled,
        bool expectedPaste)
    {
        var handled = SshTerminalHost.TryGetKeyboardPasteAction(
            message,
            virtualKey,
            shift,
            control,
            alt,
            out var paste);

        Assert.Equal(expectedHandled, handled);
        Assert.Equal(expectedPaste, paste);
    }

    [Theory]
    [InlineData(0x0106, 0x08, 1L << 29, true)]
    [InlineData(0x0106, 0x08, 0, false)]
    [InlineData(0x0106, 0x41, 1L << 29, false)]
    [InlineData(0x0102, 0x08, 1L << 29, false)]
    public void OnlyAltBackspaceSystemCharacterIsSuppressed(
        int message,
        int character,
        long keyData,
        bool expected)
    {
        Assert.Equal(
            expected,
            SshTerminalHost.IsAltBackspaceSystemCharacter(
                message,
                new IntPtr(character),
                new IntPtr(keyData)));
    }

    [Theory]
    [InlineData(0x0112, 0xF100, 0, true)]
    [InlineData(0x0112, 0xF10F, 0, true)]
    [InlineData(0x0112, 0xF100, 0x46, false)]
    [InlineData(0x0112, 0xF060, 0, false)]
    [InlineData(0x0105, 0xF100, 0, false)]
    public void OnlyStandaloneSystemMenuActivationIsSuppressed(
        int message,
        int command,
        int keyCharacter,
        bool expected)
    {
        Assert.Equal(
            expected,
            SshTerminalHost.IsStandaloneMenuActivation(
                message,
                new IntPtr(command),
                new IntPtr(keyCharacter)));
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

    [Fact]
    public void TerminalModeTrackerHandlesSplitBracketedPasteSequences()
    {
        var tracker = new AlternateScreenTracker();

        tracker.Append("\x1b[?20");
        Assert.False(tracker.IsBracketedPasteEnabled);
        tracker.Append("04h");
        Assert.True(tracker.IsBracketedPasteEnabled);
        tracker.Append("\x1b[?2004l");
        Assert.False(tracker.IsBracketedPasteEnabled);
    }

    [Theory]
    [InlineData("one\r\ntwo\nthree\rfour", "one\rtwo\rthree\rfour")]
    [InlineData("tab\tallowed\0removed\x1bremoved", "tab\tallowedremovedremoved")]
    [InlineData("中文\u0085保留内容", "中文保留内容")]
    public void ClipboardPasteMatchesWindowsTerminalFiltering(string input, string expected)
    {
        Assert.Equal(expected, SshTerminalHost.PrepareTextForPaste(input, bracketedPaste: false));
    }

    [Fact]
    public void ClipboardPasteUsesBracketedPasteWhenRemoteEnablesIt()
    {
        var actual = SshTerminalHost.PrepareTextForPaste("first\r\nsecond", bracketedPaste: true);

        Assert.Equal("\x1b[200~first\rsecond\x1b[201~", actual);
    }

    [Theory]
    [InlineData("single line", 1)]
    [InlineData("one\r\ntwo", 2)]
    [InlineData("one\ntwo\rthree", 3)]
    public void ClipboardPasteMetadataCountsLogicalLines(string input, int expected)
    {
        Assert.Equal(expected, SshTerminalHost.CountPasteLines(input));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t\r\n", false)]
    [InlineData(" command ", true)]
    [InlineData("中文", true)]
    public void ClipboardAutoCopyIgnoresBlankSelections(string? selectedText, bool expected)
    {
        Assert.Equal(expected, SshTerminalHost.ShouldCopySelection(selectedText));
    }

    [Theory]
    [InlineData(120, 0x26, 1)]
    [InlineData(-120, 0x28, 1)]
    [InlineData(240, 0x26, 2)]
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
        Assert.Equal(1, secondCount);
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
