namespace RsyncShell.Core.Models;

public sealed record RemoteMonitoringSnapshot
{
    public long SampleMonotonicNanoseconds { get; init; }
    public long CpuTotal { get; init; }
    public long CpuIdle { get; init; }
    public long MemoryTotalBytes { get; init; }
    public long MemoryAvailableBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public long DiskAvailableBytes { get; init; }
    public IReadOnlyList<RemoteDiskSnapshot> Disks { get; init; } = [];
    public string? DefaultNetworkInterface { get; init; }
    public IReadOnlyList<RemoteNetworkInterfaceCounter> NetworkInterfaces { get; init; } = [];
    public IReadOnlyList<RemoteGpuSnapshot> Gpus { get; init; } = [];
}

public sealed record RemoteNetworkInterfaceCounter
{
    public required string Name { get; init; }
    public bool IsUp { get; init; }
    public long ReceivedBytes { get; init; }
    public long TransmittedBytes { get; init; }
}

public sealed record RemoteGpuSnapshot
{
    public int Index { get; init; }
    public int CoreUtilizationPercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
}

public sealed record RemoteDiskSnapshot
{
    public required string MountPoint { get; init; }
    public string? Source { get; init; }
    public string? FileSystemType { get; init; }
    public long TotalBytes { get; init; }
    public long AvailableBytes { get; init; }
}
