namespace RsyncShell.App.Controls;

public enum TerminalHostState
{
    Starting,
    Connected,
    Exited,
    Failed,
}

public sealed record TerminalHostStateChangedEventArgs(
    TerminalHostState State,
    string Message,
    bool AuthenticationFailed = false);
