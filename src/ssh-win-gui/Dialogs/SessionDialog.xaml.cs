using System.Globalization;
using System.IO;
using Microsoft.Win32;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.App.Dialogs;

public partial class SessionDialog : System.Windows.Window
{
    private readonly ConnectionProfile? _existing;
    private readonly IReadOnlyList<ConnectionProfile> _savedProfiles;

    public SessionDialog(ConnectionProfile? existing = null, IReadOnlyList<ConnectionProfile>? savedProfiles = null)
    {
        _existing = existing;
        _savedProfiles = savedProfiles ?? [];
        InitializeComponent();
        Title = LocalizationService.Get(existing is null ? "SessionNewTitle" : "SessionEditTitle");
        NameInput.Text = existing?.Name ?? string.Empty;
        HostInput.Text = existing?.Host ?? string.Empty;
        PortInput.Text = (existing?.Port ?? 22).ToString(CultureInfo.InvariantCulture);
        UsernameInput.Text = existing?.Username ?? "root";
        GroupInput.ItemsSource = BuildGroupChoices(_savedProfiles, existing?.Group);
        GroupInput.Text = existing?.Group ?? "Sessions";
        PrivateKeyInput.ItemsSource = BuildPrivateKeyChoices(_savedProfiles, existing?.PrivateKeyPath);
        PrivateKeyInput.Text = existing?.PrivateKeyPath ?? string.Empty;
        FavoriteInput.IsChecked = existing?.Favorite ?? false;
        ProxyTypeInput.Items.Add(new ProxyChoice(SshProxyKind.None, LocalizationService.Get("ProxyNone")));
        ProxyTypeInput.Items.Add(new ProxyChoice(SshProxyKind.Socks5, LocalizationService.Get("ProxySocks5")));
        ProxyTypeInput.Items.Add(new ProxyChoice(SshProxyKind.JumpHost, LocalizationService.Get("ProxyJumpHost")));
        ProxyTypeInput.DisplayMemberPath = nameof(ProxyChoice.Display);
        ProxyTypeInput.SelectedIndex = (int)(existing?.ProxyKind ?? SshProxyKind.None);
        ProxyHostInput.Text = existing?.ProxyHost ?? "127.0.0.1";
        ProxyPortInput.Text = (existing?.ProxyPort ?? 1080).ToString(CultureInfo.InvariantCulture);
        var currentId = existing?.Id ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        foreach (var candidate in _savedProfiles.Where(candidate =>
                     !string.IsNullOrWhiteSpace(candidate.PrivateKeyPath) &&
                     !string.Equals(candidate.Id, existing?.Id, StringComparison.Ordinal) &&
                     !(existing is not null &&
                       string.Equals(candidate.Host, existing.Host, StringComparison.OrdinalIgnoreCase) &&
                       candidate.Port == existing.Port &&
                       string.Equals(candidate.Username, existing.Username, StringComparison.OrdinalIgnoreCase)) &&
                     !SshRouteResolver.WouldCreateCycle(currentId, candidate.Id, _savedProfiles)))
        {
            JumpProfileInput.Items.Add(new JumpChoice(candidate.Id, $"{candidate.Name}  ({candidate.DisplayEndpoint})"));
        }
        JumpProfileInput.SelectedItem = JumpProfileInput.Items.Cast<JumpChoice>()
            .FirstOrDefault(item => string.Equals(item.Id, existing?.JumpProfileId, StringComparison.Ordinal));
        UpdateProxyFields();
        Loaded += (_, _) => (existing is null ? NameInput : HostInput).Focus();
    }

    public ConnectionProfile? Profile { get; private set; }

    public bool ConnectAfterSave { get; private set; }

    private void ProxyTypeInput_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        UpdateProxyFields();

