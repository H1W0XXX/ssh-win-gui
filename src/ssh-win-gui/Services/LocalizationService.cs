using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace RsyncShell.App.Services;

public enum TerminalMousePasteButton
{
    Middle,
    Right,
}

public static class LocalizationService
{
    private const string English = "en";
    private const string Chinese = "zh-CN";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RsyncShell",
        "settings.json");
    private static ResourceDictionary? _languageResources;

    public static IReadOnlyList<string> SupportedLanguages { get; } = [English, Chinese];

    public static string CurrentLanguage { get; private set; } = English;

    public static TerminalMousePasteButton MousePasteButton { get; private set; } = TerminalMousePasteButton.Middle;

    public static bool KeywordHighlightingEnabled { get; private set; } = true;

    public static TerminalKeywordRules KeywordHighlightingRules { get; private set; } = TerminalKeywordRules.Default;

    public static bool IsChinese => string.Equals(CurrentLanguage, Chinese, StringComparison.Ordinal);

    public static event EventHandler? LanguageChanged;

    public static void Initialize()
    {
        var settings = LoadSettings();
        var language = settings.Language;
        MousePasteButton = ParseMousePasteButton(settings.MousePasteButton);
        KeywordHighlightingEnabled = settings.KeywordHighlightingEnabled ?? true;
        KeywordHighlightingRules = settings.KeywordHighlightingRules ?? TerminalKeywordRules.Default;
        if (!SupportedLanguages.Contains(language, StringComparer.Ordinal))
        {
            language = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? Chinese
                : English;
        }

        ApplyLanguage(language, persist: false);
    }

    public static void SetLanguage(string language)
    {
        if (!SupportedLanguages.Contains(language, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported UI language.");
        }

        if (string.Equals(CurrentLanguage, language, StringComparison.Ordinal))
        {
            return;
        }

        ApplyLanguage(language, persist: true);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void SetMousePasteButton(TerminalMousePasteButton button)
    {
        if (MousePasteButton == button)
        {
            return;
        }

        MousePasteButton = button;
        SaveSettings();
    }

    public static void SetKeywordHighlightingEnabled(bool enabled)
    {
        if (KeywordHighlightingEnabled == enabled)
        {
            return;
        }

        KeywordHighlightingEnabled = enabled;
        SaveSettings();
    }

    public static void SetKeywordHighlightingRules(TerminalKeywordRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        KeywordHighlightingRules = TerminalKeywordRules.CreateNormalized(rules.Green, rules.Red, rules.Yellow);
        SaveSettings();
    }

    internal static TerminalMousePasteButton ParseMousePasteButton(string? value) =>
        string.Equals(value, "right", StringComparison.OrdinalIgnoreCase)
            ? TerminalMousePasteButton.Right
            : TerminalMousePasteButton.Middle;

    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), values);

    public static string TranslateProfileError(string error) => error switch
    {
        "Invalid ssh:// address." => Get("ErrorInvalidSshAddress"),
        "Inline SSH passwords are not accepted. Enter the password in the authentication dialog." => Get("ErrorInlinePassword"),
        "Use user@host:port or ssh://user@host:port." => Get("ErrorQuickConnectFormat"),
        "Invalid bracketed IPv6 address." => Get("ErrorInvalidIpv6"),
        "Invalid SSH port." => Get("ErrorInvalidPort"),
        "Host is required." => Get("ErrorHostRequired"),
        "Username is required." => Get("ErrorUsernameRequired"),
        "SSH port must be between 1 and 65535." => Get("ErrorPortRange"),
        _ => error,
    };

    private static void ApplyLanguage(string language, bool persist)
    {
        var resources = new ResourceDictionary
        {
            Source = new Uri($"/ssh-win-gui;component/Resources/Strings.{language}.xaml", UriKind.Relative),
        };
        if (_languageResources is not null)
        {
            Application.Current.Resources.MergedDictionaries.Remove(_languageResources);
        }
        Application.Current.Resources.MergedDictionaries.Add(resources);
        _languageResources = resources;
        CurrentLanguage = language;

        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = culture;
        if (persist)
        {
            SaveSettings();
        }
    }

    private static StoredSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new StoredSettings();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = document.RootElement;
            var keywordRules = ReadKeywordRules(root);
            return new StoredSettings
            {
                Language = root.TryGetProperty("language", out var language)
                    ? language.GetString() ?? string.Empty
                    : string.Empty,
                MousePasteButton = root.TryGetProperty("mousePasteButton", out var mousePasteButton)
                    ? mousePasteButton.GetString() ?? string.Empty
                    : string.Empty,
                KeywordHighlightingEnabled = root.TryGetProperty("keywordHighlighting", out var keywordHighlighting) &&
                                             keywordHighlighting.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? keywordHighlighting.GetBoolean()
                    : null,
                KeywordHighlightingRules = keywordRules,
            };
        }
        catch (Exception) when (File.Exists(SettingsPath))
        {
            return new StoredSettings();
        }
    }

    private static void SaveSettings()
    {
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(
                new
                {
                    language = CurrentLanguage,
                    mousePasteButton = MousePasteButton == TerminalMousePasteButton.Right ? "right" : "middle",
                    keywordHighlighting = KeywordHighlightingEnabled,
                    keywordGreen = KeywordHighlightingRules.Green,
                    keywordRed = KeywordHighlightingRules.Red,
                    keywordYellow = KeywordHighlightingRules.Yellow,
                },
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }

    private static TerminalKeywordRules? ReadKeywordRules(JsonElement root)
    {
        if (!root.TryGetProperty("keywordGreen", out var greenElement) &&
            !root.TryGetProperty("keywordRed", out _) &&
            !root.TryGetProperty("keywordYellow", out _))
        {
            return null;
        }

        return TerminalKeywordRules.CreateNormalized(
            ReadKeywordArray(root, "keywordGreen"),
            ReadKeywordArray(root, "keywordRed"),
            ReadKeywordArray(root, "keywordYellow"));
    }

    private static IEnumerable<string> ReadKeywordArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Take(TerminalKeywordRules.MaximumKeywordCount)
            .ToArray();
    }

    private sealed class StoredSettings
    {
        public string Language { get; init; } = string.Empty;
        public string MousePasteButton { get; init; } = string.Empty;
        public bool? KeywordHighlightingEnabled { get; init; }
        public TerminalKeywordRules? KeywordHighlightingRules { get; init; }
    }
}
