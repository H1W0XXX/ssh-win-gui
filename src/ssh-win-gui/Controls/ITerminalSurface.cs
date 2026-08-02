using RsyncShell.Core.Models;

namespace RsyncShell.App.Controls;

public interface ITerminalSurface : IDisposable
{
    ConnectionProfile Profile { get; }
    SshAuthenticationOptions Authentication { get; }
    event EventHandler<TerminalHostStateChangedEventArgs>? StateChanged;
    void FocusTerminal();
    void PasteClipboard();
}
