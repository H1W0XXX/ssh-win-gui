using System.Text;
using System.Text.Json;
using RsyncShell.Core.Models;
using Tmds.Ssh;

namespace RsyncShell.Core.Services;

public sealed class RemoteMonitoringService : IAsyncDisposable
{
    internal const string Marker = "__SSH_WIN_GUI_MONITOR__";
    private const int OutputByteLimit = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly SshClientSession? _sshNetSession;
    private readonly SshClient? _jumpSession;

    private RemoteMonitoringService(SshClientSession session) => _sshNetSession = session;
    private RemoteMonitoringService(SshClient session) => _jumpSession = session;

    public static async Task<RemoteMonitoringService> ConnectAsync(
        ConnectionProfile profile,
        SshAuthenticationOptions authentication,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        IReadOnlyList<ConnectionProfile>? route = null,
        CancellationToken cancellationToken = default)
    {
        if (profile.ProxyKind == SshProxyKind.JumpHost)
        {
            var client = JumpSshClientFactory.Create(profile, authentication, route ?? [profile], verifyHostKey);
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return new RemoteMonitoringService(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        var session = await new SshClientFactory().ConnectAsync(
            profile, authentication, verifyHostKey, route, cancellationToken).ConfigureAwait(false);
        return new RemoteMonitoringService(session);
    }

    public async Task<RemoteMonitoringSnapshot> SampleAsync(CancellationToken cancellationToken = default)
    {
        var command = "python3 -c " + QuoteForPosixShell(BuildSampleScript());
        var output = _jumpSession is not null
            ? await ExecuteJumpAsync(command, cancellationToken).ConfigureAwait(false)
            : await ExecuteSshNetAsync(command, cancellationToken).ConfigureAwait(false);
        return ParseResponse(output);
    }

    private async Task<string> ExecuteSshNetAsync(string commandText, CancellationToken cancellationToken)
    {
        var client = _sshNetSession?.Client
                     ?? throw new ObjectDisposedException(nameof(RemoteMonitoringService));
        using var command = client.CreateCommand(commandText);
        var limitSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var limitCancellation = new CancellationTokenSource();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, limitCancellation.Token);
        var execution = command.ExecuteAsync(linkedCancellation.Token);
        await RemoteFileService.RejectPreCanceledExecutionAsync(execution).ConfigureAwait(false);
        var stdoutRead = RemoteFileService.ReadBoundedAsync(
            command.OutputStream, OutputByteLimit, "stdout", limitSignal);
        var stderrRead = RemoteFileService.ReadBoundedAsync(
            command.ExtendedOutputStream, 16 * 1024, "stderr", limitSignal);
        var first = await Task.WhenAny(execution, limitSignal.Task, stdoutRead, stderrRead).ConfigureAwait(false);
        if (limitSignal.Task.IsCompleted || first != execution && !execution.IsCompleted)
        {
            limitCancellation.Cancel();
        }

        await execution.ConfigureAwait(false);
        var stdout = await stdoutRead.ConfigureAwait(false);
        var stderr = await stderrRead.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (stdout.LimitExceeded || stderr.LimitExceeded)
        {
            throw new InvalidOperationException("Remote monitoring output exceeded its safety limit.");
        }
        if (stdout.Error is not null || stderr.Error is not null)
        {
            throw new InvalidOperationException(
                "Failed to read remote monitoring output.", stdout.Error ?? stderr.Error);
        }
        if (command.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"Remote monitoring command failed with exit code {command.ExitStatus}: " +
                Encoding.UTF8.GetString(stderr.Content).Trim());
        }
        return Encoding.UTF8.GetString(stdout.Content);
    }

    private async Task<string> ExecuteJumpAsync(string commandText, CancellationToken cancellationToken)
    {
        using var process = await _jumpSession!.ExecuteAsync(commandText, cancellationToken).ConfigureAwait(false);
        process.WriteEof();
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var stdoutBuffer = new byte[16 * 1024];
        var stderrBuffer = new byte[4 * 1024];
        while (true)
        {
            var (isError, read) = await process.ReadAsync(
                stdoutBuffer, stderrBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            var destination = isError ? stderr : stdout;
            var source = isError ? stderrBuffer : stdoutBuffer;
            var limit = isError ? 16 * 1024 : OutputByteLimit;
            if (destination.Length + read > limit)
            {
                throw new InvalidOperationException("Remote monitoring output exceeded its safety limit.");
            }
            destination.Write(source, 0, read);
        }
        var exitCode = await process.GetExitCodeAsync(cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Remote monitoring command failed with exit code {exitCode}: " +
                Encoding.UTF8.GetString(stderr.ToArray()).Trim());
        }
        return Encoding.UTF8.GetString(stdout.ToArray());
    }