    private void UpdateProxyFields()
    {
        if (ProxyTypeInput?.SelectedItem is not ProxyChoice choice || SocksProxyPanel is null) return;
        var socks = choice.Kind == SshProxyKind.Socks5;
        var jump = choice.Kind == SshProxyKind.JumpHost;
        ProxyDetailsLabel.Visibility = socks || jump ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ProxyDetailsLabel.Text = LocalizationService.Get(socks ? "ProxyServer" : "JumpSession");
        SocksProxyPanel.Visibility = socks ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        JumpProfileInput.Visibility = jump ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private void BrowseButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("SelectPrivateKey"),
            CheckFileExists = true,
            Filter = LocalizationService.Get("PrivateKeyFilter"),
        };
        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyInput.Text = dialog.FileName;
        }
    }

    private void SaveButton_OnClick(object sender, System.Windows.RoutedEventArgs e) => Complete(connect: false);

    private void SaveConnectButton_OnClick(object sender, System.Windows.RoutedEventArgs e) => Complete(connect: true);

    internal static IReadOnlyList<string> BuildGroupChoices(
        IEnumerable<ConnectionProfile> savedProfiles,
        string? currentGroup = null)
    {
        var groups = savedProfiles
            .Select(profile => profile.Group.Trim())
            .Append(currentGroup?.Trim() ?? string.Empty)
            .Append("Sessions")
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => string.Equals(group, "Sessions", StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(group => group, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return groups;
    }

    internal static IReadOnlyList<string> BuildPrivateKeyChoices(
        IEnumerable<ConnectionProfile> savedProfiles,
        string? currentPrivateKeyPath = null,
        string? userProfilePath = null)
    {
        var choices = new List<string>();

        AddChoice(currentPrivateKeyPath);
        foreach (var profile in savedProfiles)
        {
            AddChoice(profile.PrivateKeyPath);
        }

        var profilePath = userProfilePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sshDirectory = Path.Combine(profilePath, ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var candidate = Path.Combine(sshDirectory, name);
            if (File.Exists(candidate))
            {
                AddChoice(candidate);
            }
        }

        return choices;

        void AddChoice(string? path)
        {
            var value = path?.Trim();
            if (!string.IsNullOrEmpty(value) &&
                !choices.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                choices.Add(value);
            }
        }
    }

    private void Complete(bool connect)
    {
        ValidationText.Text = string.Empty;
        var name = NameInput.Text.Trim();
        var host = HostInput.Text.Trim();
        var username = UsernameInput.Text.Trim();
        var group = GroupInput.Text.Trim();
        var privateKeyPath = Environment.ExpandEnvironmentVariables(PrivateKeyInput.Text.Trim());

        if (name.Length == 0)
        {
            ValidationText.Text = LocalizationService.Get("ErrorSessionNameRequired");
            return;
        }
        if (host.Length == 0)
        {
            ValidationText.Text = LocalizationService.Get("ErrorHostRequired");
            return;
        }
        if (username.Length == 0)
        {
            ValidationText.Text = LocalizationService.Get("ErrorUsernameRequired");
            return;
        }
        if (!int.TryParse(PortInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            ValidationText.Text = LocalizationService.Get("ErrorPortRange");
            return;
        }
        if (privateKeyPath.Length > 0 && !File.Exists(privateKeyPath))
        {
            ValidationText.Text = LocalizationService.Get("ErrorPrivateKeyMissing");
            return;
        }
        var proxyKind = (ProxyTypeInput.SelectedItem as ProxyChoice)?.Kind ?? SshProxyKind.None;
        var proxyHost = ProxyHostInput.Text.Trim();
        if (proxyKind == SshProxyKind.Socks5 && proxyHost.Length == 0)
        {
            ValidationText.Text = LocalizationService.Get("ErrorProxyHostRequired");
            return;
        }
        var proxyPort = 1080;
        if (proxyKind == SshProxyKind.Socks5 &&
            (!int.TryParse(ProxyPortInput.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out proxyPort) ||
             proxyPort is < 1 or > 65535))
        {
            ValidationText.Text = LocalizationService.Get("ErrorProxyPortRange");
            return;
        }
        var jump = JumpProfileInput.SelectedItem as JumpChoice;
        if (proxyKind == SshProxyKind.JumpHost && jump is null)
        {
            ValidationText.Text = LocalizationService.Get("ErrorJumpSessionRequired");
            return;
        }

        Profile = new ConnectionProfile
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Name = name,
            Host = host,
            Port = port,
            Username = username,
            Group = group.Length == 0 ? "Sessions" : group,
            PrivateKeyPath = privateKeyPath.Length == 0 ? null : privateKeyPath,
            Favorite = FavoriteInput.IsChecked == true,
            ProxyKind = proxyKind,
            ProxyHost = proxyKind == SshProxyKind.Socks5 ? proxyHost : null,
            ProxyPort = proxyPort,
            JumpProfileId = proxyKind == SshProxyKind.JumpHost ? jump?.Id : null,
        };
        ConnectAfterSave = connect;
        DialogResult = true;
    }

    private sealed record ProxyChoice(SshProxyKind Kind, string Display);
    private sealed record JumpChoice(string Id, string Display);
}
