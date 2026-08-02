using RsyncShell.Core.Models;

namespace RsyncShell.Core.Services;

public static class SshRouteResolver
{
    public const int MaximumJumpDepth = 8;

    public static IReadOnlyList<ConnectionProfile> Resolve(
        ConnectionProfile target,
        IEnumerable<ConnectionProfile> savedProfiles)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(savedProfiles);
        var profiles = savedProfiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var route = new List<ConnectionProfile> { target };
        var seen = new HashSet<string>(StringComparer.Ordinal) { target.Id };
        var current = target;

        while (current.ProxyKind == SshProxyKind.JumpHost)
        {
            if (route.Count > MaximumJumpDepth)
            {
                throw new InvalidOperationException($"SSH jump route exceeds {MaximumJumpDepth} hops.");
            }
            if (string.IsNullOrWhiteSpace(current.JumpProfileId) ||
                !profiles.TryGetValue(current.JumpProfileId, out var jump))
            {
                throw new InvalidOperationException($"Jump session for '{current.Name}' no longer exists.");
            }
            if (!seen.Add(jump.Id))
            {
                throw new InvalidOperationException("SSH jump route contains a cycle.");
            }
            route.Add(jump);
            current = jump;
        }

        return route;
    }

    public static bool WouldCreateCycle(
        string profileId,
        string candidateJumpId,
        IEnumerable<ConnectionProfile> savedProfiles)
    {
        var profiles = savedProfiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { profileId };
        var currentId = candidateJumpId;
        for (var depth = 0; depth <= MaximumJumpDepth; depth++)
        {
            if (!seen.Add(currentId))
            {
                return true;
            }
            if (!profiles.TryGetValue(currentId, out var current) ||
                current.ProxyKind != SshProxyKind.JumpHost ||
                string.IsNullOrWhiteSpace(current.JumpProfileId))
            {
                return false;
            }
            currentId = current.JumpProfileId;
        }
        return true;
    }
}
