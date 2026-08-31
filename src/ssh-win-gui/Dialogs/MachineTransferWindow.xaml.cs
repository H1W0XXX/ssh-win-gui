using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;
using Path = System.IO.Path;

namespace RsyncShell.App.Dialogs;

public partial class MachineTransferWindow : Window
{
    private const int LocalDirectoryEntryLimit = 2_000;
    private const int JobLogLineLimit = 1_000;
    private const int DiagnosticLogLineLimit = 2_000;
    private readonly IReadOnlyList<ConnectionProfile> _profiles;
    private readonly string? _workerPath;
    private readonly Func<SshHostKeyInfo, bool> _verifyHostKey;
    private readonly Func<ConnectionProfile, SshAuthenticationOptions?> _activeAuthentication;
    private readonly RemoteFileService _remoteFiles = new();
    private readonly RemoteNetworkDiscoveryService _networkDiscovery = new();
    private readonly Dictionary<string, SshAuthenticationOptions> _authenticationCache = new(StringComparer.Ordinal);
    private readonly ObservableCollection<MachineTransferJob> _jobs = [];
    private readonly ObservableCollection<RouteProbeChoice> _routeChoices = [];
    private readonly Queue<string> _diagnosticLog = new();
    private readonly object _diagnosticLogGate = new();
    private readonly EndpointState _endpointA;
    private readonly EndpointState _endpointB;
    private readonly IReadOnlyList<EndpointChoice> _choices;
    private bool _ready;
    private int _nextJobNumber = 1;
    private CancellationTokenSource? _routeProbeCancellation;
    private RouteProbeChoice? _selectedRoute;

    public MachineTransferWindow(
        IReadOnlyList<ConnectionProfile> profiles,
        string? workerPath,
        Func<SshHostKeyInfo, bool> verifyHostKey,
        Func<ConnectionProfile, SshAuthenticationOptions?> activeAuthentication)
    {
        InitializeComponent();
        _profiles = profiles.ToArray();
        _workerPath = workerPath;
        _verifyHostKey = verifyHostKey;
        _activeAuthentication = activeAuthentication;
        _endpointA = new EndpointState(
            "A", AHostComboBox, APathTextBox, AFileList, AStatusText, AProgress, AEndpointText);
        _endpointB = new EndpointState(
            "B", BHostComboBox, BPathTextBox, BFileList, BStatusText, BProgress, BEndpointText);
        _choices = _profiles
            .OrderBy(profile => profile.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(EndpointChoice.Remote)
            .ToArray();

        AHostComboBox.ItemsSource = _choices;
        BHostComboBox.ItemsSource = _choices;
        JobsList.ItemsSource = _jobs;
        RouteResultsList.ItemsSource = _routeChoices;
        AHostComboBox.SelectedIndex = -1;
        BHostComboBox.SelectedIndex = -1;
        _ready = true;
        UpdateTransferMode();
    }

    private EndpointState EndpointFromSender(object sender)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        return string.Equals(tag, "B", StringComparison.Ordinal) ? _endpointB : _endpointA;
    }

