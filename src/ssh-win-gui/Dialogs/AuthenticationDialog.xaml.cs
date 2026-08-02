using Microsoft.Win32;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using File = System.IO.File;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;

namespace RsyncShell.App.Dialogs;

public partial class AuthenticationDialog : System.Windows.Window
{
    private readonly ConnectionProfile _profile;

    public AuthenticationDialog(ConnectionProfile profile)
    {
        _profile = profile;
        InitializeComponent();
        EndpointText.Text = profile.DisplayEndpoint;
        PrivateKeyPathInput.Text = profile.PrivateKeyPath ?? FindDefaultPrivateKey() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            PrivateKeyRadio.IsChecked = true;
        }

        UpdateAuthenticationKind();
        Loaded += (_, _) =>
        {
            if (PrivateKeyRadio.IsChecked == true)
            {
                PrivateKeyPassphraseInput.Focus();
            }
            else
            {
                PasswordInput.Focus();
            }
        };
    }

    public SshAuthenticationOptions? Authentication { get; private set; }

    private void AuthenticationKind_OnChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateAuthenticationKind();
        }
    }

    private void UpdateAuthenticationKind()
    {
        var privateKey = PrivateKeyRadio.IsChecked == true;
        PrivateKeyPanel.IsEnabled = privateKey;
        PasswordInput.IsEnabled = !privateKey;
    }

    private void BrowseKeyButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationService.Get("SelectPrivateKey"),
            CheckFileExists = true,
            Filter = LocalizationService.Get("PrivateKeyFilter"),
        };
        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyPathInput.Text = dialog.FileName;
        }
    }

    private void ConnectButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (PrivateKeyRadio.IsChecked == true)
        {
            var keyPath = Environment.ExpandEnvironmentVariables(PrivateKeyPathInput.Text.Trim());
            if (!File.Exists(keyPath))
            {
                ValidationText.Text = LocalizationService.Get("ErrorPrivateKeyMissing");
                return;
            }

            Authentication = new SshAuthenticationOptions
            {
                Kind = SshAuthenticationKind.PrivateKey,
                PrivateKeyPath = keyPath,
                PrivateKeyPassphrase = PrivateKeyPassphraseInput.Password,
            };
        }
        else
        {
            if (string.IsNullOrEmpty(PasswordInput.Password))
            {
                ValidationText.Text = LocalizationService.Get("ErrorAuthenticationRequired");
                return;
            }
            Authentication = new SshAuthenticationOptions
            {
                Kind = SshAuthenticationKind.Password,
                Password = PasswordInput.Password,
            };
        }

        DialogResult = true;
    }

    private static string? FindDefaultPrivateKey()
    {
        var sshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh");
        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var candidate = Path.Combine(sshDirectory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
