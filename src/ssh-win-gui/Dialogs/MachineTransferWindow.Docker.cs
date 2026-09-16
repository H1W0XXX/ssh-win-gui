using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;

namespace RsyncShell.App.Dialogs;

public partial class MachineTransferWindow
{
    private bool IsDockerMode => TransferKindComboBox.SelectedIndex == 1;
    private bool _dockerPreparing;
    private ViewBase? _fileViewA;
    private ViewBase? _fileViewB;

    private async void TransferKind_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        MinHeight = IsDockerMode ? 800 : 680;
        TransferSubtitle.SetResourceReference(TextBlock.TextProperty, IsDockerMode ? "DockerTransferSubtitle" : "TipMachineTransfer");
        _fileViewA ??= AFileList.View;
        _fileViewB ??= BFileList.View;
        InvalidateRouteChoices();
        FileOptionsPanel.Visibility = IsDockerMode ? Visibility.Collapsed : Visibility.Visible;
        DockerOptionsPanel.Visibility = IsDockerMode ? Visibility.Visible : Visibility.Collapsed;
        PreviewCommandButton.Visibility = IsDockerMode ? Visibility.Collapsed : Visibility.Visible;
        // Refresh remains available in Docker mode without a filesystem path.
        APathPanel.Visibility = BPathPanel.Visibility = Visibility.Visible;
        foreach (var panel in new[] { APathPanel, BPathPanel })
            foreach (UIElement child in panel.Children)
                if (Grid.GetColumn(child) != 4) child.Visibility = IsDockerMode ? Visibility.Collapsed : Visibility.Visible;
        AFileList.View = IsDockerMode ? CreateDockerView() : _fileViewA;
        BFileList.View = IsDockerMode ? CreateDockerView() : _fileViewB;
        foreach (var endpoint in new[] { _endpointA, _endpointB })
        {
            endpoint.LoadCancellation?.Cancel();
            endpoint.LoadGeneration++;
            endpoint.Items.Clear();
            endpoint.DockerImages = [];
        }
        await Task.WhenAll(RefreshEndpointAsync(_endpointA), RefreshEndpointAsync(_endpointB));
    }

    private static GridView CreateDockerView()
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Repository:Tag", DisplayMemberBinding = new Binding("Name"), Width = 245 });
        view.Columns.Add(new GridViewColumn { Header = LocalizationService.Get("Size"), DisplayMemberBinding = new Binding("SizeDisplay"), Width = 80 });
        view.Columns.Add(new GridViewColumn { Header = "Image ID", DisplayMemberBinding = new Binding("ImageId"), Width = 180 });
        return view;
    }

    private void DockerFilter_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || !IsDockerMode) return;
        ApplyDockerFilter(_endpointA);
        ApplyDockerFilter(_endpointB);
    }

    private void ApplyDockerFilter(EndpointState endpoint)
    {
        var selected = endpoint.FileList.SelectedItems.OfType<MachineTransferBrowserItem>().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var filter = DockerFilterTextBox.Text.Trim();
        endpoint.Items.Clear();
        foreach (var image in endpoint.DockerImages.Where(image =>
                     (HideK8sCheckBox.IsChecked != true || !image.IsKubernetes) &&
                     image.Reference.Contains(filter, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(image => image.Reference, StringComparer.Ordinal))
        {
            endpoint.Items.Add(new MachineTransferBrowserItem(image.Reference, image.Reference, false, false, 0, DateTimeOffset.MinValue) { Image = image });
        }
        endpoint.FileList.ItemsSource = endpoint.Items;
        foreach (var item in endpoint.Items.Where(item => selected.Contains(item.Name))) endpoint.FileList.SelectedItems.Add(item);
        endpoint.StatusText.Text = LocalizationService.Format("DockerImageCount", endpoint.Items.Count, endpoint.DockerImages.Count);
    }

    private async Task RefreshDockerEndpointAsync(EndpointState endpoint)
    {
        var profile = endpoint.Choice?.Profile;
        if (profile is null) return;
        endpoint.LoadCancellation?.Cancel();
        endpoint.LoadCancellation?.Dispose();
        endpoint.LoadCancellation = new CancellationTokenSource();
        var token = endpoint.LoadCancellation.Token;
        var generation = ++endpoint.LoadGeneration;
        endpoint.DockerImages = [];
        endpoint.Items.Clear();
        endpoint.Progress.Visibility = Visibility.Visible;
        endpoint.StatusText.Text = LocalizationService.Get("DockerLoading");
        try
        {
            if (_workerPath is null) throw new InvalidOperationException(LocalizationService.Get("WorkerMissing"));
            var authentication = ResolveAuthentication(profile);
            if (authentication is null) { endpoint.StatusText.Text = LocalizationService.Get("AuthenticationCancelled"); return; }
            var images = await new RsyncWorkerTransferService(_workerPath).ListDockerImagesAsync(
                profile, SshRouteResolver.Resolve(profile, _profiles), authentication, token);
            if (generation != endpoint.LoadGeneration || token.IsCancellationRequested || !IsDockerMode) return;
            endpoint.DockerImages = images;
            ApplyDockerFilter(endpoint);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != endpoint.LoadGeneration || token.IsCancellationRequested) return;
            endpoint.StatusText.Text = ex.Message;
            AppendDiagnostic("docker:" + profile.Name, ex.ToString());
            ShowDockerError(ex, profile.Name);
        }
        finally
        {
            if (generation == endpoint.LoadGeneration) endpoint.Progress.Visibility = Visibility.Collapsed;
        }
    }

    internal static IReadOnlyList<DockerImageSelection> BuildDockerSelections(
        IReadOnlyList<DockerImage> selected, IReadOnlyList<DockerImage> source, IReadOnlyList<DockerImage> destination)
    {
        var result = new List<DockerImageSelection>();
        foreach (var image in selected.DistinctBy(image => image.Reference))
        {
            var current = source.FirstOrDefault(candidate => candidate.Reference == image.Reference);
            if (current is null || current.Id != image.Id)
                throw new InvalidOperationException(LocalizationService.Format("DockerSourceChanged", image.Reference));
            result.Add(new DockerImageSelection(image.Reference, image.Id,
                destination.FirstOrDefault(candidate => candidate.Reference == image.Reference)?.Id ?? string.Empty));
        }
        return result;
    }

    private async Task StartDockerTransferAsync()
    {
        if (_dockerPreparing) return;
        var source = AToBRadio.IsChecked == true ? _endpointA : _endpointB;
        var destination = AToBRadio.IsChecked == true ? _endpointB : _endpointA;
        var sourceProfile = source.Choice?.Profile;
        var destinationProfile = destination.Choice?.Profile;
        if (sourceProfile is null || destinationProfile is null) { ShowError(LocalizationService.Get("SelectBothEndpoints")); return; }
        if (sourceProfile.Id == destinationProfile.Id) { ShowError(LocalizationService.Get("DockerSameHost")); return; }
        var selected = source.FileList.SelectedItems.OfType<MachineTransferBrowserItem>().Select(item => item.Image).OfType<DockerImage>().ToArray();
        if (selected.Length == 0 || selected.Length > 200) { ShowError(LocalizationService.Get("DockerSelectImages")); return; }
        var route = _selectedRoute;
        if (route is not { Success: true } ||
            route.FirstHopProfileId != (route.ExecutionSide == RsyncRemoteTransferExecutionSide.Source ? sourceProfile.Id : destinationProfile.Id) ||
            route.TargetProfileId != (route.ExecutionSide == RsyncRemoteTransferExecutionSide.Source ? destinationProfile.Id : sourceProfile.Id))
        { ShowError(LocalizationService.Get("SelectRouteBeforeTransfer")); RouteProbeTab.IsSelected = true; return; }
        if (_workerPath is null) { ShowError(LocalizationService.Get("WorkerMissing")); return; }
        if (_jobs.Any(job => job.IsRunning)) { ShowError(LocalizationService.Get("DockerWaitJobs")); return; }
        var sourceAuthentication = ResolveAuthentication(sourceProfile);
        var destinationAuthentication = ResolveAuthentication(destinationProfile);
        if (sourceAuthentication is null || destinationAuthentication is null) return;
        var sourceRoute = SshRouteResolver.Resolve(sourceProfile, _profiles);
        var destinationRoute = SshRouteResolver.Resolve(destinationProfile, _profiles);
        var zstd = ZstdCheckBox.IsChecked == true;
        SetDockerPreparing(true);
        try
        {
            // Always query the unfiltered target inventory, including hidden K8s tags.
            var currentSource = await new RsyncWorkerTransferService(_workerPath).ListDockerImagesAsync(sourceProfile, sourceRoute, sourceAuthentication);
            var currentDestination = await new RsyncWorkerTransferService(_workerPath).ListDockerImagesAsync(destinationProfile, destinationRoute, destinationAuthentication);
            var images = BuildDockerSelections(selected, currentSource, currentDestination);
            var collisions = images.Where(image => image.DestinationId.Length > 0).ToArray();
            if (collisions.Length > 0 && MessageBox.Show(this,
                    LocalizationService.Format("DockerCollision", string.Join(Environment.NewLine, collisions.Select(image =>
                        image.Reference + (image.SourceId == image.DestinationId ? " [= ID]" : " [≠ ID]")))),
                    LocalizationService.Get("DockerTransferMode"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var request = new RsyncRemoteTransferRequest
            {
                SourceProfile = sourceProfile, SourceRoute = sourceRoute, SourceAuthentication = sourceAuthentication,
                DestinationProfile = destinationProfile, DestinationRoute = destinationRoute, DestinationAuthentication = destinationAuthentication,
                SourcePath = "/", DestinationPath = "/", // Unused by the Docker streaming operation.
                ExecutionSide = route.ExecutionSide,
                SourceTransferHost = route.ExecutionSide == RsyncRemoteTransferExecutionSide.Destination && !route.UseTargetProxy ? route.Host : null,
                SourceTransferPort = route.ExecutionSide == RsyncRemoteTransferExecutionSide.Destination && !route.UseTargetProxy ? route.Port : 0,
                DestinationTransferHost = route.ExecutionSide == RsyncRemoteTransferExecutionSide.Source && !route.UseTargetProxy ? route.Host : null,
                DestinationTransferPort = route.ExecutionSide == RsyncRemoteTransferExecutionSide.Source && !route.UseTargetProxy ? route.Port : 0,
                Docker = new DockerTransferOptions(images, zstd),
            };
            var job = new MachineTransferJob(_nextJobNumber++, sourceProfile.Name + ": " + string.Join(", ", images.Select(image => image.Reference)), destinationProfile.Name, JobLogLineLimit);
            _jobs.Insert(0, job);
            JobsList.SelectedItem = job;
            TransferJobsTab.IsSelected = true;
            _ = RunDockerJobAsync(job, request);
        }
        catch (Exception ex) { ShowDockerError(ex, sourceProfile.Name + " / " + destinationProfile.Name); }
        finally { SetDockerPreparing(false); }
    }

    private void SetDockerPreparing(bool preparing)
    {
        _dockerPreparing = preparing;
        foreach (var control in new Control[] { TransferKindComboBox, AHostComboBox, BHostComboBox, AToBRadio, BToARadio, StartTransferButton, DiscoverRoutesButton, RouteResultsList })
            control.IsEnabled = !preparing;
        if (!preparing) UpdateTransferMode();
    }

    private async Task RunDockerJobAsync(MachineTransferJob job, RsyncRemoteTransferRequest request)
    {
        var service = new RsyncWorkerTransferService(_workerPath!);
        service.EventReceived += (_, ev) => _ = Dispatcher.InvokeAsync(() =>
        {
            if (ev.Type == "progress" && ev.Phase?.StartsWith("docker_", StringComparison.Ordinal) == true)
            {
                job.UpdateProgress(ev with { BytesPerSecond = ev.BytesPerSecond ?? 0 });
                job.Status = LocalizationService.Get(ev.Phase switch
                {
                    "docker_prepare" => "DockerPreparing",
                    "docker_import" => "DockerImporting",
                    "docker_verify" => "DockerVerifying",
                    _ => "DockerStreaming",
                });
                return;
            }
            if (!string.IsNullOrWhiteSpace(ev.Message ?? ev.State)) job.Append(ev.Message ?? ev.State!);
            RefreshSelectedJobLog(job);
        });
        job.Cancellation = new CancellationTokenSource();
        job.Status = LocalizationService.Get("DockerPreparing");
        job.Append(LocalizationService.Get("DockerRunning"));
        try
        {
            await service.TransferRemoteToRemoteAsync(request, job.Cancellation.Token);
            job.Status = LocalizationService.Get("JobSucceeded");
            job.Append(LocalizationService.Get("DockerVerified"));
            if (IsDockerMode)
                foreach (var endpoint in new[] { _endpointA, _endpointB }.Where(endpoint => endpoint.Choice?.Profile?.Id == request.DestinationProfile.Id))
                    await RefreshDockerEndpointAsync(endpoint);
        }
        catch (OperationCanceledException) { job.Status = LocalizationService.Get("JobCancelled"); job.Append(LocalizationService.Get("DockerPartial")); }
        catch (Exception ex)
        {
            job.Status = LocalizationService.Get("JobFailed");
            job.Append(ex.Message);
            job.Append(LocalizationService.Get("DockerPartial"));
            ShowDockerError(ex, request.SourceProfile.Name + " / " + request.DestinationProfile.Name);
        }
        finally { job.IsRunning = false; RefreshSelectedJobLog(job); }
    }

    private void ShowDockerError(Exception exception, string host)
    {
        var sudo = exception is RsyncWorkerException { Code: "sudo_required" } || exception.Message.Contains("SUDO_REQUIRED:", StringComparison.Ordinal);
        ShowError(sudo ? LocalizationService.Format("DockerSudoRequired", host) : host + ": " + exception.Message);
    }
}
