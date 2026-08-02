using RsyncShell.App.Services;

namespace RsyncShell.App.Dialogs;

public partial class KeywordHighlightingDialog : System.Windows.Window
{
    public KeywordHighlightingDialog(TerminalKeywordRules rules)
    {
        InitializeComponent();
        Populate(rules);
    }

    public TerminalKeywordRules? Rules { get; private set; }

    private void RestoreDefaultsButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        Populate(TerminalKeywordRules.Default);
        ValidationText.Text = string.Empty;
    }

    private void SaveButton_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        var green = ParseLines(GreenKeywordsInput.Text);
        var red = ParseLines(RedKeywordsInput.Text);
        var yellow = ParseLines(YellowKeywordsInput.Text);
        var all = green.Concat(red).Concat(yellow).ToArray();

        if (all.Length > TerminalKeywordRules.MaximumKeywordCount)
        {
            ValidationText.Text = LocalizationService.Format(
                "ErrorTooManyKeywords",
                TerminalKeywordRules.MaximumKeywordCount);
            return;
        }

        var tooLong = all.FirstOrDefault(keyword => keyword.Length > TerminalKeywordRules.MaximumKeywordLength);
        if (tooLong is not null)
        {
            ValidationText.Text = LocalizationService.Format(
                "ErrorKeywordTooLong",
                TerminalKeywordRules.MaximumKeywordLength,
                tooLong);
            return;
        }

        var duplicate = all
            .GroupBy(keyword => keyword, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            ValidationText.Text = LocalizationService.Format("ErrorDuplicateKeyword", duplicate);
            return;
        }

        Rules = TerminalKeywordRules.CreateNormalized(green, red, yellow);
        DialogResult = true;
    }

    private void Populate(TerminalKeywordRules rules)
    {
        GreenKeywordsInput.Text = string.Join(Environment.NewLine, rules.Green);
        RedKeywordsInput.Text = string.Join(Environment.NewLine, rules.Red);
        YellowKeywordsInput.Text = string.Join(Environment.NewLine, rules.Yellow);
    }

    internal static string[] ParseLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
