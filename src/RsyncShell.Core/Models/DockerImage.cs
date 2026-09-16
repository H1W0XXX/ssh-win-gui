namespace RsyncShell.Core.Models;

public sealed record DockerImage
{
    public string Repository { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Size { get; init; } = string.Empty;
    public bool IsKubernetes { get; init; }
    public string Reference => Repository + ":" + Tag;
}

public sealed record DockerImageSelection(string Reference, string SourceId, string DestinationId);

public sealed record DockerTransferOptions(IReadOnlyList<DockerImageSelection> Images, bool Zstd);
