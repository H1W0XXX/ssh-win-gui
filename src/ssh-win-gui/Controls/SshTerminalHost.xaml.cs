using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Microsoft.Terminal.Wpf;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using Color = System.Windows.Media.Color;

namespace RsyncShell.App.Controls;

public partial class SshTerminalHost : System.Windows.Controls.UserControl, ITerminalSurface
{
    private readonly ConnectionProfile _profile;
    private SshAuthenticationOptions? _authentication;
    private readonly Func<SshHostKeyInfo, bool> _verifyHostKey;
    private readonly IReadOnlyList<ConnectionProfile> _route;
    private SshTerminalConnection? _connection;
    private PasswordPromptTerminalConnection? _passwordPrompt;
    private TerminalContainer? _terminalContainer;
    private bool _loaded;
    private bool _disposed;
    private bool _terminalMouseHooked;
    private readonly AlternateScreenTracker _alternateScreen = new();
    private int _wheelDeltaRemainder;
    private static readonly bool InputDiagnostics =
        string.Equals(Environment.GetEnvironmentVariable("SSH_WIN_GUI_INPUT_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    public SshTerminalHost(
        ConnectionProfile profile,
        SshAuthenticationOptions? authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile>? route = null)
    {
        _profile = profile;
        _authentication = authentication;
        _verifyHostKey = verifyHostKey;
        _route = route ?? [profile];
        InitializeComponent();
        MessageBody.Text = profile.DisplayEndpoint;
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public event EventHandler<TerminalHostStateChangedEventArgs>? StateChanged;

    public ConnectionProfile Profile => _profile;

    public SshAuthenticationOptions Authentication =>
        _authentication ?? throw new InvalidOperationException("SSH authentication has not completed.");

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loaded || _disposed)
        {
            return;
        }

        _loaded = true;
        if (_authentication is null)
        {
            BeginPasswordPrompt(null);
        }
        else
        {
            StartConnection();
        }
    }

    private void StartConnection()
    {
        if (_authentication is null)
        {
            BeginPasswordPrompt(null);
            return;
        }

        Terminal.Connection = null!;
        _passwordPrompt?.Dispose();
        _passwordPrompt = null;
        DisposeActiveConnection();
        MessagePanel.Visibility = System.Windows.Visibility.Visible;
        StartProgress.Visibility = System.Windows.Visibility.Visible;
        RetryButton.Visibility = System.Windows.Visibility.Collapsed;
        ChangeCredentialsButton.Visibility = System.Windows.Visibility.Collapsed;
        MessageTitle.Text = LocalizationService.Get("Connecting");
        MessageBody.Text = _profile.DisplayEndpoint;

        _connection = new SshTerminalConnection(
            _profile,
            _authentication,
            _verifyHostKey,
            _route,
            Dispatcher);
        _connection.StateChanged += Connection_OnStateChanged;
        _connection.TerminalOutput += Connection_OnTerminalOutput;
        _connection.Start();
    }

    private void Connection_OnTerminalOutput(object? sender, TerminalOutputEventArgs e)
    {
        if (sender is not SshTerminalConnection connection || !ReferenceEquals(connection, _connection))
        {
            return;
        }

        _alternateScreen.Append(e.Data);
        if (!_alternateScreen.IsActive)
        {
            _wheelDeltaRemainder = 0;
        }
    }

    private void Connection_OnStateChanged(object? sender, TerminalHostStateChangedEventArgs e)
    {
        if (sender is not SshTerminalConnection connection || !ReferenceEquals(connection, _connection))
        {
            return;
        }

        switch (e.State)
        {
            case TerminalHostState.Connected:
                Terminal.Visibility = System.Windows.Visibility.Visible;
                Terminal.UpdateLayout();
                _terminalContainer ??= FindVisualChild<TerminalContainer>(Terminal);
                EnsureTerminalMouseHook();
                Terminal.Connection = connection;
                ApplyTheme();
                connection.AttachRenderer();
                MessagePanel.Visibility = System.Windows.Visibility.Collapsed;
                FocusTerminal();
                break;
            case TerminalHostState.Failed:
                Terminal.Connection = null!;
                if (e.AuthenticationFailed)
                {
                    BeginPasswordPrompt(
                        e.Message,
                        _authentication?.Kind == SshAuthenticationKind.PrivateKey);
                    StateChanged?.Invoke(
                        this,
                        new TerminalHostStateChangedEventArgs(
                            TerminalHostState.Starting,
                            LocalizationService.Get("WaitingForPassword")));
                    return;
                }

                ShowStopped(LocalizationService.Get("ConnectionFailed"), e.Message);
                break;
            case TerminalHostState.Exited:
                Terminal.Connection = null!;
                ShowStopped(LocalizationService.Get("SessionClosed"), _profile.DisplayEndpoint);
                break;
        }

        StateChanged?.Invoke(this, e);
    }