    private async void EndpointHost_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }
        var endpoint = EndpointFromSender(sender);
        endpoint.Choice = endpoint.HostComboBox.SelectedItem as EndpointChoice;
        InvalidateRouteChoices();
        endpoint.PathTextBox.Text = DefaultPath(endpoint.Choice);
        endpoint.EndpointText.Text = endpoint.Choice?.EndpointLabel ?? string.Empty;
        UpdateTransferMode();
        if (endpoint.Choice is null)
        {
            endpoint.LoadCancellation?.Cancel();
            endpoint.Items.Clear();
            endpoint.FileList.ItemsSource = endpoint.Items;
            endpoint.StatusText.Text = string.Empty;
            endpoint.Progress.Visibility = Visibility.Collapsed;
            return;
        }
        await RefreshEndpointAsync(endpoint);
    }

    private async void EndpointRefresh_OnClick(object sender, RoutedEventArgs e) =>
        await RefreshEndpointAsync(EndpointFromSender(sender));

    private async void EndpointParent_OnClick(object sender, RoutedEventArgs e)
    {
        var endpoint = EndpointFromSender(sender);
        endpoint.PathTextBox.Text = ParentPath(endpoint.Choice, endpoint.PathTextBox.Text);
        await RefreshEndpointAsync(endpoint);
    }

    private async void EndpointPath_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        e.Handled = true;
        await RefreshEndpointAsync(EndpointFromSender(sender));
    }

    private async void EndpointFileList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var endpoint = EndpointFromSender(sender);
        if (endpoint.FileList.SelectedItem is not MachineTransferBrowserItem { IsDirectory: true } item)
        {
            return;
        }
        endpoint.PathTextBox.Text = item.FullPath;
        await RefreshEndpointAsync(endpoint);
    }

    private async Task RefreshEndpointAsync(EndpointState endpoint)
    {
        endpoint.Choice = endpoint.HostComboBox.SelectedItem as EndpointChoice;
        if (endpoint.Choice is null)
        {
            return;
        }

        endpoint.LoadCancellation?.Cancel();
        endpoint.LoadCancellation?.Dispose();
        endpoint.LoadCancellation = new CancellationTokenSource();
        var token = endpoint.LoadCancellation.Token;
        var generation = ++endpoint.LoadGeneration;
        endpoint.Progress.Visibility = Visibility.Visible;
        endpoint.StatusText.Text = LocalizationService.Get("LoadingDirectory");
        endpoint.StatusText.ToolTip = null;

        try
        {
            IReadOnlyList<MachineTransferBrowserItem> items;
            var requestedPath = endpoint.PathTextBox.Text.Trim();
            if (endpoint.Choice.IsLocal)
            {
                items = await Task.Run(() => ListLocalDirectory(requestedPath, token), token);
                requestedPath = Path.GetFullPath(requestedPath);
            }
            else
            {
                var profile = endpoint.Choice.Profile!;
                var authentication = ResolveAuthentication(profile);
                if (authentication is null)
                {
                    endpoint.StatusText.Text = LocalizationService.Get("AuthenticationCancelled");
                    return;
                }
                var route = SshRouteResolver.Resolve(profile, _profiles);
                var listing = await _remoteFiles.ListAsync(
                    profile,
                    authentication,
                    _verifyHostKey,
                    requestedPath,
                    route,
                    token);
                requestedPath = listing.Path;
                items = BuildRemoteItems(listing);
                if (listing.IsTruncated)
                {
                    endpoint.StatusText.Text = LocalizationService.Format("DirectoryTruncated", listing.EntryLimit);
                }
            }

            if (token.IsCancellationRequested || generation != endpoint.LoadGeneration)
            {
                return;
            }
            endpoint.PathTextBox.Text = requestedPath;
            endpoint.Items.Clear();
            foreach (var item in items)
            {
                endpoint.Items.Add(item);
            }
            endpoint.FileList.ItemsSource = endpoint.Items;
            if (!endpoint.StatusText.Text.StartsWith(LocalizationService.Get("DirectoryTruncatedPrefix"), StringComparison.Ordinal))
            {
                endpoint.StatusText.Text = LocalizationService.Format("DirectoryItemCount", items.Count(item => !item.IsParent));
            }
        }
        catch (OperationCanceledException)
        {
            // A newer refresh or window close owns the endpoint now.
        }
        catch (Exception ex)
        {
            var message = LocalizationService.Format("DirectoryLoadFailed", ex.Message);
            endpoint.StatusText.Text = message;
            endpoint.StatusText.ToolTip = message;
            AppendDiagnostic($"directory:{endpoint.Name}", ex.ToString());
        }
        finally
        {
            if (generation == endpoint.LoadGeneration)
            {
                endpoint.Progress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private SshAuthenticationOptions? ResolveAuthentication(ConnectionProfile profile)
    {
        var active = _activeAuthentication(profile);
        if (active is { Kind: SshAuthenticationKind.PrivateKey })
        {
            _authenticationCache[profile.Id] = active;
            return active;
        }
        if (_authenticationCache.TryGetValue(profile.Id, out var cached) &&
            cached.Kind == SshAuthenticationKind.PrivateKey)
        {
            return cached;
        }
        var expandedKeyPath = string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
            ? null
            : Environment.ExpandEnvironmentVariables(profile.PrivateKeyPath);
        if (!string.IsNullOrWhiteSpace(expandedKeyPath) && File.Exists(expandedKeyPath))
        {
            var savedKey = new SshAuthenticationOptions
            {
                Kind = SshAuthenticationKind.PrivateKey,
                PrivateKeyPath = expandedKeyPath,
            };
            _authenticationCache[profile.Id] = savedKey;
            return savedKey;
        }

        ShowError(LocalizationService.Format("MachineTransferPrivateKeyRequired", profile.Name));
        return null;
    }

    private void Direction_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            InvalidateRouteChoices();
            UpdateTransferMode();
        }
    }

    private void UpdateTransferMode()
    {
        var bothRemote = _endpointA.Choice is { IsLocal: false } && _endpointB.Choice is { IsLocal: false };
        DiscoverRoutesButton.IsEnabled = bothRemote && _workerPath is not null;
        DeleteCheckBox.IsEnabled = bothRemote;
        DryRunCheckBox.IsEnabled = bothRemote;
        if (!bothRemote)
        {
            DeleteCheckBox.IsChecked = false;
            DryRunCheckBox.IsChecked = false;
        }
    }

    private void InvalidateRouteChoices()
    {
        _routeProbeCancellation?.Cancel();
        _selectedRoute = null;
        _routeChoices.Clear();
        if (IsInitialized)
        {
            RouteProbeStatusText.Text = LocalizationService.Get("RouteProbeNotRun");
        }
    }

    private async void DiscoverRoutes_OnClick(object sender, RoutedEventArgs e)
    {
        RouteProbeTab.IsSelected = true;
        var source = AToBRadio.IsChecked == true ? _endpointA : _endpointB;
        var destination = AToBRadio.IsChecked == true ? _endpointB : _endpointA;
        source.Choice = source.HostComboBox.SelectedItem as EndpointChoice;
        destination.Choice = destination.HostComboBox.SelectedItem as EndpointChoice;
        if (source.Choice is not { IsLocal: false, Profile: not null } ||
            destination.Choice is not { IsLocal: false, Profile: not null })
        {
            ShowError(LocalizationService.Get("RouteProbeRemoteOnly"));
            return;
        }
        if (_workerPath is null)
        {
            ShowError(LocalizationService.Get("WorkerMissing"));
            return;
        }
        var sourceAuthentication = ResolveAuthentication(source.Choice.Profile);
        var destinationAuthentication = ResolveAuthentication(destination.Choice.Profile);
        if (sourceAuthentication is null || destinationAuthentication is null)
        {
            return;
        }

        _routeProbeCancellation?.Cancel();
        _routeProbeCancellation?.Dispose();
        _routeProbeCancellation = new CancellationTokenSource();
        var token = _routeProbeCancellation.Token;
        _selectedRoute = null;
        _routeChoices.Clear();
        RouteResultsList.SelectedItem = null;
        RouteProbeProgress.Visibility = Visibility.Visible;
        RouteProbeStatusText.Text = LocalizationService.Get("DiscoveringNetworkAddresses");
        RouteProbeStatusText.ToolTip = null;
        DiscoverRoutesButton.IsEnabled = false;
        AppendDiagnostic(
            "route",
            $"Starting bidirectional route discovery: {source.Choice.Profile.Name} <-> {destination.Choice.Profile.Name}");

        try
        {
            var sourceRoute = SshRouteResolver.Resolve(source.Choice.Profile, _profiles);
            var destinationRoute = SshRouteResolver.Resolve(destination.Choice.Profile, _profiles);
            var sourceInventoryTask = _networkDiscovery.DiscoverAsync(
                source.Choice.Profile,
                sourceAuthentication,
                _verifyHostKey,
                sourceRoute,
                token);
            var destinationInventoryTask = _networkDiscovery.DiscoverAsync(
                destination.Choice.Profile,
                destinationAuthentication,
                _verifyHostKey,
                destinationRoute,
                token);
            var sourceInventoryResult = CaptureInventoryAsync(sourceInventoryTask, source.Choice.Profile.Name);
            var destinationInventoryResult = CaptureInventoryAsync(destinationInventoryTask, destination.Choice.Profile.Name);
            await Task.WhenAll(sourceInventoryResult, destinationInventoryResult);
            var sourceInventory = await sourceInventoryResult;
            var destinationInventory = await destinationInventoryResult;
            var sourceCandidates = BuildRouteCandidates(source.Choice.Profile, sourceInventory);
            var destinationCandidates = BuildRouteCandidates(destination.Choice.Profile, destinationInventory);
            if (sourceInventory is not null)
            {
                AppendInventoryDiagnostic(source.Choice.Profile, sourceInventory, sourceCandidates);
            }
            if (destinationInventory is not null)
            {
                AppendInventoryDiagnostic(destination.Choice.Profile, destinationInventory, destinationCandidates);
            }

            RouteProbeStatusText.Text = LocalizationService.Format(
                "ProbingRouteCandidates",
                destinationCandidates.Count + sourceCandidates.Count);
            var sourceProbeService = new RsyncWorkerTransferService(_workerPath);
            var destinationProbeService = new RsyncWorkerTransferService(_workerPath);
            AttachProbeDiagnostics(sourceProbeService, source.Choice.Profile.Name);
            AttachProbeDiagnostics(destinationProbeService, destination.Choice.Profile.Name);
            var sourceProbe = CaptureProbeAsync(
                sourceProbeService.ProbeRemoteRoutesAsync(
                    new RsyncRemoteRouteProbeRequest
                    {
                        FirstHopProfile = source.Choice.Profile,
                        FirstHopRoute = sourceRoute,
                        FirstHopAuthentication = sourceAuthentication,
                        TargetProfile = destination.Choice.Profile,
                        TargetRoute = destinationRoute,
                        TargetAuthentication = destinationAuthentication,
                        Candidates = destinationCandidates,
                    },
                    token),
                source.Choice.Profile.Name,
                destination.Choice.Profile.Name);
            var destinationProbe = CaptureProbeAsync(
                destinationProbeService.ProbeRemoteRoutesAsync(
                    new RsyncRemoteRouteProbeRequest
                    {
                        FirstHopProfile = destination.Choice.Profile,
                        FirstHopRoute = destinationRoute,
                        FirstHopAuthentication = destinationAuthentication,
                        TargetProfile = source.Choice.Profile,
                        TargetRoute = sourceRoute,
                        TargetAuthentication = sourceAuthentication,
                        Candidates = sourceCandidates,
                    },
                    token),
                destination.Choice.Profile.Name,
                source.Choice.Profile.Name);
            await Task.WhenAll(sourceProbe, destinationProbe);
            var sourceProbeResult = await sourceProbe;
            var destinationProbeResult = await destinationProbe;
            var rows = BuildRouteRows(
                    source.Choice.Profile,
                    destination.Choice.Profile,
                    sourceProbeResult.Results,
                    RsyncRemoteTransferExecutionSide.Source)
                .Concat(BuildRouteRows(
                    destination.Choice.Profile,
                    source.Choice.Profile,
                    destinationProbeResult.Results,
                    RsyncRemoteTransferExecutionSide.Destination))
                .OrderByDescending(row => row.Success)
                .ThenBy(row => row.LatencyMilliseconds)
                .ThenBy(row => row.Host, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var row in rows)
            {
                _routeChoices.Add(row);
                AppendDiagnostic(
                    "route-result",
                    $"execute={row.ExecuteOn}; target={row.TargetSession}; interface={row.InterfaceName}; " +
                    $"address={row.Host}:{row.Port}; success={row.Success}; latency_ms={row.LatencyMilliseconds}; " +
                    $"fingerprint={row.Fingerprint}; details={row.Message}");
            }
            var succeeded = rows.Count(row => row.Success);
            var probeErrors = new[] { sourceProbeResult.Error, destinationProbeResult.Error }
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .ToArray();
            RouteProbeStatusText.Text = succeeded > 0
                ? LocalizationService.Format("RouteProbeCompleted", succeeded, rows.Length)
                : rows.Length > 0
                    ? LocalizationService.Format("RouteProbeNoSuccess", rows.Length)
                    : LocalizationService.Format("RouteProbeFailed", string.Join("; ", probeErrors));
            RouteProbeStatusText.ToolTip = probeErrors.Length > 0
                ? string.Join(Environment.NewLine, probeErrors)
                : null;
        }
        catch (OperationCanceledException)
        {
            RouteProbeStatusText.Text = LocalizationService.Get("RouteProbeCancelled");
        }
        catch (Exception ex)
        {
            RouteProbeStatusText.Text = LocalizationService.Format("RouteProbeFailed", ex.Message);
            RouteProbeStatusText.ToolTip = ex.Message;
            AppendDiagnostic("route-error", ex.ToString());
            ShowDiagnosticLog();
        }
        finally
        {
            RouteProbeProgress.Visibility = Visibility.Collapsed;
            UpdateTransferMode();
        }
    }

    internal static IReadOnlyList<RemoteNetworkAddressCandidate> BuildRouteCandidates(
        ConnectionProfile profile,
        RemoteNetworkInventory? inventory)
    {
        var candidates = (inventory?.Addresses ?? []).Select(address => new RemoteNetworkAddressCandidate
            {
                Host = address.Address,
                Port = inventory!.SshLocalPort,
                InterfaceName = address.InterfaceName,
            })
            .Append(new RemoteNetworkAddressCandidate
            {
                Host = profile.Host,
                Port = profile.Port,
                InterfaceName = LocalizationService.Get("SavedDirectEndpoint"),
                IsSavedEndpoint = true,
            });
        if (profile.ProxyKind == SshProxyKind.JumpHost)
        {
            candidates = candidates.Append(new RemoteNetworkAddressCandidate
            {
                Host = profile.Host,
                Port = profile.Port,
                InterfaceName = LocalizationService.Get("SavedJumpRoute"),
                IsSavedEndpoint = true,
                UseTargetProxy = true,
            });
        }
        return candidates
            .GroupBy(
                candidate => $"{candidate.Host}\0{candidate.Port}\0{candidate.UseTargetProxy}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.IsSavedEndpoint).First())
            .OrderByDescending(candidate => candidate.IsSavedEndpoint)
            .ThenByDescending(candidate => candidate.UseTargetProxy)
            .ThenBy(candidate => candidate.InterfaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Host, StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
    }

    private async Task<RemoteNetworkInventory?> CaptureInventoryAsync(
        Task<RemoteNetworkInventory> discovery,
        string profileName)
    {
        try
        {
            return await discovery;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendDiagnostic("inventory-error", $"session={profileName}; details={ex.Message}");
            return null;
        }
    }

    private async Task<RouteProbeAttempt> CaptureProbeAsync(
        Task<IReadOnlyList<RsyncRemoteRouteProbeResult>> probe,
        string firstHopName,
        string targetName)
    {
        try
        {
            return new RouteProbeAttempt(await probe, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"execute={firstHopName}; target={targetName}; details={ex.Message}";
            AppendDiagnostic("route-direction-error", message);
            return new RouteProbeAttempt([], message);
        }
    }

    private static IEnumerable<RouteProbeChoice> BuildRouteRows(
        ConnectionProfile firstHop,
        ConnectionProfile target,
        IReadOnlyList<RsyncRemoteRouteProbeResult> results,
        RsyncRemoteTransferExecutionSide executionSide) =>
        results.Select(result => new RouteProbeChoice(
            firstHop.Id,
            target.Id,
            executionSide,
            firstHop.Name,
            target.Name,
            result.InterfaceName,
            result.Host,
            result.Port,
            result.UseTargetProxy,
            result.Success,
            result.LatencyMilliseconds,
            result.Fingerprint,
            result.Message));

    private void RouteResultsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RouteResultsList.SelectedItem is not RouteProbeChoice selected)
        {
            _selectedRoute = null;
            return;
        }
        if (!selected.Success)
        {
            _selectedRoute = null;
            RouteProbeStatusText.Text = LocalizationService.Get("SelectSuccessfulRoute");
            return;
        }
        _selectedRoute = selected;
        RouteProbeStatusText.Text = LocalizationService.Format(
            "SelectedRoute",
            selected.ExecuteOn,
            selected.Host,
            selected.Port);
    }

    private void CopyProbeResults_OnClick(object sender, RoutedEventArgs e)
    {
        if (_routeChoices.Count == 0)
        {
            return;
        }
        static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
        var lines = new[]
            {
                "execute_on\ttarget\tinterface\thost\tport\tstatus\tlatency_ms\tfingerprint\tdetails",
            }
            .Concat(_routeChoices.Select(route => string.Join('\t',
                SingleLine(route.ExecuteOn),
                SingleLine(route.TargetSession),
                SingleLine(route.InterfaceName),
                SingleLine(route.Host),
                route.Port.ToString(CultureInfo.InvariantCulture),
                SingleLine(route.Status),
                route.LatencyMilliseconds.ToString(CultureInfo.InvariantCulture),
                SingleLine(route.Fingerprint),
                SingleLine(route.Message))));
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    private void ViewDiagnosticLog_OnClick(object sender, RoutedEventArgs e) => ShowDiagnosticLog();

    private void ShowDiagnosticLog()
    {
        string text;
        lock (_diagnosticLogGate)
        {
            text = _diagnosticLog.Count == 0
                ? LocalizationService.Get("NoDiagnosticLogs")
                : string.Join(Environment.NewLine, _diagnosticLog);
        }
        var dialog = new CommandPreviewDialog(
            text,
            LocalizationService.Get("DiagnosticLogTitle"),
            LocalizationService.Get("CopyLog"))
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private void AttachProbeDiagnostics(RsyncWorkerTransferService service, string firstHopName)
    {
        service.EventReceived += (_, workerEvent) =>
        {
            var detail = workerEvent.Message ?? workerEvent.State;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                AppendDiagnostic(
                    $"worker:{firstHopName}",
                    $"type={workerEvent.Type}; level={workerEvent.Level}; phase={workerEvent.Phase}; detail={detail}");
            }
        };
    }

    private void AppendInventoryDiagnostic(
        ConnectionProfile profile,
        RemoteNetworkInventory inventory,
        IReadOnlyList<RemoteNetworkAddressCandidate> candidates)
    {
        var addresses = inventory.Addresses.Count == 0
            ? "(none)"
            : string.Join(", ", inventory.Addresses.Select(address =>
                $"{address.InterfaceName}={address.Address}/{address.PrefixLength}"));
        var routes = string.Join(", ", candidates.Select(candidate =>
            $"{candidate.InterfaceName}={candidate.Host}:{candidate.Port}"));
        AppendDiagnostic(
            "inventory",
            $"session={profile.Name}; host={inventory.HostName}; ssh_local={inventory.SshLocalAddress}:{inventory.SshLocalPort}; " +
            $"addresses={addresses}; candidates={routes}");
    }

    private void AppendDiagnostic(string category, string message)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        lock (_diagnosticLogGate)
        {
            foreach (var line in lines)
            {
                _diagnosticLog.Enqueue($"{timestamp} [{category}] {line}");
            }
            while (_diagnosticLog.Count > DiagnosticLogLineLimit)
            {
                _diagnosticLog.Dequeue();
            }
        }
    }

    private void StartTransfer_OnClick(object sender, RoutedEventArgs e)
    {
        var specification = BuildSpecification(showErrors: true);
        if (specification is null)
        {
            return;
        }
        if (_workerPath is null)
        {
            ShowError(LocalizationService.Get("WorkerMissing"));
            return;
        }
        if (!specification.Source.Choice!.IsLocal && !specification.Destination.Choice!.IsLocal)
        {
            if (_selectedRoute is not { Success: true } route ||
                !string.Equals(route.FirstHopProfileId,
                    route.ExecutionSide == RsyncRemoteTransferExecutionSide.Source
                        ? specification.Source.Choice.Profile!.Id
                        : specification.Destination.Choice.Profile!.Id,
                    StringComparison.Ordinal) ||
                !string.Equals(route.TargetProfileId,
                    route.ExecutionSide == RsyncRemoteTransferExecutionSide.Source
                        ? specification.Destination.Choice.Profile!.Id
                        : specification.Source.Choice.Profile!.Id,
                    StringComparison.Ordinal))
            {
                ShowError(LocalizationService.Get("SelectRouteBeforeTransfer"));
                RouteProbeTab.IsSelected = true;
                return;
            }
        }
        if (!ConfirmOverwrite(specification) || !ConfirmDelete(specification.Options))
        {
            return;
        }

        SshAuthenticationOptions? sourceAuthentication = null;
        SshAuthenticationOptions? destinationAuthentication = null;
        if (!specification.Source.Choice!.IsLocal)
        {
            sourceAuthentication = ResolveAuthentication(specification.Source.Choice.Profile!);
            if (sourceAuthentication is null)
            {
                return;
            }
        }
        if (!specification.Destination.Choice!.IsLocal)
        {
            destinationAuthentication = ResolveAuthentication(specification.Destination.Choice.Profile!);
            if (destinationAuthentication is null)
            {
                return;
            }
        }

        foreach (var source in specification.Sources)
        {
            var job = new MachineTransferJob(
                _nextJobNumber++,
                EndpointPathLabel(specification.Source.Choice!, source.FullPath),
                EndpointPathLabel(specification.Destination.Choice!, specification.DestinationPath),
                JobLogLineLimit);
            _jobs.Insert(0, job);
            JobsList.SelectedItem = job;
            job.Append(LocalizationService.Format("JobQueued", job.Number));
            _ = RunJobAsync(
                job,
                specification,
                source.FullPath,
                sourceAuthentication,
                destinationAuthentication);
        }
        TransferJobsTab.IsSelected = true;
        MessageBox.Show(
            this,
            LocalizationService.Format("TransferJobsStarted", specification.Sources.Count),
            LocalizationService.Get("StartTransfer"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task RunJobAsync(
        MachineTransferJob job,
        TransferSpecification specification,
        string sourcePath,
        SshAuthenticationOptions? sourceAuthentication,
        SshAuthenticationOptions? destinationAuthentication)
    {
        var service = new RsyncWorkerTransferService(_workerPath!);
        service.EventReceived += (_, transferEvent) =>
        {
            if (transferEvent.Type == "progress")
            {
                _ = Dispatcher.InvokeAsync(() => job.UpdateProgress(transferEvent));
                return;
            }
            _ = Dispatcher.InvokeAsync(() =>
            {
                var text = transferEvent.Message ?? transferEvent.State;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    job.Append($"[{transferEvent.Type}] {text}");
                    RefreshSelectedJobLog(job);
                }
            });
        };
        job.Cancellation = new CancellationTokenSource();
        job.Status = LocalizationService.Get("JobRunning");
        try
        {
            if (specification.Source.Choice!.IsLocal)
            {
                var destination = specification.Destination.Choice!.Profile!;
                await service.TransferAsync(
                    BuildLocalRemoteRequest(
                        RsyncTransferDirection.Upload,
                        destination,
                        sourcePath,
                        specification.DestinationPath,
                        specification.Options),
                    destinationAuthentication!,
                    job.Cancellation.Token);
            }
            else if (specification.Destination.Choice!.IsLocal)
            {
                var source = specification.Source.Choice.Profile!;
                await service.TransferAsync(
                    BuildLocalRemoteRequest(
                        RsyncTransferDirection.Download,
                        source,
                        specification.DestinationPath,
                        sourcePath,
                        specification.Options),
                    sourceAuthentication!,
                    job.Cancellation.Token);
            }
            else
            {
                var source = specification.Source.Choice.Profile!;
                var destination = specification.Destination.Choice.Profile!;
                await service.TransferRemoteToRemoteAsync(new RsyncRemoteTransferRequest
                {
                    SourceProfile = source,
                    SourceRoute = SshRouteResolver.Resolve(source, _profiles),
                    SourceAuthentication = sourceAuthentication!,
                    SourcePath = sourcePath,
                    DestinationProfile = destination,
                    DestinationRoute = SshRouteResolver.Resolve(destination, _profiles),
                    DestinationAuthentication = destinationAuthentication!,
                    DestinationPath = specification.DestinationPath,
                    ExecutionSide = specification.ExecutionSide,
                    CopyContents = false,
                    PreserveTimes = specification.Options.Archive,
                    PreservePermissions = specification.Options.Archive,
                    PreserveLinks = specification.Options.Archive,
                    Compress = specification.Options.Compress,
                    Delete = specification.Options.Delete,
                    DryRun = specification.Options.DryRun,
                    BandwidthLimitKbps = specification.Options.BandwidthLimitKbps,
                    ExtraArguments = specification.Options.ExtraArguments,
                    SourceTransferHost = specification.ExecutionSide == RsyncRemoteTransferExecutionSide.Destination &&
                                         _selectedRoute is { UseTargetProxy: false }
                        ? _selectedRoute?.Host
                        : null,
                    SourceTransferPort = specification.ExecutionSide == RsyncRemoteTransferExecutionSide.Destination &&
                                         _selectedRoute is { UseTargetProxy: false }
                        ? _selectedRoute?.Port ?? 0
                        : 0,
                    DestinationTransferHost = specification.ExecutionSide == RsyncRemoteTransferExecutionSide.Source &&
                                              _selectedRoute is { UseTargetProxy: false }
                        ? _selectedRoute?.Host
                        : null,
                    DestinationTransferPort = specification.ExecutionSide == RsyncRemoteTransferExecutionSide.Source &&
                                              _selectedRoute is { UseTargetProxy: false }
                        ? _selectedRoute?.Port ?? 0
                        : 0,
                }, job.Cancellation.Token);
            }
            job.Status = LocalizationService.Get("JobSucceeded");
            job.Append(LocalizationService.Get("LogTransferCompleted"));
        }
        catch (OperationCanceledException)
        {
            job.Status = LocalizationService.Get("JobCancelled");
            job.Append(LocalizationService.Get("LogTransferCancelled"));
        }
        catch (Exception ex)
        {
            job.Status = LocalizationService.Get("JobFailed");
            job.Append(LocalizationService.Format("LogTransferFailed", ex.Message));
        }
        finally
        {
            job.IsRunning = false;
            RefreshSelectedJobLog(job);
        }
    }

    private RsyncTransferRequest BuildLocalRemoteRequest(
        RsyncTransferDirection direction,
        ConnectionProfile profile,
        string localPath,
        string remotePath,
        TransferOptionSnapshot options) =>
        new()
        {
            Direction = direction,
            Profile = profile,
            Route = SshRouteResolver.Resolve(profile, _profiles),
            LocalPath = localPath,
            RemotePath = remotePath,
            CopyContents = false,
            PreserveTimes = options.Archive,
            PreservePermissions = options.Archive,
            PreserveLinks = options.Archive,
            Compress = options.Compress,
            BandwidthLimitKbps = options.BandwidthLimitKbps,
            ExtraArguments = options.ExtraArguments,
        };

    private void PreviewCommand_OnClick(object sender, RoutedEventArgs e)
    {
        var specification = BuildSpecification(showErrors: true);
        if (specification is null)
        {
            return;
        }
        var lines = specification.Sources
            .Select(source => BuildPreview(specification, source.FullPath))
            .ToArray();
        var dialog = new CommandPreviewDialog(string.Join(
            Environment.NewLine + Environment.NewLine,
            lines))
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }

    private TransferSpecification? BuildSpecification(bool showErrors)
    {
        var source = AToBRadio.IsChecked == true ? _endpointA : _endpointB;
        var destination = AToBRadio.IsChecked == true ? _endpointB : _endpointA;
        source.Choice = source.HostComboBox.SelectedItem as EndpointChoice;
        destination.Choice = destination.HostComboBox.SelectedItem as EndpointChoice;
        if (source.Choice is null || destination.Choice is null)
        {
            if (showErrors) ShowError(LocalizationService.Get("SelectBothEndpoints"));
            return null;
        }
        if (source.Choice.IsLocal && destination.Choice.IsLocal)
        {
            if (showErrors) ShowError(LocalizationService.Get("LocalToLocalNotSupported"));
            return null;
        }
        var sourcePath = source.PathTextBox.Text.Trim();
        var destinationPath = destination.PathTextBox.Text.Trim();
        if (sourcePath.Length == 0 || destinationPath.Length == 0)
        {
            if (showErrors) ShowError(LocalizationService.Get("EnterBothPaths"));
            return null;
        }
        if (source.Choice.IsLocal && !Path.IsPathFullyQualified(sourcePath) ||
            destination.Choice.IsLocal && !Path.IsPathFullyQualified(destinationPath) ||
            !source.Choice.IsLocal && !sourcePath.StartsWith("/", StringComparison.Ordinal) ||
            !destination.Choice.IsLocal && !destinationPath.StartsWith("/", StringComparison.Ordinal))
        {
            if (showErrors) ShowError(LocalizationService.Get("AbsolutePathsRequired"));
            return null;
        }
        if (!int.TryParse(BandwidthTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth) || bandwidth < 0)
        {
            if (showErrors) ShowError(LocalizationService.Get("InvalidBandwidthLimit"));
            return null;
        }
        if (!TrySplitArguments(ExtraArgumentsTextBox.Text, out var extraArguments, out var argumentError))
        {
            if (showErrors) ShowError(argumentError);
            return null;
        }

        var selectedItems = source.FileList.SelectedItems
            .Cast<MachineTransferBrowserItem>()
            .Where(item => !item.IsParent)
            .ToArray();
        var sources = selectedItems.Length > 0
            ? selectedItems
            : [new MachineTransferBrowserItem(PathName(source.Choice, sourcePath), sourcePath, true, false, 0, DateTimeOffset.MinValue)];
        var executionSide = _selectedRoute?.ExecutionSide
                            ?? RsyncRemoteTransferExecutionSide.Automatic;
        return new TransferSpecification(
            source,
            destination,
            sources,
            destinationPath,
            executionSide,
            new TransferOptionSnapshot(
                ArchiveCheckBox.IsChecked == true,
                CompressCheckBox.IsChecked == true,
                DeleteCheckBox.IsChecked == true,
                DryRunCheckBox.IsChecked == true,
                bandwidth,
                extraArguments));
    }

    private bool ConfirmOverwrite(TransferSpecification specification)
    {
        if (specification.Options.DryRun)
        {
            return true;
        }
        var destinationNames = specification.Destination.Items
            .Where(item => !item.IsParent)
            .Select(item => item.Name);
        var collisions = FindTopLevelCollisions(
            specification.Sources.Select(item => item.Name),
            destinationNames);
        if (collisions.Count == 0)
        {
            return true;
        }
        var message = LocalizationService.Format(
            "ConfirmNamedOverwrite",
            string.Join(Environment.NewLine, collisions));
        return MessageBox.Show(
                   this,
                   message,
                   LocalizationService.Get("ConfirmOverwriteTitle"),
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    internal static IReadOnlyList<string> FindTopLevelCollisions(
        IEnumerable<string> sourceNames,
        IEnumerable<string> destinationNames)
    {
        var destinationSet = destinationNames.ToHashSet(StringComparer.Ordinal);
        return sourceNames
            .Where(destinationSet.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
    }

    private bool ConfirmDelete(TransferOptionSnapshot options) =>
        !options.Delete || MessageBox.Show(
            this,
            LocalizationService.Get("ConfirmRsyncDelete"),
            LocalizationService.Get("DeleteOption"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private string BuildPreview(TransferSpecification specification, string sourcePath)
    {
        var args = new List<string> { "rsync", "-r" };
        if (specification.Options.Archive) args.AddRange(["-t", "-p", "-l"]);
        if (specification.Options.Compress) args.Add("-z");
        if (specification.Options.Delete) args.Add("--delete");
        if (specification.Options.DryRun) args.Add("--dry-run");
        if (specification.Options.BandwidthLimitKbps > 0) args.Add($"--bwlimit={specification.Options.BandwidthLimitKbps}");
        args.AddRange(specification.Options.ExtraArguments);
        string prefix;
        if (specification.Source.Choice!.IsLocal)
        {
            args.Add(sourcePath);
            args.Add(RemoteSpec(specification.Destination.Choice!.Profile!, specification.DestinationPath));
            prefix = LocalizationService.Get("PreviewRunsLocally");
        }
        else if (specification.Destination.Choice!.IsLocal)
        {
            args.Add(RemoteSpec(specification.Source.Choice.Profile!, sourcePath));
            args.Add(specification.DestinationPath);
            prefix = LocalizationService.Get("PreviewRunsLocally");
        }
        else
        {
            var executeOnDestination = specification.ExecutionSide == RsyncRemoteTransferExecutionSide.Destination;
            var inner = executeOnDestination ? specification.Source.Choice.Profile! : specification.Destination.Choice.Profile!;
            var innerHost = _selectedRoute?.Host ?? inner.Host;
            var innerPort = _selectedRoute?.Port ?? inner.Port;
            args.AddRange(["--protect-args", "-e", $"ssh -p {innerPort} (forwarded-key)"]);
            args.Add(executeOnDestination ? RemoteSpec(inner.Username, innerHost, sourcePath) : sourcePath);
            args.Add(executeOnDestination ? specification.DestinationPath : RemoteSpec(inner.Username, innerHost, specification.DestinationPath));
            var executor = executeOnDestination
                ? specification.Destination.Choice.Profile!.Name
                : specification.Source.Choice.Profile!.Name;
            prefix = LocalizationService.Format("PreviewRunsOn", executor);
        }
        return prefix + Environment.NewLine + string.Join(" ", args.Select(QuotePreviewArgument));
    }

    private void JobsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshSelectedJobLog(JobsList.SelectedItem as MachineTransferJob);

    private void RefreshSelectedJobLog(MachineTransferJob? changedJob)
    {
        if (JobsList.SelectedItem is not MachineTransferJob selected ||
            changedJob is not null && !ReferenceEquals(changedJob, selected))
        {
            return;
        }
        JobLogTextBox.Text = string.Join(Environment.NewLine, selected.LogLines);
        JobLogTextBox.CaretIndex = JobLogTextBox.Text.Length;
        JobLogTextBox.ScrollToEnd();
    }

    private void CopyJobLog_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(JobLogTextBox.Text))
        {
            Clipboard.SetText(JobLogTextBox.Text);
        }
    }

    private void CancelSelectedJob_OnClick(object sender, RoutedEventArgs e)
    {
        if (JobsList.SelectedItem is MachineTransferJob { IsRunning: true } job)
        {
            job.Cancellation?.Cancel();
        }
    }

    private void MachineTransferWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        var running = _jobs.Where(job => job.IsRunning).ToArray();
        if (running.Length > 0)
        {
            var result = MessageBox.Show(
                this,
                LocalizationService.Format("CloseCancelsTransfers", running.Length),
                LocalizationService.Get("MachineTransferTitle"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                _ = Dispatcher.BeginInvoke(Hide);
                return;
            }
        }
        _endpointA.LoadCancellation?.Cancel();
        _endpointB.LoadCancellation?.Cancel();
        _routeProbeCancellation?.Cancel();
        foreach (var job in running)
        {
            job.Cancellation?.Cancel();
        }
    }

    private void ShowError(string message) => MessageBox.Show(
        this,
        message,
        LocalizationService.Get("MachineTransferTitle"),
        MessageBoxButton.OK,
        MessageBoxImage.Error);

    internal static bool TrySplitArguments(string text, out IReadOnlyList<string> arguments, out string error)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var escaping = false;
        foreach (var character in text)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }
            if (character == '\\' && quote == '"')
            {
                escaping = true;
                continue;
            }
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }
        if (escaping || quote is not null)
        {
            arguments = [];
            error = LocalizationService.Get("UnclosedArgumentQuote");
            return false;
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        if (result.Count > 32 || result.Any(argument => argument.Length > 512 || argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n')))
        {
            arguments = [];
            error = LocalizationService.Get("InvalidExtraArguments");
            return false;
        }
        arguments = result;
        error = string.Empty;
        return true;
    }

    private static IReadOnlyList<MachineTransferBrowserItem> ListLocalDirectory(string path, CancellationToken token)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {directory.FullName}");
        }
        var items = new List<MachineTransferBrowserItem>();
        if (directory.Parent is not null)
        {
            items.Add(new MachineTransferBrowserItem("..", directory.Parent.FullName, true, true, 0, DateTimeOffset.MinValue));
        }
        foreach (var entry in directory.EnumerateFileSystemInfos()
                     .Take(LocalDirectoryEntryLimit)
                     .OrderBy(entry => entry is not DirectoryInfo)
                     .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var isDirectory = entry is DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.Directory);
                var length = entry is FileInfo file ? file.Length : 0;
                items.Add(new MachineTransferBrowserItem(
                    entry.Name,
                    entry.FullName,
                    isDirectory,
                    false,
                    length,
                    entry.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A racing or inaccessible entry does not make the whole directory unusable.
            }
        }
        return items;
    }

    private static IReadOnlyList<MachineTransferBrowserItem> BuildRemoteItems(RemoteDirectoryListing listing)
    {
        var items = new List<MachineTransferBrowserItem>(listing.Entries.Count + 1);
        var parent = PosixParent(listing.Path);
        if (!string.Equals(parent, listing.Path, StringComparison.Ordinal))
        {
            items.Add(new MachineTransferBrowserItem("..", parent, true, true, 0, DateTimeOffset.MinValue));
        }
        items.AddRange(listing.Entries.Select(entry => new MachineTransferBrowserItem(
            entry.Name,
            entry.Path,
            entry.IsDirectory,
            false,
            entry.Size,
            entry.Modified)));
        return items;
    }

    private static string DefaultPath(EndpointChoice? choice)
    {
        if (choice is { IsLocal: true })
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (choice?.Profile is not { } profile)
        {
            return string.Empty;
        }
        return string.Equals(profile.Username, "root", StringComparison.OrdinalIgnoreCase)
            ? "/root"
            : "/home/" + profile.Username;
    }

    private static string ParentPath(EndpointChoice? choice, string path)
    {
        if (choice is { IsLocal: true })
        {
            try
            {
                return Directory.GetParent(Path.GetFullPath(path))?.FullName ?? Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
        return PosixParent(path);
    }

    private static string PosixParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "/";
        var normalized = path.TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    private static string PathName(EndpointChoice choice, string path)
    {
        if (choice.IsLocal)
        {
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        var normalized = path.TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string EndpointPathLabel(EndpointChoice choice, string path) =>
        choice.IsLocal ? path : $"{choice.Profile!.Name}:{path}";

    private static string RemoteSpec(ConnectionProfile profile, string path)
        => RemoteSpec(profile.Username, profile.Host, path);

    private static string RemoteSpec(string username, string hostValue, string path)
    {
        var host = hostValue.Contains(':', StringComparison.Ordinal) ? $"[{hostValue}]" : hostValue;
        return $"{username}@{host}:{path}";
    }

    private static string QuotePreviewArgument(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || "-_=./:@\\".Contains(character, StringComparison.Ordinal))
            ? value
            : "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private sealed class EndpointState(
        string name,
        ComboBox hostComboBox,
        TextBox pathTextBox,
        ListView fileList,
        TextBlock statusText,
        ProgressBar progress,
        TextBlock endpointText)
    {
        public string Name { get; } = name;
        public ComboBox HostComboBox { get; } = hostComboBox;
        public TextBox PathTextBox { get; } = pathTextBox;
        public ListView FileList { get; } = fileList;
        public TextBlock StatusText { get; } = statusText;
        public ProgressBar Progress { get; } = progress;
        public TextBlock EndpointText { get; } = endpointText;
        public ObservableCollection<MachineTransferBrowserItem> Items { get; } = [];
        public EndpointChoice? Choice { get; set; }
        public CancellationTokenSource? LoadCancellation { get; set; }
        public int LoadGeneration { get; set; }
    }

    private sealed record EndpointChoice(bool IsLocal, ConnectionProfile? Profile, string DisplayName)
    {
        public string EndpointLabel => IsLocal ? LocalizationService.Get("Local") : Profile!.DisplayEndpoint;
        public static EndpointChoice Local(string displayName) => new(true, null, $"[{displayName}]");
        public static EndpointChoice Remote(ConnectionProfile profile) => new(false, profile, $"{profile.Group} / {profile.Name}");
    }

    private sealed record RouteProbeChoice(
        string FirstHopProfileId,
        string TargetProfileId,
        RsyncRemoteTransferExecutionSide ExecutionSide,
        string ExecuteOn,
        string TargetSession,
        string InterfaceName,
        string Host,
        int Port,
        bool UseTargetProxy,
        bool Success,
        long LatencyMilliseconds,
        string Fingerprint,
        string Message)
    {
        public string Status => LocalizationService.Get(Success ? "RouteAvailable" : "RouteUnavailable");
        public string LatencyDisplay => LatencyMilliseconds > 0 ? $"{LatencyMilliseconds} ms" : "—";
    }

    private sealed record RouteProbeAttempt(
        IReadOnlyList<RsyncRemoteRouteProbeResult> Results,
        string? Error);

    private sealed record TransferOptionSnapshot(
        bool Archive,
        bool Compress,
        bool Delete,
        bool DryRun,
        int BandwidthLimitKbps,
        IReadOnlyList<string> ExtraArguments);

    private sealed record TransferSpecification(
        EndpointState Source,
        EndpointState Destination,
        IReadOnlyList<MachineTransferBrowserItem> Sources,
        string DestinationPath,
        RsyncRemoteTransferExecutionSide ExecutionSide,
        TransferOptionSnapshot Options);

    private sealed record MachineTransferBrowserItem(
        string Name,
        string FullPath,
        bool IsDirectory,
        bool IsParent,
        long Size,
        DateTimeOffset Modified)
    {
        public string Glyph => IsDirectory ? "📁" : "📄";
        public string SizeDisplay => IsDirectory ? string.Empty : RemoteFileEntrySize(Size);
        public string ModifiedDisplay => IsParent || Modified == DateTimeOffset.MinValue
            ? string.Empty
            : Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

        private static string RemoteFileEntrySize(long value)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var size = (double)Math.Max(0, value);
            var unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return unit == 0 ? $"{value} B" : $"{size:0.#} {units[unit]}";
        }
    }

    private sealed class MachineTransferJob : INotifyPropertyChanged
    {
        private readonly int _logLimit;
        private string _status;
        private string _progress = string.Empty;
        private string _speed = string.Empty;
        private bool _isRunning = true;
        private long _lastProgressBytes;
        private long _lastProgressTimestamp;

        public MachineTransferJob(int number, string source, string destination, int logLimit)
        {
            Number = number;
            Source = source;
            Destination = destination;
            _logLimit = logLimit;
            _status = LocalizationService.Get("JobQueuedStatus");
        }

        public int Number { get; }
        public string Source { get; }
        public string Destination { get; }
        public List<string> LogLines { get; } = [];
        public CancellationTokenSource? Cancellation { get; set; }
        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }
        public string Progress
        {
            get => _progress;
            private set
            {
                if (_progress == value) return;
                _progress = value;
                OnPropertyChanged();
            }
        }
        public string Speed
        {
            get => _speed;
            private set
            {
                if (_speed == value) return;
                _speed = value;
                OnPropertyChanged();
            }
        }
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning == value) return;
                _isRunning = value;
                OnPropertyChanged();
            }
        }

        public void Append(string line)
        {
            LogLines.Add($"[{DateTime.Now:T}] {line}");
            if (LogLines.Count > _logLimit)
            {
                LogLines.RemoveRange(0, LogLines.Count - _logLimit);
            }
        }

        public void UpdateProgress(RsyncWorkerEvent transferEvent)
        {
            var bytes = transferEvent.TransferredBytes ??
                        Math.Max(transferEvent.ProtocolReadBytes, transferEvent.ProtocolWrittenBytes);
            var now = Stopwatch.GetTimestamp();
            var bytesPerSecond = transferEvent.BytesPerSecond;
            if (bytesPerSecond is null && _lastProgressTimestamp > 0 && bytes >= _lastProgressBytes)
            {
                var elapsed = Stopwatch.GetElapsedTime(_lastProgressTimestamp, now).TotalSeconds;
                if (elapsed > 0)
                {
                    bytesPerSecond = (long)((bytes - _lastProgressBytes) / elapsed);
                }
            }
            _lastProgressBytes = bytes;
            _lastProgressTimestamp = now;

            var transferred = FormatTransferSize(bytes);
            Progress = transferEvent.Percent is { } percent
                ? $"{percent:0.#}% · {transferred}"
                : transferred;
            Speed = bytesPerSecond is > 0
                ? FormatTransferSize(bytesPerSecond.Value) + "/s"
                : string.Empty;
        }

        private static string FormatTransferSize(long value)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
            var size = (double)Math.Max(0, value);
            var unit = 0;
            while (size >= 1000 && unit < units.Length - 1)
            {
                size /= 1000;
                unit++;
            }
            return unit == 0 ? $"{value} B" : $"{size:0.#} {units[unit]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
