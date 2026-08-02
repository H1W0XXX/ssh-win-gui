using System.Globalization;
using System.Text.Json.Serialization;

namespace RsyncShell.Core.Models;

public enum SshProxyKind
{
    None,
    Socks5,
    JumpHost,
}

public sealed record ConnectionProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public string Group { get; init; } = "Sessions";
    public string? PrivateKeyPath { get; init; }
    public bool Favorite { get; init; }
    public SshProxyKind ProxyKind { get; init; }
    public string? ProxyHost { get; init; }
    public int ProxyPort { get; init; } = 1080;
    public string? JumpProfileId { get; init; }

    [JsonIgnore]
    public string DisplayEndpoint => $"{Username}@{Host}:{Port}";

    public static bool TryParseQuickConnect(
        string value,
        string defaultUsername,
        out ConnectionProfile? profile,
        out string error)
    {
        profile = null;
        error = string.Empty;

        var input = value.Trim();
        if (input.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "Invalid ssh:// address.";
                return false;
            }

            var userInfo = Uri.UnescapeDataString(uri.UserInfo);
            if (userInfo.Contains(':'))
            {
                error = "Inline SSH passwords are not accepted. Enter the password in the terminal prompt.";
                return false;
            }
            var username = string.IsNullOrWhiteSpace(userInfo) ? defaultUsername : userInfo;
            return Create(uri.Host, uri.IsDefaultPort ? 22 : uri.Port, username, out profile, out error);
        }

        if (input.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase))
        {
            input = input[4..].Trim();
        }

        if (input.Contains(' ') || input.Contains('\t'))
        {
            error = "Use user@host:port or ssh://user@host:port.";
            return false;
        }

        var usernamePart = defaultUsername;
        var hostPart = input;
        var at = input.LastIndexOf('@');
        if (at >= 0)
        {
            usernamePart = input[..at];
            hostPart = input[(at + 1)..];
        }

        var port = 22;
        string host;
        if (hostPart.StartsWith('['))
        {
            var closing = hostPart.IndexOf(']');
            if (closing < 1)
            {
                error = "Invalid bracketed IPv6 address.";
                return false;
            }

            host = hostPart[1..closing];
            var remainder = hostPart[(closing + 1)..];
            if (remainder.Length > 0)
            {
                if (!remainder.StartsWith(':') || !TryParsePort(remainder[1..], out port))
                {
                    error = "Invalid SSH port.";
                    return false;
                }
            }
        }
        else
        {
            host = hostPart;
            var firstColon = hostPart.IndexOf(':');
            var lastColon = hostPart.LastIndexOf(':');
            if (firstColon > 0 && firstColon == lastColon)
            {
                host = hostPart[..lastColon];
                if (!TryParsePort(hostPart[(lastColon + 1)..], out port))
                {
                    error = "Invalid SSH port.";
                    return false;
                }
            }
        }

        return Create(host, port, usernamePart, out profile, out error);
    }

    private static bool Create(
        string host,
        int port,
        string username,
        out ConnectionProfile? profile,
        out string error)
    {
        profile = null;
        error = string.Empty;
        host = host.Trim();
        username = username.Trim();

        if (host.Length == 0)
        {
            error = "Host is required.";
            return false;
        }

        if (username.Length == 0)
        {
            error = "Username is required.";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = "SSH port must be between 1 and 65535.";
            return false;
        }

        profile = new ConnectionProfile
        {
            Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            Name = host,
            Host = host,
            Port = port,
            Username = username,
        };
        return true;
    }

    private static bool TryParsePort(string value, out int port) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
        port is >= 1 and <= 65535;
}
