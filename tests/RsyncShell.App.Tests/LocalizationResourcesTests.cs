using System.Xml.Linq;
using RsyncShell.App.Services;

namespace RsyncShell.App.Tests;

public sealed class LocalizationResourcesTests
{
    [Theory]
    [InlineData(null, TerminalMousePasteButton.Middle)]
    [InlineData("middle", TerminalMousePasteButton.Middle)]
    [InlineData("right", TerminalMousePasteButton.Right)]
    [InlineData("RIGHT", TerminalMousePasteButton.Right)]
    public void MousePasteSettingUsesMiddleAsSafeDefault(string? value, TerminalMousePasteButton expected)
    {
        Assert.Equal(expected, LocalizationService.ParseMousePasteButton(value));
    }

    [Fact]
    public void EnglishAndChineseAreTheOnlyLocalesAndHaveMatchingKeys()
    {
        Assert.Equal(["en", "zh-CN"], LocalizationService.SupportedLanguages);

        var directory = Path.Combine(AppContext.BaseDirectory, "Localization");
        var files = Directory.GetFiles(directory, "Strings.*.xaml")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["Strings.en.xaml", "Strings.zh-CN.xaml"],
            files.Select(path => Path.GetFileName(path)!).ToArray());

        var english = ReadKeys(files.Single(path => path.EndsWith("Strings.en.xaml", StringComparison.Ordinal)));
        var chinese = ReadKeys(files.Single(path => path.EndsWith("Strings.zh-CN.xaml", StringComparison.Ordinal)));
        Assert.NotEmpty(english);
        Assert.Equal(english, chinese);
    }

    private static string[] ReadKeys(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(path);
        var keys = document.Root!.Elements()
            .Select(element => new
            {
                Key = (string?)element.Attribute(x + "Key"),
                Value = element.Value,
            })
            .Select(entry =>
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Key));
                Assert.False(string.IsNullOrWhiteSpace(entry.Value));
                return entry.Key!;
            })
            .ToArray();
        var duplicates = keys
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Assert.True(duplicates.Length == 0, $"Duplicate resource keys: {string.Join(", ", duplicates)}");
        return keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
    }
}
