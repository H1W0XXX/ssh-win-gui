using System.Windows;

namespace RsyncShell.App.Dialogs;

public partial class CommandPreviewDialog : Window
{
    public CommandPreviewDialog(string commandText)
    {
        InitializeComponent();
        CommandTextBox.Text = commandText;
        Loaded += (_, _) =>
        {
            CommandTextBox.Focus();
            CommandTextBox.SelectAll();
        };
    }

    private void CopyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(CommandTextBox.Text))
        {
            Clipboard.SetText(CommandTextBox.Text);
        }
    }
}
