namespace RsyncShell.Core.Models;

public sealed record RemoteFileEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsSymbolicLink { get; init; }
    public bool IsParent { get; init; }
    public long Size { get; init; }
    public long ModifiedUnix { get; init; }
    public string Mode { get; init; } = string.Empty;

    public DateTimeOffset Modified => DateTimeOffset.FromUnixTimeSeconds(ModifiedUnix);
    public string Kind => IsDirectory ? "Folder" : IsSymbolicLink ? "Link" : "File";
    public string DisplaySize => IsDirectory ? string.Empty : FormatSize(Size);

    private static string FormatSize(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var size = (double)Math.Max(0, value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{size:0.#} {units[unit]}";
    }
}

public sealed record RemoteDirectoryListing
{
    public required string Path { get; init; }
    public required IReadOnlyList<RemoteFileEntry> Entries { get; init; }
    public bool IsTruncated { get; init; }
    public int EntryLimit { get; init; }
}
