using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RsyncShell.App.Controls;
using RsyncShell.App.Dialogs;
using RsyncShell.App.Services;
using RsyncShell.Core.Models;
using RsyncShell.Core.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Directory = System.IO.Directory;
using File = System.IO.File;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;

namespace RsyncShell.App;

public partial class MainWindow : Window
{
    private readonly ExternalToolLocator _toolLocator = new();
    private readonly SessionRepository _sessionRepository = new();
    private readonly KnownHostStore _knownHostStore = new();
    private readonly SshHostKeyVerifier _hostKeyVerifier;
    private readonly SshTunnelService _tunnelService = new();
    private readonly List<ConnectionProfile> _profiles = [];
    private readonly List<TerminalViewState> _terminals = [];
    private readonly Queue<string> _transferLogLines = new();
    private readonly Dictionary<int, CancellationTokenSource> _transferOperations = [];
    private readonly TaskCompletionSource _initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalViewState? _activeTerminal;
    private bool _sidebarLockedToSessions;
    private bool _updatingNetworkInterface;
    private bool _updatingDiskMount;
    private int _nextTerminalNumber = 1;
    private int _nextTransferNumber = 1;
    private TunnelManagerWindow? _tunnelWindow;
    private readonly KeyboardHookProc _keyboardHookProc;
    private IntPtr _keyboardHook;
    private IntPtr _mainWindowHandle;
    private bool _hookShiftDown;
    private bool _hookControlDown;
    private bool _hookAltDown;
    private bool _hookConsumedShiftInsert;

    public MainWindow()
    {
        InitializeComponent();
        _keyboardHookProc = KeyboardHookCallback;
        _hostKeyVerifier = new SshHostKeyVerifier(this, _knownHostStore);
        LocalizationService.LanguageChanged += LocalizationService_OnLanguageChanged;
        Loaded += MainWindow_OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _mainWindowHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _keyboardHook = SetWindowsHookEx(13, _keyboardHookProc, IntPtr.Zero, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            DiagnosticLog.Write("TerminalInput", $"Unable to install UI keyboard hook: {Marshal.GetLastWin32Error()}.");
        }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && GetForegroundWindow() == _mainWindowHandle)
        {
            var message = unchecked((int)wParam.ToInt64());
            var keyboard = Marshal.PtrToStructure<LowLevelKeyboardInput>(lParam);
            var virtualKey = unchecked((int)keyboard.VirtualKey);
            var isKeyDown = message is 0x0100 or 0x0104;
            var isKeyUp = message is 0x0101 or 0x0105;
            switch (virtualKey)
            {
                case 0x10:
                case 0xA0:
                case 0xA1:
                    _hookShiftDown = isKeyDown;
                    break;
                case 0x11:
                case 0xA2:
                case 0xA3:
                    _hookControlDown = isKeyDown;
                    break;
                case 0x12:
                case 0xA4:
                case 0xA5:
                    _hookAltDown = isKeyDown;
                    break;
                case 0x2D when isKeyDown && _hookShiftDown && !_hookControlDown && !_hookAltDown:
                    _hookConsumedShiftInsert = true;
                    Dispatcher.BeginInvoke(() => _activeTerminal?.Surface.PasteClipboard());
                    return new IntPtr(1);
                case 0x2D when isKeyUp && _hookConsumedShiftInsert:
                    _hookConsumedShiftInsert = false;
                    return new IntPtr(1);
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _profiles.AddRange(await _sessionRepository.LoadAsync());
        }
        catch (Exception ex)
        {
            SetConnectionStatus(LocalizationService.Format("ErrorLoadSessions", ex.Message), isError: true);
        }
        finally
        {
            RebuildSessionTree();
            UpdateToolStatus();
            UpdateLanguageMenu();
            UpdateMousePasteMenu();
            UpdateKeywordHighlightingMenu();
            QuickConnectBox.Focus();
            _initialization.TrySetResult();
        }
    }

    private void UpdateToolStatus()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.Terminal.Control.dll")))
        {
            ToolStatusText.Text = LocalizationService.Get("TerminalNativeMissing");
            ToolStatusText.Foreground = Brushes.Firebrick;
            return;
        }

