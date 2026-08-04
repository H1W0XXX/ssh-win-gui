using System.Windows;

namespace RsyncShell.App.Dialogs;

public partial class CommandPreviewDialog : Window
{
    public CommandPreviewDialog(string commandText, string? title = null, string? copyButtonText = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title;
        }
        if (!string.IsNullOrWhiteSpace(copyButtonText))
        {
            CopyButton.Content = copyButtonText;
        }
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
