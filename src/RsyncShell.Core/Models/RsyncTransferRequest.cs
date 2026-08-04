namespace RsyncShell.Core.Models;

public enum RsyncTransferDirection
{
    Upload,
    Download,
}

public enum RsyncRemoteTransferExecutionSide
{
    Automatic,
    Source,
    Destination,
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
    public int BandwidthLimitKbps { get; init; }
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}

public sealed record RsyncRemoteTransferRequest
{
    public required ConnectionProfile SourceProfile { get; init; }
    public IReadOnlyList<ConnectionProfile> SourceRoute { get; init; } = [];
    public required SshAuthenticationOptions SourceAuthentication { get; init; }
    public required string SourcePath { get; init; }
    public required ConnectionProfile DestinationProfile { get; init; }
    public IReadOnlyList<ConnectionProfile> DestinationRoute { get; init; } = [];
    public required SshAuthenticationOptions DestinationAuthentication { get; init; }
    public required string DestinationPath { get; init; }
    public RsyncRemoteTransferExecutionSide ExecutionSide { get; init; }
    public bool CopyContents { get; init; }
    public bool PreserveTimes { get; init; } = true;
    public bool PreservePermissions { get; init; } = true;
    public bool PreserveLinks { get; init; } = true;
    public bool Delete { get; init; }
    public bool DryRun { get; init; }
    public bool Compress { get; init; } = true;
    public bool Partial { get; init; }
    public int BandwidthLimitKbps { get; init; }
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    public string? SourceTransferHost { get; init; }
    public int SourceTransferPort { get; init; }
    public string? DestinationTransferHost { get; init; }
    public int DestinationTransferPort { get; init; }
}

public sealed record RemoteNetworkAddressCandidate
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string InterfaceName { get; init; }
    public bool IsSavedEndpoint { get; init; }
}

public sealed record RsyncRemoteRouteProbeRequest
{
    public required ConnectionProfile FirstHopProfile { get; init; }
    public IReadOnlyList<ConnectionProfile> FirstHopRoute { get; init; } = [];
    public required SshAuthenticationOptions FirstHopAuthentication { get; init; }
    public required ConnectionProfile TargetProfile { get; init; }
    public IReadOnlyList<ConnectionProfile> TargetRoute { get; init; } = [];
    public required SshAuthenticationOptions TargetAuthentication { get; init; }
    public required IReadOnlyList<RemoteNetworkAddressCandidate> Candidates { get; init; }
}

public sealed record RsyncRemoteRouteProbeResult
{
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string InterfaceName { get; init; }
    public bool IsSavedEndpoint { get; init; }
    public bool Success { get; init; }
    public long LatencyMilliseconds { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
