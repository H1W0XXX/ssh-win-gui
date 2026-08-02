using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
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
    private bool _terminalInputHooked;
    private bool _threadInputHooked;
    private bool _shiftKeyDown;
    private bool _controlKeyDown;
    private bool _altKeyDown;
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
        PreviewKeyUp += OnPreviewKeyUp;
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
        _connection?.Dispose();
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
        _connection.Start();
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
                EnsureTerminalInputHook();
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
        _connection?.Dispose();
        _connection = null;
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
        EnsureTerminalInputHook();
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

    private void EnsureTerminalInputHook()
    {
        if (_terminalInputHooked || _terminalContainer is null) return;
        _terminalContainer.MessageHook += TerminalContainerMessageHook;
        _terminalInputHooked = true;
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
        _threadInputHooked = true;
        if (InputDiagnostics)
            DiagnosticLog.Write("TerminalInput", $"Native terminal input hook installed on HWND {_terminalContainer.Handle}.");
    }

    private void OnThreadPreprocessMessage(ref MSG message, ref bool handled)
    {
        try
        {
            if (_disposed || handled || !IsKeyboardMessage(message.message))
            {
                return;
            }

            var virtualKey = ExtractVirtualKey(message.wParam);
            UpdateModifierKeyState(message.message, virtualKey);
            if ((_connection is null && _passwordPrompt is null) || _terminalContainer is null ||
                !OwnsNativeWindow(message.hwnd))
            {
                return;
            }

            if (TryGetKeyboardPasteAction(
                    message.message,
                    virtualKey,
                    _shiftKeyDown || IsVirtualKeyDown(0x10),
                    _controlKeyDown || IsVirtualKeyDown(0x11),
                    _altKeyDown || IsVirtualKeyDown(0x12),
                    out var pasteNow))
            {
                if (pasteNow)
                {
                    PasteClipboardText();
                    if (InputDiagnostics)
                    {
                        DiagnosticLog.Write("TerminalInput", "Shift+Insert pasted clipboard text.");
                    }
                }
                handled = true;
                return;
            }

            if (_connection is null || HasNavigationModifier())
            {
                return;
            }

            if (TryTranslateNavigationMessage(message.message, virtualKey, out var sequence, out var write))
            {
                if (write && sequence is not null)
                {
                    _connection.WriteInput(sequence);
                    if (InputDiagnostics)
                    {
                        DiagnosticLog.Write(
                            "TerminalInput",
                            $"Thread message forwarded virtual key 0x{virtualKey:X2} as {Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(sequence))}.");
                    }
                }
                handled = true;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("TerminalInput", ex);
        }
    }

    private static bool IsKeyboardMessage(int message) =>
        message is 0x0100 or 0x0101 or 0x0104 or 0x0105;

    internal static int ExtractVirtualKey(IntPtr wParam) =>
        unchecked((int)wParam.ToInt64()) & 0xFFFF;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed || e.Handled || (_connection is null && _passwordPrompt is null))
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        UpdateWpfModifierKeyState(key, isDown: true);
        if (IsShiftInsert(
                key,
                Keyboard.Modifiers,
                _shiftKeyDown || IsVirtualKeyDown(0x10),
                _controlKeyDown || IsVirtualKeyDown(0x11),
                _altKeyDown || IsVirtualKeyDown(0x12)))
        {
            PasteClipboardText();
            e.Handled = true;
            return;
        }

        if (_connection is null || HasNavigationModifier())
        {
            return;
        }

        if (TranslateNavigationKey(key) is { } sequence)
        {
            _connection.WriteInput(sequence);
            e.Handled = true;
            if (InputDiagnostics)
            {
                DiagnosticLog.Write(
                    "TerminalInput",
                    $"WPF preview forwarded {key} as {Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(sequence))}.");
            }
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        UpdateWpfModifierKeyState(key, isDown: false);
    }

    private void UpdateWpfModifierKeyState(Key key, bool isDown)
    {
        switch (key)
        {
            case Key.LeftShift:
            case Key.RightShift:
                _shiftKeyDown = isDown;
                break;
            case Key.LeftCtrl:
            case Key.RightCtrl:
                _controlKeyDown = isDown;
                break;
            case Key.LeftAlt:
            case Key.RightAlt:
                _altKeyDown = isDown;
                break;
        }
    }

    private bool OwnsNativeWindow(IntPtr hwnd)
    {
        var terminalHandle = _terminalContainer?.Handle ?? IntPtr.Zero;
        return terminalHandle != IntPtr.Zero &&
               (hwnd == terminalHandle || IsChild(terminalHandle, hwnd));
    }

    private static bool HasNavigationModifier() =>
        IsVirtualKeyDown(0x10) || IsVirtualKeyDown(0x11) || IsVirtualKeyDown(0x12);

    private static bool IsVirtualKeyDown(int virtualKey) =>
        (GetKeyState(virtualKey) & 0x8000) != 0 ||
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void UpdateModifierKeyState(int message, int virtualKey)
    {
        var isDown = message is 0x0100 or 0x0104;
        var isUp = message is 0x0101 or 0x0105;
        if (!isDown && !isUp)
        {
            return;
        }

        var value = isDown;
        switch (virtualKey)
        {
            case 0x10:
            case 0xA0:
            case 0xA1:
                _shiftKeyDown = value;
                break;
            case 0x11:
            case 0xA2:
            case 0xA3:
                _controlKeyDown = value;
                break;
            case 0x12:
            case 0xA4:
            case 0xA5:
                _altKeyDown = value;
                break;
        }
    }

    internal static bool IsShiftInsert(
        Key key,
        ModifierKeys modifiers,
        bool trackedShift = false,
        bool trackedControl = false,
        bool trackedAlt = false) =>
        key == Key.Insert &&
        (modifiers == ModifierKeys.Shift ||
         (trackedShift && !trackedControl && !trackedAlt));

    internal static bool TryGetKeyboardPasteAction(
        int message,
        int virtualKey,
        bool shiftPressed,
        bool controlPressed,
        bool altPressed,
        out bool pasteNow)
    {
        const int WmKeyDown = 0x0100;
        const int WmKeyUp = 0x0101;
        const int VkInsert = 0x2D;
        var isShortcut = virtualKey == VkInsert && shiftPressed && !controlPressed && !altPressed;
        pasteNow = isShortcut && message == WmKeyDown;
        return isShortcut && (message == WmKeyDown || message == WmKeyUp);
    }

    internal static bool TryTranslateNavigationMessage(
        int message,
        int virtualKey,
        out string? sequence,
        out bool write)
    {
        const int WmKeyDown = 0x0100;
        const int WmKeyUp = 0x0101;
        if (message != WmKeyDown && message != WmKeyUp)
        {
            sequence = null;
            write = false;
            return false;
        }

        sequence = TranslateNavigationKey(KeyInterop.KeyFromVirtualKey(virtualKey));
        write = message == WmKeyDown && sequence is not null;
        return sequence is not null;
    }

    private IntPtr TerminalContainerMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        const int WmLeftButtonDown = 0x0201;
        const int WmLeftButtonUp = 0x0202;

        if (_disposed || _terminalContainer is null || hwnd != _terminalContainer.Handle)
        {
            return IntPtr.Zero;
        }

        if (IsKeyboardMessage(message))
        {
            var virtualKey = ExtractVirtualKey(wParam);
            UpdateModifierKeyState(message, virtualKey);
            if (TryGetKeyboardPasteAction(
                    message,
                    virtualKey,
                    _shiftKeyDown || IsVirtualKeyDown(0x10),
                    _controlKeyDown || IsVirtualKeyDown(0x11),
                    _altKeyDown || IsVirtualKeyDown(0x12),
                    out var keyboardPasteNow))
            {
                handled = true;
                if (keyboardPasteNow)
                {
                    PasteClipboardText();
                    if (InputDiagnostics)
                    {
                        DiagnosticLog.Write("TerminalInput", "Native terminal hook handled Shift+Insert paste.");
                    }
                }
                return IntPtr.Zero;
            }
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

    internal static string? TranslateNavigationKey(Key key) => key switch
    {
        Key.Up => "\x1b[A",
        Key.Down => "\x1b[B",
        Key.Right => "\x1b[C",
        Key.Left => "\x1b[D",
        Key.Home => "\x1b[H",
        Key.End => "\x1b[F",
        Key.Insert => "\x1b[2~",
        Key.Delete => "\x1b[3~",
        Key.PageUp => "\x1b[5~",
        Key.PageDown => "\x1b[6~",
        _ => null,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PreviewKeyDown -= OnPreviewKeyDown;
        PreviewKeyUp -= OnPreviewKeyUp;
        if (_threadInputHooked)
        {
            ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
            _threadInputHooked = false;
        }
        if (_terminalInputHooked)
        {
            if (_terminalContainer is not null)
                _terminalContainer.MessageHook -= TerminalContainerMessageHook;
            _terminalInputHooked = false;
        }
        Terminal.Connection = null!;
        _passwordPrompt?.Dispose();
        _passwordPrompt = null;
        _connection?.Dispose();
        _connection = null;
        (_terminalContainer ?? FindVisualChild<TerminalContainer>(Terminal))?.Dispose();
        _terminalContainer = null;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

}
