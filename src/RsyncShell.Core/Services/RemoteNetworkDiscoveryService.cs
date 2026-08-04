using System.Text;
using System.Text.Json;
using RsyncShell.Core.Models;

namespace RsyncShell.Core.Services;

public sealed class RemoteNetworkDiscoveryService
{
    internal const string Marker = "__SSH_WIN_GUI_NETWORK__";
    internal const int OutputByteLimit = 256 * 1024;
    private const int ErrorByteLimit = 16 * 1024;
    private readonly SshClientFactory _clientFactory = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<RemoteNetworkInventory> DiscoverAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile> route,
        CancellationToken cancellationToken = default)
    {
        if (authentication.Kind != SshAuthenticationKind.PrivateKey)
        {
            throw new InvalidOperationException("Machine-to-machine discovery requires private-key authentication.");
        }
        var command = "python3 -c " + QuoteForPosixShell(BuildInventoryScript());
        var output = profile.ProxyKind == SshProxyKind.JumpHost
            ? await ExecuteThroughJumpAsync(profile, authentication, verifyHostKey, route, command, cancellationToken)
                .ConfigureAwait(false)
            : await ExecuteDirectAsync(profile, authentication, verifyHostKey, route, command, cancellationToken)
                .ConfigureAwait(false);
        return ParseResponse(output);
    }

    internal static string BuildInventoryScript() => $$"""
        import ipaddress
        import json
        import os
        import socket
        import subprocess

        excluded_prefixes = (
            'docker', 'br-', 'veth', 'cni', 'flannel', 'cali', 'cilium',
            'kube', 'weave', 'virbr', 'podman', 'lxc', 'tunl', 'genev', 'vxlan.calico'
        )

        def excluded_interface(name):
            lowered = (name or '').lower()
            return lowered == 'lo' or lowered.startswith(excluded_prefixes)

        private_v4 = (
            ipaddress.ip_network('10.0.0.0/8'),
            ipaddress.ip_network('172.16.0.0/12'),
            ipaddress.ip_network('192.168.0.0/16'),
        )
        private_v6 = ipaddress.ip_network('fc00::/7')

        def is_internal(value):
            try:
                address = ipaddress.ip_address(value.split('%', 1)[0])
            except ValueError:
                return False
            if address.is_loopback or address.is_link_local or address.is_multicast or address.is_unspecified:
                return False
            if address.version == 4:
                return any(address in network for network in private_v4)
            return address in private_v6

        addresses = []
        try:
            completed = subprocess.run(
                ['ip', '-j', 'address', 'show'],
                check=True,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=5,
            )
            for interface in json.loads(completed.stdout):
                name = interface.get('ifname', '')
                if excluded_interface(name) or interface.get('operstate') == 'DOWN':
                    continue
                for item in interface.get('addr_info') or []:
                    value = item.get('local', '')
                    if not is_internal(value):
                        continue
                    addresses.append({
                        'interfaceName': name,
                        'address': value,
                        'addressFamily': 6 if item.get('family') == 'inet6' else 4,
                        'prefixLength': int(item.get('prefixlen') or 0),
                    })
        except Exception:
            seen = set()
            for item in socket.getaddrinfo(socket.gethostname(), None):
                value = item[4][0]
                if value in seen or not is_internal(value):
                    continue
                seen.add(value)
                addresses.append({
                    'interfaceName': 'unknown',
                    'address': value,
                    'addressFamily': 6 if ':' in value else 4,
                    'prefixLength': 0,
                })

        ssh_connection = os.environ.get('SSH_CONNECTION', '').split()
        ssh_local_address = ssh_connection[2] if len(ssh_connection) >= 4 else ''
        try:
            ssh_local_port = int(ssh_connection[3]) if len(ssh_connection) >= 4 else 22
        except ValueError:
            ssh_local_port = 22

        deduplicated = []
        seen = set()
        for item in addresses:
            key = (item['interfaceName'], item['address'])
            if key not in seen:
                seen.add(key)
                deduplicated.append(item)

        print('{{Marker}}' + json.dumps({
            'hostName': socket.gethostname(),
            'sshLocalAddress': ssh_local_address,
            'sshLocalPort': ssh_local_port,
            'addresses': deduplicated,
        }, ensure_ascii=False, separators=(',', ':')))
        """;

    internal static RemoteNetworkInventory ParseResponse(string stdout)
    {
        var markerIndex = stdout.LastIndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException("Remote network discovery response did not contain its JSON marker.");
        }
        var inventory = JsonSerializer.Deserialize<RemoteNetworkInventory>(
                            stdout.AsSpan(markerIndex + Marker.Length),
                            JsonOptions)
                        ?? throw new InvalidOperationException("Remote network discovery response was empty.");
        if (inventory.Addresses.Count > 128)
        {
            throw new InvalidOperationException("Remote network discovery returned too many addresses.");
        }
        if (inventory.SshLocalPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Remote network discovery returned an invalid SSH port.");
        }
        foreach (var address in inventory.Addresses)
        {
            if (string.IsNullOrWhiteSpace(address.InterfaceName) || string.IsNullOrWhiteSpace(address.Address))
            {
                throw new InvalidOperationException("Remote network discovery returned an incomplete address.");
            }
        }
        return inventory;
    }

    private async Task<string> ExecuteDirectAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile> route,
        string commandText,
        CancellationToken cancellationToken)
    {
        using var session = await _clientFactory.ConnectAsync(
            profile, authentication, verifyHostKey, route, cancellationToken).ConfigureAwait(false);
        using var command = session.Client.CreateCommand(commandText);
        using var outputLimitCancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, outputLimitCancellation.Token);
        var limitSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = command.ExecuteAsync(linkedCancellation.Token);
        await RemoteFileService.RejectPreCanceledExecutionAsync(execution).ConfigureAwait(false);
        var stdoutRead = RemoteFileService.ReadBoundedAsync(
            command.OutputStream, OutputByteLimit, "stdout", limitSignal);
        var stderrRead = RemoteFileService.ReadBoundedAsync(
            command.ExtendedOutputStream, ErrorByteLimit, "stderr", limitSignal);
        var first = await Task.WhenAny(execution, limitSignal.Task, stdoutRead, stderrRead).ConfigureAwait(false);
        if (limitSignal.Task.IsCompleted || first != execution && !execution.IsCompleted)
        {
            outputLimitCancellation.Cancel();
        }
        await execution.ConfigureAwait(false);
        var stdout = await stdoutRead.ConfigureAwait(false);
        var stderr = await stderrRead.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stdout.LimitExceeded || stderr.LimitExceeded)
        {
            throw new InvalidOperationException("Remote network discovery output exceeded its safety limit.");
        }
        if (stdout.Error is not null || stderr.Error is not null)
        {
            throw new InvalidOperationException(
                "Failed to read remote network discovery output.", stdout.Error ?? stderr.Error);
        }
        if (command.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"Remote network discovery failed with exit code {command.ExitStatus}: " +
                Encoding.UTF8.GetString(stderr.Content).Trim());
        }
        return Encoding.UTF8.GetString(stdout.Content);
    }

    private static async Task<string> ExecuteThroughJumpAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile> route,
        string commandText,
        CancellationToken cancellationToken)
    {
        using var client = JumpSshClientFactory.Create(profile, authentication, route, verifyHostKey);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        using var process = await client.ExecuteAsync(commandText, cancellationToken).ConfigureAwait(false);
        process.WriteEof();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var stdoutBuffer = new byte[16 * 1024];
        var stderrBuffer = new byte[4 * 1024];
        while (true)
        {
            var (isError, read) = await process.ReadAsync(
                stdoutBuffer, stderrBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var destination = isError ? stderr : stdout;
            var source = isError ? stderrBuffer : stdoutBuffer;
            var limit = isError ? ErrorByteLimit : OutputByteLimit;
            if (destination.Length + read > limit)
            {
                throw new InvalidOperationException("Remote network discovery output exceeded its safety limit.");
            }
            destination.Write(source, 0, read);
        }
        var exitCode = await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Remote network discovery failed with exit code {exitCode}: " +
                Encoding.UTF8.GetString(stderr.ToArray()).Trim());
        }
        return Encoding.UTF8.GetString(stdout.ToArray());
    }

    private static string QuoteForPosixShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
