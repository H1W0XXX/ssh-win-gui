namespace RsyncShell.Core.Models;

public enum RsyncTransferDirection
{
    Upload,
    Download,
}

public sealed record RsyncTransferRequest
{
    public required RsyncTransferDirection Direction { get; init; }
    public required ConnectionProfile Profile { get; init; }
    public IReadOnlyList<ConnectionProfile> Route { get; init; } = [];
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public bool CopyContents { get; init; }
    public bool PreserveTimes { get; init; } = true;
    public bool PreservePermissions { get; init; } = true;
    public bool PreserveLinks { get; init; } = true;
    public bool Delete { get; init; }
    public bool DryRun { get; init; }
    public bool Compress { get; init; } = true;
    public bool Partial { get; init; }
}
