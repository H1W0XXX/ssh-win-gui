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
    [InlineData(Key.Tab, "\t")]
    [InlineData(Key.Up, "\x1b[A")]
    [InlineData(Key.Down, "\x1b[B")]
    [InlineData(Key.Left, "\x1b[D")]
    [InlineData(Key.Right, "\x1b[C")]
    [InlineData(Key.Home, "\x1b[H")]
    [InlineData(Key.End, "\x1b[F")]
    [InlineData(Key.PageUp, "\x1b[5~")]
    [InlineData(Key.PageDown, "\x1b[6~")]
    [InlineData(Key.Delete, "\x1b[3~")]
    public void NavigationKeysUseCanonicalXtermSequences(Key key, string expected)
    {
        Assert.Equal(expected, SshTerminalHost.TranslateNavigationKey(key));
    }

    [Theory]
    [InlineData(0x0100, 0x09, true, true, "\t")]
    [InlineData(0x0101, 0x09, true, false, "\t")]
    [InlineData(0x0100, 0x26, true, true, "\x1b[A")]
    [InlineData(0x0101, 0x26, true, false, "\x1b[A")]
    [InlineData(0x0100, 0x28, true, true, "\x1b[B")]
    [InlineData(0x0100, 0x21, true, true, "\x1b[5~")]
    [InlineData(0x0101, 0x21, true, false, "\x1b[5~")]
    [InlineData(0x0100, 0x22, true, true, "\x1b[6~")]
    [InlineData(0x0101, 0x22, true, false, "\x1b[6~")]
    [InlineData(0x0102, 0x26, false, false, null)]
    [InlineData(0x0100, 0x41, false, false, null)]
    public void ThreadKeyboardMessagesConsumeNavigationBeforeNativeDispatch(
        int message,
        int virtualKey,
        bool expectedHandled,
        bool expectedWrite,
        string? expectedSequence)
    {
        var handled = SshTerminalHost.TryTranslateNavigationMessage(
            message,
            virtualKey,
            out var sequence,
            out var write);

        Assert.Equal(expectedHandled, handled);
        Assert.Equal(expectedWrite, write);
        Assert.Equal(expectedSequence, sequence);
    }

    [Fact]
    public void VirtualKeyExtractionIgnoresHighPointerBitsWithoutOverflow()
    {
        var wParam = new IntPtr(unchecked((long)0x7FFF_FFFF_0000_0026));

        Assert.Equal(0x26, SshTerminalHost.ExtractVirtualKey(wParam));
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
    [InlineData(Key.Insert, ModifierKeys.Shift, true)]
    [InlineData(Key.Insert, ModifierKeys.None, false)]
    [InlineData(Key.Insert, ModifierKeys.Shift | ModifierKeys.Control, false)]
    [InlineData(Key.Delete, ModifierKeys.Shift, false)]
    public void ShiftInsertWpfFallbackRequiresExactShortcut(
        Key key,
        ModifierKeys modifiers,
        bool expected)
    {
        Assert.Equal(expected, SshTerminalHost.IsShiftInsert(key, modifiers));
    }

    [Fact]
    public void ShiftInsertWpfFallbackUsesModifierStateTrackedByNativeHook()
    {
        Assert.True(SshTerminalHost.IsShiftInsert(
            Key.Insert,
            ModifierKeys.None,
            trackedShift: true));
        Assert.False(SshTerminalHost.IsShiftInsert(
            Key.Insert,
            ModifierKeys.None,
            trackedShift: true,
            trackedControl: true));
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
