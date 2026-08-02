using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RsyncShell.Core.Models;

namespace RsyncShell.App.Services;

internal sealed record SettingsTransferPackage
{
    public int SchemaVersion { get; init; } = 1;
    public ConnectionProfile[] Sessions { get; init; } = [];
    public string Language { get; init; } = "en";
    public string MousePasteButton { get; init; } = "middle";
    public bool KeywordHighlightingEnabled { get; init; } = true;
    public string[] KeywordGreen { get; init; } = [];
    public string[] KeywordRed { get; init; } = [];
    public string[] KeywordYellow { get; init; } = [];
}

internal static class SettingsTransferService
{
    public const string ExportFileName = "ssh-win-gui-settings.json";
    private const int MaximumSessionCount = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string GetExportPath(string directory) =>
        Path.Combine(directory, ExportFileName);

    public static async Task ExportAsync(
        string directory,
        IEnumerable<ConnectionProfile> sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var rules = LocalizationService.KeywordHighlightingRules;
        var package = new SettingsTransferPackage
        {
            Sessions = sessions.Select(session => session with { PrivateKeyPath = null }).ToArray(),
            Language = LocalizationService.CurrentLanguage,
            MousePasteButton = LocalizationService.MousePasteButton == TerminalMousePasteButton.Right
                ? "right"
                : "middle",
            KeywordHighlightingEnabled = LocalizationService.KeywordHighlightingEnabled,
            KeywordGreen = rules.Green.ToArray(),
            KeywordRed = rules.Red.ToArray(),
            KeywordYellow = rules.Yellow.ToArray(),
        };

        var path = GetExportPath(directory);
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, package, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static async Task<SettingsTransferPackage> ImportAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = GetExportPath(directory);
        await using var stream = File.OpenRead(path);
        var package = await JsonSerializer.DeserializeAsync<SettingsTransferPackage>(
                          stream,
                          JsonOptions,
                          cancellationToken)
                      .ConfigureAwait(false)
                      ?? throw new InvalidDataException("The settings file is empty.");
        Validate(package);
        var rules = TerminalKeywordRules.CreateNormalized(
            package.KeywordGreen,
            package.KeywordRed,
            package.KeywordYellow);
        return package with
        {
            Language = string.IsNullOrWhiteSpace(package.Language) ? "en" : package.Language,
            MousePasteButton = string.Equals(package.MousePasteButton, "right", StringComparison.OrdinalIgnoreCase)
                ? "right"
                : "middle",
            KeywordGreen = rules.Green.ToArray(),
            KeywordRed = rules.Red.ToArray(),
            KeywordYellow = rules.Yellow.ToArray(),
            Sessions = package.Sessions.Select(session => session with { PrivateKeyPath = null }).ToArray(),
        };
    }

    private static void Validate(SettingsTransferPackage package)
    {
        if (package.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported settings schema version: {package.SchemaVersion}.");
        }
        if (package.Sessions is null || package.KeywordGreen is null || package.KeywordRed is null ||
            package.KeywordYellow is null)
        {
            throw new InvalidDataException("The settings file is missing required data.");
        }
        if (package.Sessions.Length > MaximumSessionCount)
        {
            throw new InvalidDataException($"Too many sessions: {package.Sessions.Length}.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in package.Sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Id) || !ids.Add(session.Id) ||
                string.IsNullOrWhiteSpace(session.Name) || string.IsNullOrWhiteSpace(session.Host) ||
                string.IsNullOrWhiteSpace(session.Username) || session.Port is < 1 or > 65535 ||
                session.ProxyPort is < 1 or > 65535)
            {
                throw new InvalidDataException("The settings file contains an invalid or duplicate SSH session.");
            }
        }
    }
}
