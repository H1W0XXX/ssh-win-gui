using RsyncShell.App.Services;

namespace RsyncShell.App.Dialogs;

public partial class RenameRemoteItemDialog : System.Windows.Window
{
    public RenameRemoteItemDialog(string currentName)
    {
        InitializeComponent();
        NameInput.Text = currentName;
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    public string NewName { get; private set; } = string.Empty;

    private void RenameButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var value = NameInput.Text.Trim();
        if (value.Length == 0 || value is "." or ".." || value.Contains('/') || value.Contains('\0'))
        {
            System.Windows.MessageBox.Show(
                this,
                LocalizationService.Get("InvalidRemoteName"),
                LocalizationService.Get("RenameRemoteTitle"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }
        NewName = value;
        DialogResult = true;
    }
}
