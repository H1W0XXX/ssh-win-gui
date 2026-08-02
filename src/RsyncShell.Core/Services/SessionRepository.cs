using System.Text.Json;
using RsyncShell.Core.Models;

namespace RsyncShell.Core.Services;

public sealed class SessionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public SessionRepository(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RsyncShell",
            "sessions.json");
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return Array.Empty<ConnectionProfile>();
        }

        await using var stream = File.OpenRead(FilePath);
        return await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public async Task SaveAsync(IEnumerable<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException("Session file has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = FilePath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, profiles.ToArray(), JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, FilePath, true);
    }
}