    internal static RemoteMonitoringSnapshot ParseResponse(string output)
    {
        var marker = output.LastIndexOf(Marker, StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new InvalidOperationException("Remote monitoring response marker was missing.");
        }
        var snapshot = JsonSerializer.Deserialize<RemoteMonitoringSnapshot>(
                           output[(marker + Marker.Length)..], JsonOptions)
                       ?? throw new InvalidOperationException("Remote monitoring response was empty.");
        if (snapshot.CpuTotal <= 0 || snapshot.CpuIdle < 0 ||
            snapshot.MemoryTotalBytes <= 0 || snapshot.DiskTotalBytes <= 0 ||
            snapshot.NetworkInterfaces.Any(item => string.IsNullOrWhiteSpace(item.Name)) ||
            snapshot.Disks.Any(item => string.IsNullOrWhiteSpace(item.MountPoint) || item.TotalBytes <= 0))
        {
            throw new InvalidOperationException("Remote monitoring response contained invalid values.");
        }
        return snapshot;
    }

    internal static string BuildSampleScript() => $$"""
        import json
        import os
        import shutil
        import subprocess
        import time

        with open('/proc/stat', 'r', encoding='ascii') as stream:
            fields = [int(value) for value in stream.readline().split()[1:]]
        cpu_total = sum(fields[:8])
        cpu_idle = fields[3] + (fields[4] if len(fields) > 4 else 0)

        memory = {}
        with open('/proc/meminfo', 'r', encoding='ascii') as stream:
            for line in stream:
                key, value = line.split(':', 1)
                memory[key] = int(value.strip().split()[0]) * 1024
        memory_total = memory['MemTotal']
        memory_available = memory.get('MemAvailable', memory.get('MemFree', 0))

        default_interface = None
        try:
            with open('/proc/net/route', 'r', encoding='ascii') as stream:
                next(stream, None)
                for line in stream:
                    columns = line.split()
                    if len(columns) >= 4 and columns[1] == '00000000' and int(columns[3], 16) & 2:
                        default_interface = columns[0]
                        break
        except OSError:
            pass

        interfaces = []
        network_root = '/sys/class/net'
        if os.path.isdir(network_root):
            for name in sorted(os.listdir(network_root)):
                base = os.path.join(network_root, name)
                try:
                    with open(os.path.join(base, 'statistics/rx_bytes'), 'r', encoding='ascii') as stream:
                        received = int(stream.read().strip())
                    with open(os.path.join(base, 'statistics/tx_bytes'), 'r', encoding='ascii') as stream:
                        transmitted = int(stream.read().strip())
                    with open(os.path.join(base, 'operstate'), 'r', encoding='ascii') as stream:
                        is_up = stream.read().strip() == 'up'
                    interfaces.append({
                        'name': name,
                        'isUp': is_up,
                        'receivedBytes': received,
                        'transmittedBytes': transmitted,
                    })
                except (OSError, ValueError):
                    pass

        def decode_mount_path(value):
            return value.replace('\\040', ' ').replace('\\011', '\t').replace('\\012', '\n').replace('\\134', '\\')

        disks_by_mount = {}
        accepted_network_types = {'nfs', 'nfs4', 'cifs', 'ceph', 'glusterfs', 'fuse.sshfs'}
        try:
            with open('/proc/self/mountinfo', 'r', encoding='utf-8') as stream:
                for line in stream:
                    before, after = line.rstrip('\n').split(' - ', 1)
                    left = before.split()
                    right = after.split()
                    if len(left) < 5 or len(right) < 2:
                        continue
                    mount_point = decode_mount_path(left[4])
                    file_system_type = right[0]
                    source = decode_mount_path(right[1])
                    is_block_disk = source.startswith('/dev/') and not source.startswith(('/dev/loop', '/dev/ram'))
                    is_disk = mount_point == '/' or is_block_disk or file_system_type in accepted_network_types or file_system_type in {'zfs', 'btrfs', 'overlay'}
                    if not is_disk:
                        continue
                    try:
                        values = os.statvfs(mount_point)
                        total = values.f_frsize * values.f_blocks
                        available = values.f_frsize * values.f_bavail
                        if total > 0:
                            disks_by_mount[mount_point] = {
                                'mountPoint': mount_point,
                                'source': source,
                                'fileSystemType': file_system_type,
                                'totalBytes': total,
                                'availableBytes': available,
                            }
                    except OSError:
                        pass
        except (OSError, ValueError):
            pass

        if '/' not in disks_by_mount:
            values = os.statvfs('/')
            disks_by_mount['/'] = {
                'mountPoint': '/',
                'source': None,
                'fileSystemType': None,
                'totalBytes': values.f_frsize * values.f_blocks,
                'availableBytes': values.f_frsize * values.f_bavail,
            }
        disks = sorted(disks_by_mount.values(), key=lambda item: (item['mountPoint'] != '/', item['mountPoint']))
        disk_total = disks_by_mount['/']['totalBytes']
        disk_available = disks_by_mount['/']['availableBytes']

        gpus = []
        nvidia_smi = shutil.which('nvidia-smi')
        if nvidia_smi:
            try:
                result = subprocess.run([
                    nvidia_smi,
                    '--query-gpu=index,utilization.gpu,memory.used,memory.total',
                    '--format=csv,noheader,nounits',
                ], capture_output=True, text=True, timeout=2, check=False)
                if result.returncode == 0:
                    for line in result.stdout.splitlines():
                        values = [value.strip() for value in line.split(',')]
                        if len(values) == 4:
                            try:
                                gpus.append({
                                    'index': int(values[0]),
                                    'coreUtilizationPercent': int(float(values[1])) if values[1] != 'N/A' else 0,
                                    'memoryUsedBytes': int(float(values[2])) * 1024 * 1024,
                                    'memoryTotalBytes': int(float(values[3])) * 1024 * 1024,
                                })
                            except ValueError:
                                pass
            except (OSError, ValueError, subprocess.TimeoutExpired):
                pass

        print('{{Marker}}' + json.dumps({
            'sampleMonotonicNanoseconds': time.monotonic_ns(),
            'cpuTotal': cpu_total,
            'cpuIdle': cpu_idle,
            'memoryTotalBytes': memory_total,
            'memoryAvailableBytes': memory_available,
            'diskTotalBytes': disk_total,
            'diskAvailableBytes': disk_available,
            'disks': disks,
            'defaultNetworkInterface': default_interface,
            'networkInterfaces': interfaces,
            'gpus': gpus,
        }, separators=(',', ':')))
        """;

