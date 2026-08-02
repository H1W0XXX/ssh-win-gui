using System.Globalization;

namespace RsyncShell.Core.Models;

public enum SshTunnelKind
{
    LocalForward,
    RemoteForward,
    LocalSocks5,
    RemoteSocks5,
}

public sealed record TunnelEndpoint(string Host, int Port)
{
    public override string ToString() => Host.Contains(':', StringComparison.Ordinal)
        ? $"[{Host}]:{Port.ToString(CultureInfo.InvariantCulture)}"
        : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out TunnelEndpoint? endpoint, out string error)
    {
        endpoint = null;
        error = string.Empty;
        var input = value?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            error = "Endpoint is required.";
            return false;
        }

        var host = "127.0.0.1";
        var portText = input;
        if (input.StartsWith('['))
        {
            var closing = input.IndexOf(']');
            if (closing < 2 || closing + 2 > input.Length || input[closing + 1] != ':')
            {
                error = "Use [IPv6]:port for an IPv6 endpoint.";
                return false;
            }
            host = input[1..closing].Trim();
            portText = input[(closing + 2)..];
        }
        else
        {
            var firstColon = input.IndexOf(':');
            var lastColon = input.LastIndexOf(':');
            if (firstColon >= 0)
            {
                if (firstColon != lastColon)
                {
                    error = "Use [IPv6]:port for an IPv6 endpoint.";
                    return false;
                }
                host = input[..firstColon].Trim();
                portText = input[(firstColon + 1)..];
            }
        }

        if (host.Length == 0)
        {
            error = "Endpoint host is required.";
            return false;
        }
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            error = "Endpoint port must be between 1 and 65535.";
            return false;
        }

        endpoint = new TunnelEndpoint(host, port);
        return true;
    }
}

public sealed record SshTunnelDefinition(
    string Id,
    ConnectionProfile Profile,
    SshTunnelKind Kind,
    TunnelEndpoint Listen,
    TunnelEndpoint? Target);
