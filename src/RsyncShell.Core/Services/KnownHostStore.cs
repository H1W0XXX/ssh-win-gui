using System.Text.Json;
using RsyncShell.Core.Models;

namespace RsyncShell.Core.Services;

public enum KnownHostStatus
{
    Unknown,
    AdditionalAlgorithm,
    Trusted,
    Changed,
}

public sealed record KnownHostEntry
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Algorithm { get; init; }
    public required string FingerprintSha256 { get; init; }
    public DateTimeOffset TrustedAtUtc { get; init; }
}

public sealed class KnownHostStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private List<KnownHostEntry>? _entries;

    public KnownHostStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RsyncShell",
            "known_hosts.json");
    }

    public string FilePath { get; }

    public KnownHostStatus Check(SshHostKeyInfo key, out KnownHostEntry? existing)
    {
        lock (_gate)
        {
            existing = LoadLocked().FirstOrDefault(entry =>
                string.Equals(entry.Host, key.Host, StringComparison.OrdinalIgnoreCase) &&
                entry.Port == key.Port &&
                string.Equals(entry.Algorithm, key.Algorithm, StringComparison.Ordinal));
            if (existing is null)
            {
                return LoadLocked().Any(entry =>
                    string.Equals(entry.Host, key.Host, StringComparison.OrdinalIgnoreCase) &&
                    entry.Port == key.Port)
                    ? KnownHostStatus.AdditionalAlgorithm
                    : KnownHostStatus.Unknown;
            }

            return string.Equals(
                existing.FingerprintSha256,
                key.FingerprintSha256,
                StringComparison.Ordinal)
                ? KnownHostStatus.Trusted
                : KnownHostStatus.Changed;
        }
    }

    public void Trust(SshHostKeyInfo key)
    {
        lock (_gate)
        {
            var entries = LoadLocked();
            entries.RemoveAll(entry =>
                string.Equals(entry.Host, key.Host, StringComparison.OrdinalIgnoreCase) &&
                entry.Port == key.Port &&
                string.Equals(entry.Algorithm, key.Algorithm, StringComparison.Ordinal));
            entries.Add(new KnownHostEntry
            {
                Host = key.Host,
                Port = key.Port,
                Algorithm = key.Algorithm,
                FingerprintSha256 = key.FingerprintSha256,
                TrustedAtUtc = DateTimeOffset.UtcNow,
            });
            SaveLocked(entries);
        }
    }

    public KnownHostEntry? FindTrusted(string host, int port)
    {
        lock (_gate)
        {
            return LoadLocked()
                .Where(entry =>
                    string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase) &&
                    entry.Port == port)
                .OrderByDescending(entry => entry.TrustedAtUtc)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<KnownHostEntry> FindTrustedAll(string host, int port)
    {
        lock (_gate)
        {
            return LoadLocked()
                .Where(entry =>
                    string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase) &&
                    entry.Port == port)
                .OrderBy(entry => entry.Algorithm, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private List<KnownHostEntry> LoadLocked()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        if (!File.Exists(FilePath))
        {
            return _entries = [];
        }

        try
        {
            _entries = JsonSerializer.Deserialize<List<KnownHostEntry>>(
                           File.ReadAllText(FilePath),
                           JsonOptions) ?? [];
            return _entries;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"The known-host file is invalid and was not ignored: {FilePath}",
                ex);
        }
    }

    private void SaveLocked(IReadOnlyCollection<KnownHostEntry> entries)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("Known-host file has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temporaryPath, FilePath, true);
    }
}