    public static double CalculateCpuUtilization(
        RemoteMonitoringSnapshot previous,
        RemoteMonitoringSnapshot current)
    {
        var total = current.CpuTotal - previous.CpuTotal;
        var idle = current.CpuIdle - previous.CpuIdle;
        if (total <= 0)
        {
            return 0;
        }
        return Math.Clamp(100d * (total - Math.Clamp(idle, 0, total)) / total, 0, 100);
    }

    public static (double ReceivedBytesPerSecond, double TransmittedBytesPerSecond) CalculateNetworkRate(
        RemoteMonitoringSnapshot previous,
        RemoteMonitoringSnapshot current,
        string interfaceName)
    {
        var elapsed = (current.SampleMonotonicNanoseconds - previous.SampleMonotonicNanoseconds) / 1_000_000_000d;
        var oldCounter = previous.NetworkInterfaces.FirstOrDefault(item => item.Name == interfaceName);
        var newCounter = current.NetworkInterfaces.FirstOrDefault(item => item.Name == interfaceName);
        if (elapsed <= 0 || oldCounter is null || newCounter is null)
        {
            return (0, 0);
        }
        return (
            Math.Max(0, newCounter.ReceivedBytes - oldCounter.ReceivedBytes) / elapsed,
            Math.Max(0, newCounter.TransmittedBytes - oldCounter.TransmittedBytes) / elapsed);
    }

    private static string QuoteForPosixShell(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public ValueTask DisposeAsync()
    {
        _sshNetSession?.Dispose();
        _jumpSession?.Dispose();
        return ValueTask.CompletedTask;
    }
}