        ToolStatusText.Text = LocalizationService.Get(
            _toolLocator.FindRsyncWorker() is null ? "SshReadyWorkerMissing" : "SshReadyRsyncReady");
    }

    private void RebuildSessionTree()
    {
        SessionTree.Items.Clear();
        foreach (var group in _profiles
                     .OrderByDescending(profile => profile.Favorite)
                     .ThenBy(profile => profile.Group, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                     .GroupBy(profile => profile.Favorite ? LocalizationService.Get("Favorites") : profile.Group))
        {
            var groupItem = new TreeViewItem
            {
                Header = string.Equals(group.Key, "Sessions", StringComparison.Ordinal)
                    ? LocalizationService.Get("MenuSessions")
                    : group.Key,
                IsExpanded = true,
                FontWeight = FontWeights.SemiBold,
            };
            foreach (var profile in group)
            {
                groupItem.Items.Add(new TreeViewItem
                {
                    Header = profile.Name,
                    Tag = profile,
                    ToolTip = profile.DisplayEndpoint,
                    FontWeight = FontWeights.Normal,
                });
            }

            SessionTree.Items.Add(groupItem);
        }

        SessionCountText.Text = LocalizationService.Format("SessionCount", _profiles.Count);
        UpdateSessionCommands();
    }

    private async void QuickConnectBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await OpenQuickConnectAsync(QuickConnectBox.Text);
    }

    public async Task OpenQuickConnectAsync(string endpoint)
    {
        await _initialization.Task;
        if (!ConnectionProfile.TryParseQuickConnect(
                endpoint,
                Environment.UserName,
                out var parsed,
                out var error) || parsed is null)
        {
            SetConnectionStatus(LocalizationService.TranslateProfileError(error), isError: true);
            if (error.StartsWith("Inline SSH passwords", StringComparison.Ordinal))
            {
                QuickConnectBox.Clear();
            }
            return;
        }

        var profile = _profiles.FirstOrDefault(existing =>
            string.Equals(existing.Host, parsed.Host, StringComparison.OrdinalIgnoreCase) &&
            existing.Port == parsed.Port &&
            string.Equals(existing.Username, parsed.Username, StringComparison.OrdinalIgnoreCase));
        profile ??= parsed with { Name = parsed.DisplayEndpoint };

        QuickConnectBox.Clear();
        await OpenTerminalAsync(profile);
    }

    private void SessionTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        UpdateSessionCommands();

    private async void SessionTree_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var item = FindSessionTreeItemAt(e.GetPosition(SessionTree));
        if (item?.Tag is ConnectionProfile profile)
        {
            // The header content does not fill the whole TreeView width. Handle the
            // row ourselves so clicking the blank area to its right keeps it selected.
            e.Handled = true;
            item.IsSelected = true;
            item.Focus();
            if (e.ClickCount == 2)
            {
                await OpenTerminalAsync(profile);
            }
            return;
        }

        if (item is null)
        {
            ClearSessionTreeSelection();
            SessionTree.Focus();
        }
    }

    private TreeViewItem? FindSessionTreeItemAt(Point point)
    {
        foreach (var group in SessionTree.Items.OfType<TreeViewItem>())
        {
            // An expanded group's ActualHeight includes all of its children, so
            // child rows must be tested before the group container.
            foreach (var item in group.Items.OfType<TreeViewItem>())
            {
                if (item.IsVisible && IsPointWithinTreeItemRow(item, point))
                {
                    return item;
                }
            }

            if (IsPointWithinTreeItemRow(group, point))
            {
                return group;
            }
        }

        return null;
    }

    private bool IsPointWithinTreeItemRow(TreeViewItem item, Point point)
    {
        var origin = item.TranslatePoint(new Point(0, 0), SessionTree);
        return point.Y >= origin.Y && point.Y < origin.Y + item.ActualHeight;
    }

    private void ClearSessionTreeSelection()
    {
        foreach (var group in SessionTree.Items.OfType<TreeViewItem>())
        {
            group.IsSelected = false;
            foreach (var item in group.Items.OfType<TreeViewItem>())
            {
                item.IsSelected = false;
            }
        }

        UpdateSessionCommands();
    }

    private void SessionTreeItem_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        item.IsSelected = true;
        item.Focus();
        if (item.Tag is not ConnectionProfile || SessionTree.ContextMenu is null) return;
        SessionTree.ContextMenu.PlacementTarget = item;
        SessionTree.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private async void NewSessionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SessionDialog(savedProfiles: _profiles) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null)
        {
            return;
        }

        _profiles.Add(dialog.Profile);
        if (!await SaveSessionsAsync())
        {
            _profiles.Remove(dialog.Profile);
            return;
        }

        RebuildSessionTree();
        SetConnectionStatus(LocalizationService.Format("SessionSaved", dialog.Profile.Name));
        ShowSidebar(SidebarView.Sessions);
        if (dialog.ConnectAfterSave)
        {
            await OpenTerminalAsync(dialog.Profile);
        }
    }

    private void TunnelButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_tunnelWindow is { IsVisible: true })
        {
            _tunnelWindow.Activate();
            return;
        }
        _tunnelWindow = new TunnelManagerWindow(_tunnelService, _profiles, _hostKeyVerifier.Verify)
        {
            Owner = this,
        };
        _tunnelWindow.Closed += (_, _) => _tunnelWindow = null;
        _tunnelWindow.Show();
    }

    private async void EditSessionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var existing = SelectedProfile();
        if (existing is null)
        {
            SetConnectionStatus(LocalizationService.Get("NoSessionSelected"), isError: true);
            return;
        }

        var dialog = new SessionDialog(existing, _profiles) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null)
        {
            return;
        }

        var index = _profiles.IndexOf(existing);
        _profiles[index] = dialog.Profile;
        if (!await SaveSessionsAsync())
        {
            _profiles[index] = existing;
            return;
        }

        RebuildSessionTree();
        SetConnectionStatus(LocalizationService.Format("SessionUpdated", dialog.Profile.Name));
        if (dialog.ConnectAfterSave)
        {
            await OpenTerminalAsync(dialog.Profile);
        }
    }

    private async void DeleteSessionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            SetConnectionStatus(LocalizationService.Get("NoSessionSelected"), isError: true);
            return;
        }

        if (MessageBox.Show(
                this,
                LocalizationService.Format("DeleteSessionPrompt", profile.Name),
                LocalizationService.Get("DeleteSessionTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var index = _profiles.IndexOf(profile);
        _profiles.RemoveAt(index);
        if (!await SaveSessionsAsync())
        {
            _profiles.Insert(index, profile);
            return;
        }

        RebuildSessionTree();
        SetConnectionStatus(LocalizationService.Format("SessionDeleted", profile.Name));
    }

    private ConnectionProfile? SelectedProfile() =>
        SessionTree.SelectedItem is TreeViewItem { Tag: ConnectionProfile profile } ? profile : null;

    private void UpdateSessionCommands()
    {
        var enabled = SelectedProfile() is not null;
        EditSessionButton.IsEnabled = enabled;
        DeleteSessionButton.IsEnabled = enabled;
        EditSessionMenuItem.IsEnabled = enabled;
        DeleteSessionMenuItem.IsEnabled = enabled;
    }

    private async Task<bool> SaveSessionsAsync()
    {
        try
        {
            await _sessionRepository.SaveAsync(_profiles);
            return true;
        }
        catch (Exception ex)
        {
            SetConnectionStatus(LocalizationService.Format("ErrorSaveSessions", ex.Message), isError: true);
            return false;
        }
    }

    private async Task OpenTerminalAsync(ConnectionProfile profile)
    {
        IReadOnlyList<ConnectionProfile> route;
        try
        {
            route = SshRouteResolver.Resolve(profile, _profiles);
        }
        catch (Exception ex)
        {
            SetConnectionStatus(ex.Message, isError: true);
            return;
        }
        SshAuthenticationOptions? authentication = null;
        if (!string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            authentication = new SshAuthenticationOptions
            {
                Kind = SshAuthenticationKind.PrivateKey,
                PrivateKeyPath = Environment.ExpandEnvironmentVariables(profile.PrivateKeyPath),
            };
        }

        var surface = new SshTerminalHost(profile, authentication, _hostKeyVerifier.Verify, route);
        var state = new TerminalViewState
        {
            Number = _nextTerminalNumber++,
            Profile = profile,
            Route = route,
            Surface = surface,
            View = surface,
        };
        surface.StateChanged += (_, args) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                state.HostState = args.State;
                if (args.State != TerminalHostState.Connected)
                {
                    DisconnectBrowser(state);
                    StopMonitoring(state);
                }

                if (_activeTerminal == state)
                {
                    SetConnectionStatus(args.Message, args.State == TerminalHostState.Failed);
                    UpdateBrowserControls(state);
                    UpdateMonitoringView(state);
                    if (args.State == TerminalHostState.Connected)
                    {
                        _ = LoadRemoteDirectoryAsync(state, state.RemotePath);
                        StartMonitoring(state);
                    }
                    else
                    {
                        BrowserStatusText.Text = LocalizationService.Get("BrowserDisconnected");
                        BrowserStatusText.Foreground = args.State == TerminalHostState.Failed
                            ? Brushes.Firebrick
                            : (Brush)FindResource("MutedTextBrush");
                    }
                }
            });
        };

        var tabButton = BuildTerminalTabButton(state);
        state.TabButton = tabButton;
        _terminals.Add(state);
        TerminalTabStrip.Children.Insert(TerminalTabStrip.Children.Count - 1, tabButton);
        TerminalStage.Children.Add(surface);

        await ActivateTerminalAsync(state);
    }

    private ToggleButton BuildTerminalTabButton(TerminalViewState state)
    {
        var close = new Button
        {
            Content = "×",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = LocalizationService.Get("CloseSession"),
        };
        var title = new TextBlock
        {
            Text = $"{state.Number}. {state.Profile.Name}",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var content = new DockPanel();
        var terminalIcon = new TextBlock
        {
            Text = "\uE756",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(terminalIcon, Dock.Left);
        content.Children.Add(close);
        content.Children.Add(terminalIcon);
        content.Children.Add(title);

        var button = new ToggleButton
        {
            Style = (Style)FindResource("TerminalTabButton"),
            Content = content,
            ToolTip = state.Profile.DisplayEndpoint,
        };
        button.Click += async (_, _) => await ActivateTerminalAsync(state);
        close.Click += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            CloseTerminal(state);
        };
        return button;
    }

    private async Task ActivateTerminalAsync(TerminalViewState state)
    {
        if (_activeTerminal is { } previous && previous != state)
        {
            StopMonitoring(previous);
        }
        _activeTerminal = state;
        HomeView.Visibility = Visibility.Hidden;
        HomeTabButton.IsChecked = false;
        foreach (var terminal in _terminals)
        {
            terminal.View.Visibility = terminal == state ? Visibility.Visible : Visibility.Hidden;
            if (terminal.TabButton is not null)
            {
                terminal.TabButton.IsChecked = terminal == state;
            }
        }

        ActiveUserText.Text = state.Profile.DisplayEndpoint;
        ActivePathStatusText.Text = state.RemotePath;
        Title = $"{state.Profile.Name} - ssh-win-gui";
        SetConnectionStatus(LocalizationService.Format("ActiveSession", state.Profile.Name));
        RemoteFileList.ItemsSource = state.RemoteItems;
        RemotePathBox.ItemsSource = state.RemotePathHistory;
        RemotePathBox.Text = state.RemotePath;
        UpdateBrowserControls(state);
        UpdateMonitoringView(state);
        if (state.HostState == TerminalHostState.Connected)
        {
            StartMonitoring(state);
        }
        state.Surface.FocusTerminal();

        if (!_sidebarLockedToSessions)
        {
            ShowSidebar(SidebarView.Browser);
        }

        if (!state.BrowserLoaded && state.HostState == TerminalHostState.Connected)
        {
            await LoadRemoteDirectoryAsync(state, state.RemotePath);
        }
        else if (!state.BrowserLoaded)
        {
            var disconnected = state.HostState is TerminalHostState.Exited or TerminalHostState.Failed;
            BrowserStatusText.Text = LocalizationService.Get(disconnected ? "BrowserDisconnected" : "WaitingSsh");
            BrowserStatusText.Foreground = state.HostState == TerminalHostState.Failed
                ? Brushes.Firebrick
                : (Brush)FindResource("MutedTextBrush");
            BrowserItemCountText.Text = LocalizationService.Format("ItemsCount", 0);
        }
        else
        {
            UpdateBrowserSummary(state);
        }
    }

    private void CloseTerminal(TerminalViewState state)
    {
        var wasActive = _activeTerminal == state;
        StopMonitoring(state);
        DisconnectBrowser(state);
        state.Surface.Dispose();
        TerminalStage.Children.Remove(state.View);
        if (state.TabButton is not null)
        {
            TerminalTabStrip.Children.Remove(state.TabButton);
        }
        _terminals.Remove(state);

        if (!wasActive)
        {
            return;
        }

        if (_terminals.Count > 0)
        {
            _ = ActivateTerminalAsync(_terminals[^1]);
        }
        else
        {
            ShowHome();
        }
    }

    private void ShowHome()
    {
        if (_activeTerminal is { } previous)
        {
            StopMonitoring(previous);
        }
        _activeTerminal = null;
        HomeView.Visibility = Visibility.Visible;
        HomeTabButton.IsChecked = true;
        foreach (var terminal in _terminals)
        {
            terminal.View.Visibility = Visibility.Hidden;
            if (terminal.TabButton is not null)
            {
                terminal.TabButton.IsChecked = false;
            }
        }

        ActiveUserText.Text = LocalizationService.Get("Local");
        ActivePathStatusText.Text = "—";
        Title = "ssh-win-gui";
        SetConnectionStatus(LocalizationService.Get("Ready"));
        RemoteFileList.ItemsSource = null;
        RemotePathBox.ItemsSource = null;
        RemotePathBox.Text = string.Empty;
        BrowserStatusText.Text = LocalizationService.Get("NoActiveSshSession");
        BrowserStatusText.Foreground = (Brush)FindResource("MutedTextBrush");
        BrowserItemCountText.Text = string.Empty;
        RemoteMonitoringPanel.Visibility = Visibility.Collapsed;
        UpdateBrowserControls(null);
        ShowSidebar(SidebarView.Sessions);
    }

    private void StartMonitoring(TerminalViewState state)
    {
        StopMonitoring(state);
        if (_activeTerminal != state || state.HostState != TerminalHostState.Connected)
        {
            return;
        }

        state.PreviousMonitoringSnapshot = null;
        state.MonitoringSnapshot = null;
        state.MonitoringError = null;
        var operation = new CancellationTokenSource();
        state.MonitoringOperation = operation;
        UpdateMonitoringView(state);
        _ = RunMonitoringLoopAsync(state, operation);
    }

    private static void StopMonitoring(TerminalViewState state)
    {
        var operation = state.MonitoringOperation;
        state.MonitoringOperation = null;
        operation?.Cancel();
    }

    private async Task RunMonitoringLoopAsync(TerminalViewState state, CancellationTokenSource operation)
    {
        try
        {
            while (!operation.IsCancellationRequested)
            {
                try
                {
                    await using var monitor = await RemoteMonitoringService.ConnectAsync(
                        state.Profile,
                        state.Surface.Authentication,
                        _hostKeyVerifier.Verify,
                        state.Route,
                        operation.Token);
                    while (!operation.IsCancellationRequested)
                    {
                        using var sampleTimeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                        sampleTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                        RemoteMonitoringSnapshot snapshot;
                        try
                        {
                            snapshot = await monitor.SampleAsync(sampleTimeout.Token);
                        }
                        catch (OperationCanceledException) when (!operation.IsCancellationRequested)
                        {
                            throw new TimeoutException("Remote monitoring sample timed out.");
                        }

                        if (!ReferenceEquals(state.MonitoringOperation, operation))
                        {
                            return;
                        }
                        state.PreviousMonitoringSnapshot = state.MonitoringSnapshot;
                        state.MonitoringSnapshot = snapshot;
                        state.MonitoringError = null;
                        if (string.IsNullOrWhiteSpace(state.SelectedNetworkInterface) ||
                            snapshot.NetworkInterfaces.All(item => item.Name != state.SelectedNetworkInterface))
                        {
                            state.SelectedNetworkInterface = SelectDefaultNetworkInterface(snapshot);
                        }
                        if (string.IsNullOrWhiteSpace(state.SelectedDiskMount) ||
                            snapshot.Disks.All(item => item.MountPoint != state.SelectedDiskMount))
                        {
                            state.SelectedDiskMount = snapshot.Disks.FirstOrDefault(item => item.MountPoint == "/")?.MountPoint ??
                                                      snapshot.Disks.FirstOrDefault()?.MountPoint;
                        }
                        if (_activeTerminal == state)
                        {
                            UpdateMonitoringView(state);
                        }
                        await Task.Delay(TimeSpan.FromSeconds(2), operation.Token);
                    }
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    if (!string.Equals(state.LastLoggedMonitoringError, error.Message, StringComparison.Ordinal))
                    {
                        DiagnosticLog.Write($"RemoteMonitoring:{state.Profile.Name}", error);
                        state.LastLoggedMonitoringError = error.Message;
                    }
                    state.MonitoringError = error.Message;
                    if (_activeTerminal == state && ReferenceEquals(state.MonitoringOperation, operation))
                    {
                        UpdateMonitoringView(state);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), operation.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when changing tabs, closing the terminal, or disconnecting SSH.
        }
        finally
        {
            if (ReferenceEquals(state.MonitoringOperation, operation))
            {
                state.MonitoringOperation = null;
            }
            operation.Dispose();
        }
    }

    private void UpdateMonitoringView(TerminalViewState? state)
    {
        if (state is null || _activeTerminal != state || state.HostState != TerminalHostState.Connected)
        {
            RemoteMonitoringPanel.Visibility = Visibility.Collapsed;
            return;
        }

        RemoteMonitoringPanel.Visibility = Visibility.Visible;
        var snapshot = state.MonitoringSnapshot;
        if (snapshot is null)
        {
            MonitorMetricsPanel.Visibility = Visibility.Collapsed;
            GpuMonitorRow.Visibility = Visibility.Collapsed;
            MonitoringStatusText.Visibility = Visibility.Visible;
            MonitoringStatusText.Text = LocalizationService.Get(
                state.MonitoringError is null ? "MonitorConnecting" : "MonitorUnavailable");
            MonitoringStatusText.ToolTip = state.MonitoringError;
            return;
        }

        MonitoringStatusText.Visibility = Visibility.Collapsed;
        MonitoringStatusText.ToolTip = null;
        MonitorMetricsPanel.Visibility = Visibility.Visible;
        var previous = state.PreviousMonitoringSnapshot;
        var cpu = previous is null ? 0 : RemoteMonitoringService.CalculateCpuUtilization(previous, snapshot);
        var memoryUsed = Math.Max(0, snapshot.MemoryTotalBytes - snapshot.MemoryAvailableBytes);
        var memoryPercent = 100d * memoryUsed / snapshot.MemoryTotalBytes;
        MonitorCpuText.Text = previous is null ? "—" : $"{cpu:0}%";
        MonitorMemoryText.Text = $"{FormatBytes(memoryUsed)}/{FormatBytes(snapshot.MemoryTotalBytes)}  {memoryPercent:0}%";

        var interfaceNames = snapshot.NetworkInterfaces.Select(item => item.Name).ToArray();
        _updatingNetworkInterface = true;
        try
        {
            NetworkInterfaceInput.ItemsSource = interfaceNames;
            NetworkInterfaceInput.SelectedItem = state.SelectedNetworkInterface;
        }
        finally
        {
            _updatingNetworkInterface = false;
        }
        var rate = previous is null || state.SelectedNetworkInterface is null
            ? (ReceivedBytesPerSecond: 0d, TransmittedBytesPerSecond: 0d)
            : RemoteMonitoringService.CalculateNetworkRate(
                previous, snapshot, state.SelectedNetworkInterface);
        MonitorNetworkText.Text = $"↓ {FormatRate(rate.ReceivedBytesPerSecond)}  ↑ {FormatRate(rate.TransmittedBytesPerSecond)}";

        var diskMounts = snapshot.Disks.Select(item => item.MountPoint).ToArray();
        _updatingDiskMount = true;
        try
        {
            DiskMountInput.ItemsSource = diskMounts;
            DiskMountInput.SelectedItem = state.SelectedDiskMount;
        }
        finally
        {
            _updatingDiskMount = false;
        }
        var selectedDisk = snapshot.Disks.FirstOrDefault(item => item.MountPoint == state.SelectedDiskMount);
        var diskTotal = selectedDisk?.TotalBytes ?? snapshot.DiskTotalBytes;
        var diskAvailable = selectedDisk?.AvailableBytes ?? snapshot.DiskAvailableBytes;
        var diskUsed = Math.Max(0, diskTotal - diskAvailable);
        var diskPercent = diskTotal > 0 ? 100d * diskUsed / diskTotal : 0;
        MonitorDiskText.Text = $"{FormatBytes(diskUsed)}/{FormatBytes(diskTotal)}  {diskPercent:0}%";

        var gpuItems = snapshot.Gpus
            .OrderBy(gpu => gpu.Index)
            .Select(gpu => $"G{gpu.Index}  {gpu.CoreUtilizationPercent}%  {FormatGpuMemory(gpu.MemoryUsedBytes)}/{FormatGpuMemory(gpu.MemoryTotalBytes)}G")
            .ToArray();
        GpuMonitorItems.ItemsSource = gpuItems;
        GpuMonitorRow.Visibility = gpuItems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void NetworkInterfaceInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingNetworkInterface || _activeTerminal is null ||
            NetworkInterfaceInput.SelectedItem is not string interfaceName)
        {
            return;
        }
        _activeTerminal.SelectedNetworkInterface = interfaceName;
        UpdateMonitoringView(_activeTerminal);
    }

    private void DiskMountInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingDiskMount || _activeTerminal is null ||
            DiskMountInput.SelectedItem is not string mountPoint)
        {
            return;
        }
        _activeTerminal.SelectedDiskMount = mountPoint;
        UpdateMonitoringView(_activeTerminal);
    }

    private static string? SelectDefaultNetworkInterface(RemoteMonitoringSnapshot snapshot) =>
        snapshot.NetworkInterfaces.FirstOrDefault(item => item.Name == snapshot.DefaultNetworkInterface)?.Name ??
        snapshot.NetworkInterfaces.FirstOrDefault(item => item.IsUp && item.Name != "lo")?.Name ??
        snapshot.NetworkInterfaces.FirstOrDefault(item => item.IsUp)?.Name ??
        snapshot.NetworkInterfaces.FirstOrDefault()?.Name;

    private async Task LoadRemoteDirectoryAsync(TerminalViewState state, string path)
    {
        if (!_terminals.Contains(state) || state.HostState != TerminalHostState.Connected)
        {
            return;
        }

        state.BrowserOperation?.Cancel();
        state.BrowserOperation?.Dispose();
        var operation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        state.BrowserOperation = operation;
        var generation = ++state.BrowserGeneration;

        if (_activeTerminal == state)
        {
            BrowserStatusText.Text = LocalizationService.Get("ConnectingDirectory");
            BrowserStatusText.Foreground = (Brush)FindResource("MutedTextBrush");
            UpdateBrowserControls(state);
        }

        try
        {
            var service = new RemoteFileService();
            var listing = await service.ListAsync(
                state.Profile,
                state.Surface.Authentication,
                _hostKeyVerifier.Verify,
                path,
                state.Route,
                operation.Token);
            if (!ReferenceEquals(state.BrowserOperation, operation) ||
                state.BrowserGeneration != generation ||
                state.HostState != TerminalHostState.Connected ||
                !_terminals.Contains(state))
            {
                return;
            }
            operation.Token.ThrowIfCancellationRequested();
            var entries = new List<RemoteFileEntry>(listing.Entries.Count + 1);
            if (!string.Equals(listing.Path, "/", StringComparison.Ordinal))
            {
                entries.Add(new RemoteFileEntry
                {
                    Name = "..",
                    Path = ParentRemotePath(listing.Path),
                    IsDirectory = true,
                    IsParent = true,
                });
            }
            entries.AddRange(listing.Entries);
            state.RemoteItems = new ObservableCollection<RemoteFileEntry>(entries);
            state.RemotePath = listing.Path;
            if (!state.RemotePathHistory.Contains(listing.Path, StringComparer.Ordinal))
            {
                state.RemotePathHistory.Insert(0, listing.Path);
            }
            state.BrowserLoaded = true;
            state.BrowserListingTruncated = listing.IsTruncated;
            state.BrowserEntryLimit = listing.EntryLimit;
            if (_activeTerminal == state)
            {
                RemoteFileList.ItemsSource = state.RemoteItems;
                RemotePathBox.ItemsSource = state.RemotePathHistory;
                RemotePathBox.Text = listing.Path;
                ActivePathStatusText.Text = listing.Path;
                UpdateBrowserSummary(state);
                UpdateBrowserControls(state);
            }
        }
        catch (OperationCanceledException)
        {
            if (_activeTerminal == state && ReferenceEquals(state.BrowserOperation, operation))
            {
                BrowserStatusText.Text = LocalizationService.Get("DirectoryTimeout");
                BrowserStatusText.Foreground = Brushes.Firebrick;
            }
        }
        catch (Exception ex)
        {
            if (_activeTerminal == state && ReferenceEquals(state.BrowserOperation, operation))
            {
                BrowserStatusText.Text = LocalizationService.Format("Disconnected", ex.Message);
                BrowserStatusText.Foreground = Brushes.Firebrick;
            }
        }
        finally
        {
            if (ReferenceEquals(state.BrowserOperation, operation))
            {
                state.BrowserOperation = null;
                operation.Dispose();
                if (_activeTerminal == state)
                {
                    UpdateBrowserControls(state);
                }
            }
        }
    }

    private void DisconnectBrowser(TerminalViewState state)
    {
        state.BrowserGeneration++;
        var operation = state.BrowserOperation;
        state.BrowserOperation = null;
        operation?.Cancel();
        operation?.Dispose();
        state.BrowserLoaded = false;
        state.BrowserListingTruncated = false;
        state.BrowserEntryLimit = 0;
        state.RemoteItems = [];

        if (_activeTerminal != state)
        {
            return;
        }

        RemoteFileList.ItemsSource = state.RemoteItems;
        RemotePathBox.ItemsSource = state.RemotePathHistory;
        RemotePathBox.Text = state.RemotePath;
        ActivePathStatusText.Text = state.RemotePath;
        BrowserItemCountText.Text = LocalizationService.Format("ItemsCount", 0);
        UpdateBrowserControls(state);
    }

    private void UpdateBrowserSummary(TerminalViewState state)
    {
        var itemCount = state.RemoteItems.Count(entry => !entry.IsParent);
        if (state.BrowserListingTruncated)
        {
            var limit = state.BrowserEntryLimit > 0 ? state.BrowserEntryLimit : itemCount;
            BrowserStatusText.Text = LocalizationService.Format("ListingCapped", limit);
            BrowserStatusText.Foreground = Brushes.DarkGoldenrod;
            BrowserItemCountText.Text = LocalizationService.Format("ItemsCountCapped", itemCount);
            return;
        }

        BrowserStatusText.Text = state.Profile.DisplayEndpoint;
        BrowserStatusText.Foreground = (Brush)FindResource("MutedTextBrush");
        BrowserItemCountText.Text = LocalizationService.Format("ItemsCount", itemCount);
    }

    private async void RemoteFileList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_activeTerminal is not null && RemoteFileList.SelectedItem is RemoteFileEntry { IsDirectory: true } entry)
        {
            await LoadRemoteDirectoryAsync(_activeTerminal, entry.Path);
        }
    }

    private void RemoteFileList_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }
        if (ItemsControl.ContainerFromElement(RemoteFileList, source) is not ListViewItem item)
        {
            return;
        }
        if (!item.IsSelected)
        {
            RemoteFileList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        item.Focus();
    }

    private void RemoteFileContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRemoteEntries();
        var actionable = selected.Where(entry => !entry.IsParent).ToArray();
        var connected = _activeTerminal is { HostState: TerminalHostState.Connected } terminal &&
                        CanTransferInDirectory(terminal) && terminal.BrowserOperation is null;
        CopyRemotePathMenuItem.IsEnabled = selected.Length > 0;
        RenameRemoteItemMenuItem.IsEnabled = connected && actionable.Length == 1 && selected.Length == 1;
        DeleteRemoteItemsMenuItem.IsEnabled = connected && actionable.Length > 0 && actionable.Length == selected.Length;
    }

    private void CopyRemotePathMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var paths = SelectedRemoteEntries().Select(entry => entry.Path).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
            SetConnectionStatus(LocalizationService.Format("RemotePathsCopied", paths.Length));
        }
        catch (Exception error)
        {
            ShowRemoteOperationError(_activeTerminal, error);
        }
    }

    private async void RenameRemoteItemMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var terminal = _activeTerminal;
        var selected = SelectedRemoteEntries();
        if (terminal is null || selected is not [var entry] || entry.IsParent ||
            terminal.HostState != TerminalHostState.Connected)
        {
            return;
        }

        var dialog = new RenameRemoteItemDialog(entry.Name) { Owner = this };
        if (dialog.ShowDialog() != true || string.Equals(dialog.NewName, entry.Name, StringComparison.Ordinal))
        {
            return;
        }

        var service = new RemoteFileService();
        await RunRemoteMutationAsync(
            terminal,
            token => service.RenameAsync(
                terminal.Profile,
                terminal.Surface.Authentication,
                _hostKeyVerifier.Verify,
                entry.Path,
                dialog.NewName,
                terminal.Route,
                token),
            LocalizationService.Format("RemoteRenameCompleted", entry.Name, dialog.NewName));
    }

    private async void DeleteRemoteItemsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var terminal = _activeTerminal;
        var selected = SelectedRemoteEntries().Where(entry => !entry.IsParent).ToArray();
        if (terminal is null || selected.Length == 0 || terminal.HostState != TerminalHostState.Connected)
        {
            return;
        }

        var visibleNames = selected.Take(12).Select(entry => "• " + entry.Name).ToList();
        if (selected.Length > visibleNames.Count)
        {
            visibleNames.Add(LocalizationService.Format("MoreConflicts", selected.Length - visibleNames.Count));
        }
        if (MessageBox.Show(
                this,
                LocalizationService.Format("DeleteRemoteItemsPrompt", string.Join(Environment.NewLine, visibleNames)),
                LocalizationService.Get("DeleteRemoteItemsTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        var service = new RemoteFileService();
        await RunRemoteMutationAsync(
            terminal,
            token => service.DeleteAsync(
                terminal.Profile,
                terminal.Surface.Authentication,
                _hostKeyVerifier.Verify,
                selected.Select(entry => entry.Path).ToArray(),
                terminal.Route,
                token),
            LocalizationService.Format("RemoteDeleteCompleted", selected.Length));
    }

    private RemoteFileEntry[] SelectedRemoteEntries() =>
        RemoteFileList.SelectedItems.Cast<RemoteFileEntry>().ToArray();

    private async Task RunRemoteMutationAsync(
        TerminalViewState terminal,
        Func<CancellationToken, Task> operationBody,
        string successMessage)
    {
        if (!_terminals.Contains(terminal) || terminal.HostState != TerminalHostState.Connected ||
            terminal.BrowserOperation is not null)
        {
            return;
        }

        var operation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        terminal.BrowserOperation = operation;
        if (_activeTerminal == terminal)
        {
            BrowserStatusText.Text = LocalizationService.Get("RemoteOperationRunning");
            BrowserStatusText.Foreground = (Brush)FindResource("MutedTextBrush");
            UpdateBrowserControls(terminal);
        }

        var succeeded = false;
        try
        {
            await operationBody(operation.Token);
            succeeded = true;
            AppendTransferLog($"[{DateTime.Now:T}] [{terminal.Profile.Name}] {successMessage}");
            SetConnectionStatus(successMessage);
        }
        catch (OperationCanceledException) when (terminal.HostState != TerminalHostState.Connected)
        {
            // DisconnectBrowser owns the cancellation and already updates the browser UI.
        }
        catch (Exception error)
        {
            ShowRemoteOperationError(terminal, error);
        }
        finally
        {
            if (ReferenceEquals(terminal.BrowserOperation, operation))
            {
                terminal.BrowserOperation = null;
                operation.Dispose();
                if (_activeTerminal == terminal)
                {
                    UpdateBrowserControls(terminal);
                }
            }
        }

        if (succeeded && _terminals.Contains(terminal) &&
            terminal.HostState == TerminalHostState.Connected && terminal.BrowserOperation is null)
        {
            await LoadRemoteDirectoryAsync(terminal, terminal.RemotePath);
        }
    }

    private void ShowRemoteOperationError(TerminalViewState? terminal, Exception error)
    {
        var message = LocalizationService.Format("RemoteOperationFailed", error.Message);
        var sessionName = terminal?.Profile.Name ?? LocalizationService.Get("RemoteFileOperation");
        AppendTransferLog($"[{DateTime.Now:T}] [{sessionName}] {message}");
        SetConnectionStatus(message, isError: true);
        MessageBox.Show(
            this,
            LocalizationService.Format("RemoteOperationFailedWithLog", error.Message),
            LocalizationService.Get("RemoteFileOperation"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void ParentDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var terminal = _activeTerminal;
        if (terminal is null || !CanTransferInDirectory(terminal))
        {
            SetConnectionStatus(LocalizationService.Get("BrowserAbsolutePathRequired"), isError: true);
            return;
        }

        await LoadRemoteDirectoryAsync(terminal, ParentRemotePath(terminal.RemotePath));
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeTerminal is not null)
        {
            await LoadRemoteDirectoryAsync(_activeTerminal, _activeTerminal.RemotePath);
        }
    }

    private async void RemotePathBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _activeTerminal is not null && !string.IsNullOrWhiteSpace(RemotePathBox.Text))
        {
            e.Handled = true;
            await LoadRemoteDirectoryAsync(_activeTerminal, RemotePathBox.Text.Trim());
        }
    }

    private async void UploadButton_OnClick(object sender, RoutedEventArgs e)
    {
        var terminal = _activeTerminal;
        if (terminal is null || !CanTransferInDirectory(terminal))
        {
            return;
        }

        var destinationDirectory = terminal.RemotePath;
        var authentication = terminal.Surface.Authentication;

        var dialog = new OpenFileDialog { Multiselect = true, Title = LocalizationService.Get("UploadDialogTitle") };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var conflicts = dialog.FileNames
            .Select(file => Path.GetFileName(file)!)
            .Where(name => RemoteNameExists(terminal, name))
            .ToArray();
        if (!ConfirmOverwrite(conflicts, "UploadOverwritePrompt"))
        {
            return;
        }

        var transfers = dialog.FileNames.Select(file =>
            RunTransferAsync(new RsyncTransferRequest
                {
                    Direction = RsyncTransferDirection.Upload,
                    Profile = terminal.Profile,
                    Route = terminal.Route,
                    LocalPath = file,
                    RemotePath = EnsureRemoteDirectory(destinationDirectory),
                    PreservePermissions = false,
                    PreserveLinks = false,
                }, authentication));
        await Task.WhenAll(transfers);

        if (_terminals.Contains(terminal) &&
            terminal.HostState == TerminalHostState.Connected &&
            terminal.BrowserOperation is null &&
            string.Equals(terminal.RemotePath, destinationDirectory, StringComparison.Ordinal))
        {
            await LoadRemoteDirectoryAsync(terminal, destinationDirectory);
        }
    }

    private async void UploadFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        var terminal = _activeTerminal;
        if (terminal is null || !CanTransferInDirectory(terminal))
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = LocalizationService.Get("UploadFolderDialogTitle") };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dialog.FolderName));
        if (!ConfirmOverwrite(
                RemoteNameExists(terminal, folderName) ? [folderName] : [],
                "UploadOverwritePrompt"))
        {
            return;
        }

        var destinationDirectory = terminal.RemotePath;
        await RunTransferAsync(new RsyncTransferRequest
            {
                Direction = RsyncTransferDirection.Upload,
                Profile = terminal.Profile,
                Route = terminal.Route,
                LocalPath = dialog.FolderName,
                RemotePath = EnsureRemoteDirectory(destinationDirectory),
                PreservePermissions = false,
                PreserveLinks = false,
            }, terminal.Surface.Authentication);

        if (_terminals.Contains(terminal) &&
            terminal.HostState == TerminalHostState.Connected &&
            terminal.BrowserOperation is null &&
            string.Equals(terminal.RemotePath, destinationDirectory, StringComparison.Ordinal))
        {
            await LoadRemoteDirectoryAsync(terminal, destinationDirectory);
        }
    }

    private async void DownloadButton_OnClick(object sender, RoutedEventArgs e)
    {
        var terminal = _activeTerminal;
        if (terminal is null || !CanTransferInDirectory(terminal) || RemoteFileList.SelectedItems.Count == 0)
        {
            SetConnectionStatus(LocalizationService.Get("SelectRemoteItems"), isError: true);
            return;
        }

        var dialog = new OpenFolderDialog { Title = LocalizationService.Get("DownloadDialogTitle") };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var selected = RemoteFileList.SelectedItems
            .Cast<RemoteFileEntry>()
            .Where(entry => !entry.IsParent)
            .ToArray();
        if (selected.Length == 0)
        {
            SetConnectionStatus(LocalizationService.Get("NavigationOnly"), isError: true);
            return;
        }

        var downloadConflicts = selected
            .Where(entry =>
            {
                var target = Path.Combine(dialog.FolderName, entry.Name);
                return File.Exists(target) || Directory.Exists(target);
            })
            .Select(entry => entry.Name)
            .ToArray();
        if (!ConfirmOverwrite(downloadConflicts, "DownloadOverwritePrompt"))
        {
            return;
        }

        var transfers = selected.Select(entry =>
            RunTransferAsync(new RsyncTransferRequest
                {
                    Direction = RsyncTransferDirection.Download,
                    Profile = terminal.Profile,
                    Route = terminal.Route,
                    LocalPath = dialog.FolderName + Path.DirectorySeparatorChar,
                    RemotePath = entry.Path,
                    PreservePermissions = false,
                    PreserveLinks = false,
                }, terminal.Surface.Authentication));
        await Task.WhenAll(transfers);
    }

    private static bool RemoteNameExists(TerminalViewState terminal, string? name) =>
        !string.IsNullOrWhiteSpace(name) && terminal.RemoteItems.Any(entry =>
            !entry.IsParent && string.Equals(entry.Name, name, StringComparison.Ordinal));

    private bool ConfirmOverwrite(IReadOnlyCollection<string> conflicts, string messageKey)
    {
        if (conflicts.Count == 0)
        {
            return true;
        }

        var visibleNames = conflicts.Take(12).Select(name => "• " + name).ToList();
        if (conflicts.Count > visibleNames.Count)
        {
            visibleNames.Add(LocalizationService.Format(
                "MoreConflicts",
                conflicts.Count - visibleNames.Count));
        }
        var names = string.Join(Environment.NewLine, visibleNames);
        return MessageBox.Show(
                   this,
                   LocalizationService.Format(messageKey, names),
                   LocalizationService.Get("ConfirmOverwriteTitle"),
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private async Task<bool> RunTransferAsync(
        RsyncTransferRequest request,
        SshAuthenticationOptions authentication)
    {
        var worker = _toolLocator.FindRsyncWorker();
        if (worker is null)
        {
            SetConnectionStatus(LocalizationService.Get("WorkerMissing"), isError: true);
            ShowSidebar(SidebarView.Transfers);
            return false;
        }

        ShowSidebar(SidebarView.Transfers);
        var transferNumber = _nextTransferNumber++;
        var operation = new CancellationTokenSource();
        _transferOperations.Add(transferNumber, operation);
        CancelTransferButton.IsEnabled = true;
        var service = new RsyncWorkerTransferService(worker);
        service.EventReceived += (_, transferEvent) =>
            Dispatcher.InvokeAsync(() => AppendTransferEvent(transferNumber, request.Profile.Name, transferEvent));
        AppendTransferLog($"[{DateTime.Now:T}] [#{transferNumber} {request.Profile.Name}] {request.Direction}: {request.LocalPath} ↔ {request.Profile.DisplayEndpoint}:{request.RemotePath}");
        try
        {
            SetConnectionStatus(LocalizationService.Format("TransfersActive", _transferOperations.Count));
            await service.TransferAsync(request, authentication, operation.Token);
            AppendTransferLog($"[#{transferNumber} {request.Profile.Name}] {LocalizationService.Get("LogTransferCompleted")}");
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendTransferLog($"[#{transferNumber} {request.Profile.Name}] {LocalizationService.Get("LogTransferCancelled")}");
            return false;
        }
        catch (Exception ex)
        {
            AppendTransferLog($"[#{transferNumber} {request.Profile.Name}] {LocalizationService.Format("LogTransferFailed", ex.Message)}");
            SetConnectionStatus(LocalizationService.Format("TransferFailed", ex.Message), isError: true);
            return false;
        }
        finally
        {
            _transferOperations.Remove(transferNumber);
            operation.Dispose();
            CancelTransferButton.IsEnabled = _transferOperations.Count > 0;
            if (_transferOperations.Count > 0)
            {
                SetConnectionStatus(LocalizationService.Format("TransfersActive", _transferOperations.Count));
            }
            else
            {
                SetConnectionStatus(LocalizationService.Get("TransfersIdle"));
            }
        }
    }

    private void AppendTransferEvent(int transferNumber, string profileName, RsyncWorkerEvent transferEvent)
    {
        var prefix = $"[#{transferNumber} {profileName}] ";
        switch (transferEvent.Type)
        {
            case "progress":
                AppendTransferLog(prefix + LocalizationService.Format(
                    "ProtocolIo",
                    FormatBytes(transferEvent.ProtocolReadBytes),
                    FormatBytes(transferEvent.ProtocolWrittenBytes)));
                break;
            case "state" when !string.IsNullOrWhiteSpace(transferEvent.State):
                AppendTransferLog(prefix + LocalizationService.Format("TransferState", transferEvent.State));
                break;
            case "log" when !string.IsNullOrWhiteSpace(transferEvent.Message):
                AppendTransferLog(prefix + transferEvent.Message);
                break;
        }
    }

    private void CancelTransferButton_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var operation in _transferOperations.Values.ToArray())
        {
            operation.Cancel();
        }
    }

    private void CopyTransferLogButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TransferLogTextBox.Text))
        {
            Clipboard.SetText(TransferLogTextBox.Text);
        }
    }

    private void AppendTransferLog(string line)
    {
        _transferLogLines.Enqueue(line);
        while (_transferLogLines.Count > 500)
        {
            _transferLogLines.Dequeue();
        }

        TransferLogTextBox.Text = string.Join(Environment.NewLine, _transferLogLines);
        TransferLogTextBox.CaretIndex = TransferLogTextBox.Text.Length;
        TransferLogTextBox.ScrollToEnd();
        CopyTransferLogButton.IsEnabled = true;
    }

    private void SessionsRailButton_OnClick(object sender, RoutedEventArgs e)
    {
        _sidebarLockedToSessions = true;
        ShowSidebar(SidebarView.Sessions);
    }

    private void BrowserRailButton_OnClick(object sender, RoutedEventArgs e)
    {
        _sidebarLockedToSessions = false;
        ShowSidebar(SidebarView.Browser);
    }

    private void TransfersRailButton_OnClick(object sender, RoutedEventArgs e) =>
        ShowSidebar(SidebarView.Transfers);

    private void ShowSidebar(SidebarView view)
    {
        SessionsPane.Visibility = view == SidebarView.Sessions ? Visibility.Visible : Visibility.Collapsed;
        BrowserPane.Visibility = view == SidebarView.Browser ? Visibility.Visible : Visibility.Collapsed;
        TransfersPane.Visibility = view == SidebarView.Transfers ? Visibility.Visible : Visibility.Collapsed;
        SessionsRailButton.IsChecked = view == SidebarView.Sessions;
        BrowserRailButton.IsChecked = view == SidebarView.Browser;
        TransfersRailButton.IsChecked = view == SidebarView.Transfers;
    }

    private void HomeTabButton_OnClick(object sender, RoutedEventArgs e) => ShowHome();

    private void CloseSessionMenu_OnClick(object sender, RoutedEventArgs e)
    {
        if (_activeTerminal is not null)
        {
            CloseTerminal(_activeTerminal);
        }
    }

    private void ExitMenu_OnClick(object sender, RoutedEventArgs e) => Close();

    private void EnglishLanguageMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        LocalizationService.SetLanguage("en");

    private void ChineseLanguageMenuItem_OnClick(object sender, RoutedEventArgs e) =>
        LocalizationService.SetLanguage("zh-CN");

    private void MiddleMousePasteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        LocalizationService.SetMousePasteButton(TerminalMousePasteButton.Middle);
        UpdateMousePasteMenu();
    }

    private void RightMousePasteMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        LocalizationService.SetMousePasteButton(TerminalMousePasteButton.Right);
        UpdateMousePasteMenu();
    }

    private async void ExportSettingsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get("SelectSettingsExportFolder"),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var exportPath = SettingsTransferService.GetExportPath(dialog.FolderName);
        if (File.Exists(exportPath) && MessageBox.Show(
                this,
                LocalizationService.Format("OverwriteSettingsExport", exportPath),
                LocalizationService.Get("ExportSettings"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await SettingsTransferService.ExportAsync(dialog.FolderName, _profiles);
            MessageBox.Show(
                this,
                LocalizationService.Format("SettingsExported", exportPath),
                LocalizationService.Get("ExportSettings"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("SettingsTransferFailed", ex.Message),
                LocalizationService.Get("ExportSettings"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void ImportSettingsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = LocalizationService.Get("SelectSettingsImportFolder"),
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var importPath = SettingsTransferService.GetExportPath(dialog.FolderName);
        if (!File.Exists(importPath))
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("SettingsImportFileMissing", importPath),
                LocalizationService.Get("ImportSettings"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var package = await SettingsTransferService.ImportAsync(dialog.FolderName);
            if (MessageBox.Show(
                    this,
                    LocalizationService.Format("ConfirmSettingsImport", package.Sessions.Length),
                    LocalizationService.Get("ImportSettings"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            var previousProfiles = _profiles.ToArray();
            _profiles.Clear();
            _profiles.AddRange(package.Sessions);
            if (!await SaveSessionsAsync())
            {
                _profiles.Clear();
                _profiles.AddRange(previousProfiles);
                return;
            }

            LocalizationService.SetMousePasteButton(
                LocalizationService.ParseMousePasteButton(package.MousePasteButton));
            LocalizationService.SetKeywordHighlightingEnabled(package.KeywordHighlightingEnabled);
            LocalizationService.SetKeywordHighlightingRules(TerminalKeywordRules.CreateNormalized(
                package.KeywordGreen,
                package.KeywordRed,
                package.KeywordYellow));
            LocalizationService.SetLanguage(
                LocalizationService.SupportedLanguages.Contains(package.Language, StringComparer.Ordinal)
                    ? package.Language
                    : "en");
            RebuildSessionTree();
            UpdateLanguageMenu();
            UpdateMousePasteMenu();
            UpdateKeywordHighlightingMenu();
            MessageBox.Show(
                this,
                LocalizationService.Format("SettingsImported", package.Sessions.Length),
                LocalizationService.Get("ImportSettings"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                LocalizationService.Format("SettingsTransferFailed", ex.Message),
                LocalizationService.Get("ImportSettings"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void KeywordHighlightingEnabledMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        LocalizationService.SetKeywordHighlightingEnabled(KeywordHighlightingEnabledMenuItem.IsChecked);
        UpdateKeywordHighlightingMenu();
    }

    private void CustomizeKeywordsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new KeywordHighlightingDialog(LocalizationService.KeywordHighlightingRules)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() == true && dialog.Rules is not null)
        {
            LocalizationService.SetKeywordHighlightingRules(dialog.Rules);
        }
    }

    private void LocalizationService_OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateLanguageMenu();
        RebuildSessionTree();
        UpdateToolStatus();
        foreach (var terminal in _terminals)
        {
            if (terminal.TabButton?.Content is DockPanel panel)
            {
                var close = panel.Children.OfType<Button>().FirstOrDefault();
                if (close is not null)
                {
                    close.ToolTip = LocalizationService.Get("CloseSession");
                }
            }
        }

        if (_activeTerminal is null)
        {
            ActiveUserText.Text = LocalizationService.Get("Local");
            SetConnectionStatus(LocalizationService.Get("Ready"));
        }
        else
        {
            SetConnectionStatus(LocalizationService.Format("ActiveSession", _activeTerminal.Profile.Name));
            if (_activeTerminal.BrowserLoaded)
            {
                UpdateBrowserSummary(_activeTerminal);
            }
            else
            {
                var disconnected = _activeTerminal.HostState is TerminalHostState.Exited or TerminalHostState.Failed;
                BrowserStatusText.Text = LocalizationService.Get(disconnected ? "BrowserDisconnected" : "WaitingSsh");
                BrowserStatusText.Foreground = _activeTerminal.HostState == TerminalHostState.Failed
                    ? Brushes.Firebrick
                    : (Brush)FindResource("MutedTextBrush");
                BrowserItemCountText.Text = LocalizationService.Format("ItemsCount", 0);
            }
        }
        UpdateMonitoringView(_activeTerminal);
    }

    private void UpdateLanguageMenu()
    {
        EnglishLanguageMenuItem.IsChecked = LocalizationService.CurrentLanguage == "en";
        ChineseLanguageMenuItem.IsChecked = LocalizationService.CurrentLanguage == "zh-CN";
    }

    private void UpdateMousePasteMenu()
    {
        MiddleMousePasteMenuItem.IsChecked = LocalizationService.MousePasteButton == TerminalMousePasteButton.Middle;
        RightMousePasteMenuItem.IsChecked = LocalizationService.MousePasteButton == TerminalMousePasteButton.Right;
    }

    private void UpdateKeywordHighlightingMenu() =>
        KeywordHighlightingEnabledMenuItem.IsChecked = LocalizationService.KeywordHighlightingEnabled;

    private void AboutMenu_OnClick(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            this,
            "ssh-win-gui\n\n" + LocalizationService.Get("AboutBody"),
            LocalizationService.Get("AboutRsyncShell"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void AddTabButton_OnClick(object sender, RoutedEventArgs e)
    {
        QuickConnectBox.Focus();
        QuickConnectBox.SelectAll();
    }

    private void SetConnectionStatus(string message, bool isError = false)
    {
        ConnectionStatusText.Text = message;
        ConnectionStatusText.Foreground = isError ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(69, 81, 94));
    }

    private void UpdateBrowserControls(TerminalViewState? state)
    {
        var isActive = state is not null && ReferenceEquals(_activeTerminal, state);
        var connected = isActive && state!.HostState == TerminalHostState.Connected;
        var canTransfer = connected && CanTransferInDirectory(state!);
        RefreshButton.IsEnabled = connected && state!.BrowserOperation is null;
        RemotePathBox.IsEnabled = connected;
        ParentDirectoryButton.IsEnabled = canTransfer;
        UploadButton.IsEnabled = canTransfer;
        UploadFolderButton.IsEnabled = canTransfer;
        DownloadButton.IsEnabled = canTransfer;
        UploadMenuItem.IsEnabled = canTransfer;
        UploadFolderMenuItem.IsEnabled = canTransfer;
        DownloadMenuItem.IsEnabled = canTransfer;
    }

    private static bool CanTransferInDirectory(TerminalViewState state) =>
        state.BrowserLoaded &&
        state.RemotePath.StartsWith("/", StringComparison.Ordinal) &&
        !state.RemotePath.Contains('\0');

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        LocalizationService.LanguageChanged -= LocalizationService_OnLanguageChanged;
        _tunnelService.Dispose();
        foreach (var operation in _transferOperations.Values.ToArray())
        {
            operation.Cancel();
        }
        foreach (var terminal in _terminals.ToArray())
        {
            StopMonitoring(terminal);
            terminal.BrowserOperation?.Cancel();
            terminal.Surface.Dispose();
        }
    }

    private static string ParentRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        var normalized = path.TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "/" : normalized[..slash];
    }

    private static string EnsureRemoteDirectory(string path) => path.EndsWith('/') ? path : path + "/";

    private delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        KeyboardHookProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = (double)Math.Max(0, value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value} B" : $"{size:0.0} {units[unit]}";
    }

    private static string FormatRate(double bytesPerSecond) =>
        FormatBytes((long)Math.Max(0, bytesPerSecond)) + "/s";

    private static string FormatGpuMemory(long bytes) =>
        (Math.Max(0, bytes) / (1024d * 1024d * 1024d)).ToString("0.0");

    private enum SidebarView
    {
        Sessions,
        Browser,
        Transfers,
    }

    private sealed class TerminalViewState
    {
        public required int Number { get; init; }
        public required ConnectionProfile Profile { get; init; }
        public required IReadOnlyList<ConnectionProfile> Route { get; init; }
        public required ITerminalSurface Surface { get; init; }
        public required FrameworkElement View { get; init; }
        public ToggleButton? TabButton { get; set; }
        public string RemotePath { get; set; } = "~";
        public bool BrowserLoaded { get; set; }
        public TerminalHostState HostState { get; set; } = TerminalHostState.Starting;
        public CancellationTokenSource? BrowserOperation { get; set; }
        public int BrowserGeneration { get; set; }
        public ObservableCollection<RemoteFileEntry> RemoteItems { get; set; } = [];
        public ObservableCollection<string> RemotePathHistory { get; } = ["~"];
        public bool BrowserListingTruncated { get; set; }
        public int BrowserEntryLimit { get; set; }
        public CancellationTokenSource? MonitoringOperation { get; set; }
        public RemoteMonitoringSnapshot? PreviousMonitoringSnapshot { get; set; }
        public RemoteMonitoringSnapshot? MonitoringSnapshot { get; set; }
        public string? SelectedNetworkInterface { get; set; }
        public string? SelectedDiskMount { get; set; }
        public string? MonitoringError { get; set; }
        public string? LastLoggedMonitoringError { get; set; }
    }
}