    private void ShowStopped(string title, string message)
    {
        Terminal.Visibility = System.Windows.Visibility.Collapsed;
        MessagePanel.Visibility = System.Windows.Visibility.Visible;
        StartProgress.Visibility = System.Windows.Visibility.Collapsed;
        RetryButton.Visibility = System.Windows.Visibility.Visible;
        ChangeCredentialsButton.Visibility = System.Windows.Visibility.Visible;
        MessageTitle.Text = title;
        MessageBody.Text = message;
    }

    private void RetryButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_disposed)
        {
            StartConnection();
        }
    }

    private void ChangeCredentialsButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_disposed)
        {
            BeginPasswordPrompt(null);
        }
    }

    private void BeginPasswordPrompt(string? failureMessage, bool privateKeyFailed = false)
    {
        if (_disposed)
        {
            return;
        }

        Terminal.Connection = null!;
        DisposeActiveConnection();
        _passwordPrompt?.Dispose();

        var output = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(failureMessage))
        {
            output.Append("\r\n\x1b[33m[")
                .Append(LocalizationService.Format(
                    privateKeyFailed ? "PrivateKeyFallback" : "PasswordRetry",
                    failureMessage))
                .Append("]\x1b[0m\r\n");
        }
        output.Append(LocalizationService.Format("TerminalPasswordPrompt", _profile.DisplayEndpoint));

        var prompt = new PasswordPromptTerminalConnection(output.ToString());
        prompt.PasswordSubmitted += PasswordPrompt_OnPasswordSubmitted;
        _passwordPrompt = prompt;

        MessagePanel.Visibility = System.Windows.Visibility.Collapsed;
        Terminal.Visibility = System.Windows.Visibility.Visible;
        Terminal.UpdateLayout();
        _terminalContainer ??= FindVisualChild<TerminalContainer>(Terminal);
        EnsureTerminalMouseHook();
        Terminal.Connection = prompt;
        ApplyTheme();
        prompt.AttachRenderer();
        FocusTerminal();
    }

    private void PasswordPrompt_OnPasswordSubmitted(object? sender, string password)
    {
        if (_disposed || sender is not PasswordPromptTerminalConnection prompt ||
            !ReferenceEquals(prompt, _passwordPrompt))
        {
            return;
        }

        prompt.PasswordSubmitted -= PasswordPrompt_OnPasswordSubmitted;
        _authentication = new SshAuthenticationOptions
        {
            Kind = SshAuthenticationKind.Password,
            Password = password,
        };
        StartConnection();
    }

    private void ApplyTheme()
    {
        var background = Color.FromRgb(30, 30, 30);
        Terminal.SetTheme(
            new TerminalTheme
            {
                DefaultBackground = ColorRef(30, 30, 30),
                DefaultForeground = ColorRef(229, 229, 229),
                DefaultSelectionBackground = ColorRef(55, 82, 109),
                CursorStyle = CursorStyle.BlinkingBar,
                ColorTable =
                [
                    ColorRef(12, 12, 12), ColorRef(197, 15, 31), ColorRef(19, 161, 14), ColorRef(193, 156, 0),
                    ColorRef(0, 55, 218), ColorRef(136, 23, 152), ColorRef(58, 150, 221), ColorRef(204, 204, 204),
                    ColorRef(118, 118, 118), ColorRef(231, 72, 86), ColorRef(22, 198, 12), ColorRef(249, 241, 165),
                    ColorRef(59, 120, 255), ColorRef(180, 0, 158), ColorRef(97, 214, 214), ColorRef(242, 242, 242),
                ],
            },
            "Cascadia Mono",
            10,
            background);
    }

    private static uint ColorRef(byte red, byte green, byte blue) =>
        red | ((uint)green << 8) | ((uint)blue << 16);

    public void FocusTerminal()
    {
        if (_disposed || Terminal.Visibility != System.Windows.Visibility.Visible)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (!_disposed && Terminal.Visibility == System.Windows.Visibility.Visible)
                {
                    _terminalContainer ??= FindVisualChild<TerminalContainer>(Terminal);
                    if (_terminalContainer is not null)
                    {
                        Keyboard.Focus(_terminalContainer);
                        _terminalContainer.Focus();
                        if (_terminalContainer.Handle != IntPtr.Zero)
                        {
                            SetFocus(_terminalContainer.Handle);
                        }
                    }
                    else
                    {
                        Terminal.Focus();
                    }
                }
            });
    }

    private void EnsureTerminalMouseHook()
    {
        if (_terminalMouseHooked || _terminalContainer is null) return;

        // TerminalContainer already delegates keyboard messages to Microsoft's
        // VT input engine. Intercepting them here would lose application-cursor
        // mode and can double-send keys to full-screen programs.
        _terminalContainer.MessageHook += TerminalContainerMessageHook;
        _terminalMouseHooked = true;
        if (InputDiagnostics)
            DiagnosticLog.Write("TerminalInput", $"Terminal mouse hook installed on HWND {_terminalContainer.Handle}; keyboard input is owned by Microsoft Terminal.");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed || e.Handled || (_connection is null && _passwordPrompt is null) ||
            Terminal.Visibility != System.Windows.Visibility.Visible || _terminalContainer is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!ShouldForwardNativeNavigationKey(key, Keyboard.Modifiers))
        {
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || _terminalContainer.Handle == IntPtr.Zero)
        {
            return;
        }

        // WPF treats the arrow and tab keys as focus traversal. Consume that
        // routed action and replay the original native key into Terminal's HWND;
        // Microsoft Terminal still owns application-mode and VT encoding.
        SetFocus(_terminalContainer.Handle);
        SendTerminalKey(_terminalContainer.Handle, virtualKey, 1);
        e.Handled = true;
    }

    internal static bool ShouldForwardNativeNavigationKey(Key key, ModifierKeys modifiers) =>
        key switch
        {
            Key.Tab => true,
            Key.Up or Key.Down or Key.Left or Key.Right => true,
            Key.Home or Key.End => true,
            Key.PageUp or Key.PageDown => true,
            Key.Delete => true,
            Key.Insert => (modifiers & ModifierKeys.Shift) == 0,
            _ => false,
        };

    private IntPtr TerminalContainerMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmLeftButtonDown = 0x0201;
        const int WmLeftButtonUp = 0x0202;
        const int WmMouseWheel = 0x020A;

        if (_disposed || _terminalContainer is null || hwnd != _terminalContainer.Handle)
        {
            return IntPtr.Zero;
        }

        if (message == WmLeftButtonDown)
        {
            SetFocus(hwnd);
            return IntPtr.Zero;
        }

        if (message == WmLeftButtonUp)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, CopySelectionToClipboard);
            return IntPtr.Zero;
        }

        if (message == WmMouseWheel && _connection is not null &&
            TryGetAlternateScreenWheelNavigation(
                message,
                wParam,
                _alternateScreen.IsActive,
                HasModifierKey(),
                ref _wheelDeltaRemainder,
                out var virtualKey,
                out var repeatCount))
        {
            handled = true;
            if (repeatCount > 0)
            {
                SendTerminalKey(hwnd, virtualKey, repeatCount);
                if (InputDiagnostics)
                {
                    DiagnosticLog.Write(
                        "TerminalInput",
                        $"Alternate-screen wheel sent {repeatCount} native key presses (VK 0x{virtualKey:X2}) to Microsoft Terminal.");
                }
            }
            return IntPtr.Zero;
        }

        if (TryGetPasteMouseAction(message, LocalizationService.MousePasteButton, out var pasteNow))
        {
            handled = true;
            if (pasteNow)
            {
                PasteClipboardText();
            }
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    internal static bool TryGetPasteMouseAction(
        int message,
        TerminalMousePasteButton button,
        out bool pasteNow)
    {
        const int WmRightButtonDown = 0x0204;
        const int WmRightButtonUp = 0x0205;
        const int WmMiddleButtonDown = 0x0207;
        const int WmMiddleButtonUp = 0x0208;
        var upMessage = button == TerminalMousePasteButton.Right ? WmRightButtonUp : WmMiddleButtonUp;
        pasteNow = message == upMessage;
        return message is WmRightButtonDown or WmRightButtonUp or WmMiddleButtonDown or WmMiddleButtonUp;
    }

    internal static bool TryGetAlternateScreenWheelNavigation(
        int message,
        IntPtr wParam,
        bool alternateScreen,
        bool hasModifier,
        ref int deltaRemainder,
        out int virtualKey,
        out int repeatCount)
    {
        const int WmMouseWheel = 0x020A;
        const int WheelDelta = 120;
        const int LinesPerDetent = 3;
        virtualKey = 0;
        repeatCount = 0;
        if (message != WmMouseWheel || !alternateScreen || hasModifier)
        {
            deltaRemainder = 0;
            return false;
        }

        deltaRemainder += ExtractWheelDelta(wParam);
        var detents = deltaRemainder / WheelDelta;
        deltaRemainder -= detents * WheelDelta;
        if (detents == 0)
        {
            return true;
        }

        virtualKey = detents > 0 ? 0x26 : 0x28;
        repeatCount = Math.Min(Math.Abs(detents) * LinesPerDetent, 30);
        return true;
    }

    internal static int ExtractWheelDelta(IntPtr wParam) =>
        unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));

    private static bool HasModifierKey() =>
        (GetKeyState(0x10) & 0x8000) != 0 ||
        (GetKeyState(0x11) & 0x8000) != 0 ||
        (GetKeyState(0x12) & 0x8000) != 0;

    private static void SendTerminalKey(IntPtr hwnd, int virtualKey, int repeatCount)
    {
        const int WmKeyDown = 0x0100;
        const int WmKeyUp = 0x0101;
        const uint MapVkToVscEx = 4;
        var mappedScanCode = MapVirtualKey(unchecked((uint)virtualKey), MapVkToVscEx);
        var scanCode = mappedScanCode & 0xFF;
        var extendedFlag = (mappedScanCode & 0xFF00) == 0 ? 0L : 1L << 24;
        var keyDown = new IntPtr(1L | ((long)scanCode << 16) | extendedFlag);
        var keyUp = new IntPtr(keyDown.ToInt64() | (1L << 30) | (1L << 31));

        for (var index = 0; index < repeatCount; index++)
        {
            SendMessage(hwnd, WmKeyDown, new IntPtr(virtualKey), keyDown);
            SendMessage(hwnd, WmKeyUp, new IntPtr(virtualKey), keyUp);
        }
    }

    private void CopySelectionToClipboard()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var selectedText = Terminal.GetSelectedText();
            if (!string.IsNullOrEmpty(selectedText))
            {
                System.Windows.Clipboard.SetText(selectedText);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("TerminalClipboard", $"Selection copy failed: {ex.Message}");
        }
    }

    private void PasteClipboardText()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    if (_connection is not null)
                    {
                        _connection.WriteInput(text);
                    }
                    else
                    {
                        _passwordPrompt?.WriteInput(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("TerminalClipboard", $"Clipboard paste failed: {ex.Message}");
        }
    }

    public void PasteClipboard() => PasteClipboardText();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PreviewKeyDown -= OnPreviewKeyDown;
        if (_terminalMouseHooked)
        {
            if (_terminalContainer is not null)
                _terminalContainer.MessageHook -= TerminalContainerMessageHook;
            _terminalMouseHooked = false;
        }
        Terminal.Connection = null!;
        _passwordPrompt?.Dispose();
        _passwordPrompt = null;
        DisposeActiveConnection();
        (_terminalContainer ?? FindVisualChild<TerminalContainer>(Terminal))?.Dispose();
        _terminalContainer = null;
    }

    private void DisposeActiveConnection()
    {
        if (_connection is not null)
        {
            _connection.StateChanged -= Connection_OnStateChanged;
            _connection.TerminalOutput -= Connection_OnTerminalOutput;
            _connection.Dispose();
            _connection = null;
        }
        _alternateScreen.Reset();
        _wheelDeltaRemainder = 0;
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

}
