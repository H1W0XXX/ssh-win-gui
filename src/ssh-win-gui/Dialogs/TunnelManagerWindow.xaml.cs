using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.App.Dialogs;

public partial class TunnelManagerWindow : Window
{
    private readonly SshTunnelService _service;
    private readonly IReadOnlyList<ConnectionProfile> _profiles;
    private readonly Func<SshHostKeyInfo, bool> _verifyHostKey;
    private SshTunnelSession? _subscribedSession;

    public TunnelManagerWindow(
        SshTunnelService service,
        IReadOnlyList<ConnectionProfile> profiles,
        Func<SshHostKeyInfo, bool> verifyHostKey)
    {
        InitializeComponent();
        _service = service;
        _profiles = profiles;
        _verifyHostKey = verifyHostKey;
        DataContext = service;
        ProfileInput.ItemsSource = profiles
            .OrderBy(profile => profile.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(profile => new ProfileChoice(profile)).ToArray();
        ModeInput.ItemsSource = new[]
        {
            new ModeChoice(SshTunnelKind.LocalForward, LocalizationService.Get("TunnelLocalForward")),
            new ModeChoice(SshTunnelKind.RemoteForward, LocalizationService.Get("TunnelRemoteForward")),
            new ModeChoice(SshTunnelKind.LocalSocks5, LocalizationService.Get("TunnelLocalSocks5")),
            new ModeChoice(SshTunnelKind.RemoteSocks5, LocalizationService.Get("TunnelRemoteSocks5")),
        };
        if (ProfileInput.Items.Count > 0) ProfileInput.SelectedIndex = 0;
        ModeInput.SelectedIndex = 0;
        if (_service.Sessions.Count > 0) TunnelGrid.SelectedIndex = 0;
    }

    private void ModeInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ModeInput.SelectedItem is not ModeChoice choice) return;
        var socks = choice.Kind is SshTunnelKind.LocalSocks5 or SshTunnelKind.RemoteSocks5;
        TargetInput.IsEnabled = !socks;
        TargetLabel.IsEnabled = !socks;
        ListenInput.Text = choice.Kind switch
        {
            SshTunnelKind.LocalForward => "127.0.0.1:2888",
            SshTunnelKind.RemoteForward => "127.0.0.1:3888",
            _ => "127.0.0.1:1080",
        };
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (ProfileInput.SelectedItem is not ProfileChoice profileChoice ||
            ModeInput.SelectedItem is not ModeChoice modeChoice)
        {
            ValidationText.Text = LocalizationService.Get("TunnelSelectSession");
            return;
        }
        if (!TunnelEndpoint.TryParse(ListenInput.Text, out var listen, out var listenError))
        {
            ValidationText.Text = TranslateEndpointError(listenError);
            return;
        }
        TunnelEndpoint? target = null;
        if (modeChoice.Kind is SshTunnelKind.LocalForward or SshTunnelKind.RemoteForward &&
            !TunnelEndpoint.TryParse(TargetInput.Text, out target, out var targetError))
        {
            ValidationText.Text = TranslateEndpointError(targetError);
            return;
        }

        var profile = profileChoice.Profile;
        IReadOnlyList<ConnectionProfile> route;
        try { route = SshRouteResolver.Resolve(profile, _profiles); }
        catch (Exception ex) { ValidationText.Text = ex.Message; return; }

        SshAuthenticationOptions? authentication = null;
        if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            authentication = new SshAuthenticationOptions
            {
                Kind = SshAuthenticationKind.PrivateKey,
                PrivateKeyPath = Environment.ExpandEnvironmentVariables(profile.PrivateKeyPath),
            };
        }
        else
        {
            var authDialog = new AuthenticationDialog(profile) { Owner = this };
            if (authDialog.ShowDialog() != true) return;
            authentication = authDialog.Authentication;
        }
        if (authentication is null) return;

        var definition = new SshTunnelDefinition(
            Guid.NewGuid().ToString("N"), profile, modeChoice.Kind, listen!, target);
        try
        {
            var session = await _service.StartAsync(definition, authentication, route, _verifyHostKey);
            TunnelGrid.SelectedItem = session;
        }
        catch (Exception ex)
        {
            ValidationText.Text = LocalizationService.Format("TunnelStartFailed", ex.Message);
            if (_service.Sessions.Count > 0) TunnelGrid.SelectedItem = _service.Sessions[0];
        }
    }

    private void TunnelGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_subscribedSession is not null) _subscribedSession.PropertyChanged -= Session_OnPropertyChanged;
        _subscribedSession = TunnelGrid.SelectedItem as SshTunnelSession;
        if (_subscribedSession is not null) _subscribedSession.PropertyChanged += Session_OnPropertyChanged;
        RefreshLog();
    }

    private void Session_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SshTunnelSession.LogText))
            Dispatcher.BeginInvoke(RefreshLog);
    }

    private void RefreshLog() => LogText.Text = _subscribedSession?.LogText ?? string.Empty;

    private void CopyLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(LogText.Text)) Clipboard.SetText(LogText.Text);
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        (TunnelGrid.SelectedItem as SshTunnelSession)?.Stop();
        RefreshLog();
    }

    private void StopAllButton_OnClick(object sender, RoutedEventArgs e)
    {
        _service.StopAll();
        RefreshLog();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribedSession is not null) _subscribedSession.PropertyChanged -= Session_OnPropertyChanged;
        base.OnClosed(e);
    }

    private static string TranslateEndpointError(string error) => error switch
    {
        "Endpoint is required." => LocalizationService.Get("TunnelEndpointRequired"),
        "Use [IPv6]:port for an IPv6 endpoint." => LocalizationService.Get("TunnelIpv6Format"),
        "Endpoint host is required." => LocalizationService.Get("TunnelHostRequired"),
        "Endpoint port must be between 1 and 65535." => LocalizationService.Get("TunnelPortRange"),
        _ => error,
    };

    private sealed record ProfileChoice(ConnectionProfile Profile)
    {
        public string DisplayName => $"{Profile.Group} / {Profile.Name}  ·  {Profile.DisplayEndpoint}";
    }

    private sealed record ModeChoice(SshTunnelKind Kind, string DisplayName);
}
