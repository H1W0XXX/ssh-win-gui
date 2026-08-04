namespace RsyncShell.Core.Models;

public sealed record RemoteNetworkAddress
{
    public required string InterfaceName { get; init; }
    public required string Address { get; init; }
    public int AddressFamily { get; init; }
    public int PrefixLength { get; init; }
}

public sealed record RemoteNetworkInventory
{
    public required string HostName { get; init; }
    public string SshLocalAddress { get; init; } = string.Empty;
    public int SshLocalPort { get; init; } = 22;
    public required IReadOnlyList<RemoteNetworkAddress> Addresses { get; init; }
}
