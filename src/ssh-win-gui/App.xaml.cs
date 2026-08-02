using System.Configuration;
using System.Data;
using System.Windows;

using System.Windows.Interop;
using System.Windows.Media;
using RsyncShell.App.Services;

namespace RsyncShell.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(argument =>
                string.Equals(argument, "--software-rendering", StringComparison.OrdinalIgnoreCase)))
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        DispatcherUnhandledException += (_, args) =>
            DiagnosticLog.Write("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                DiagnosticLog.Write("AppDomain.UnhandledException", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            DiagnosticLog.Write("TaskScheduler.UnobservedTaskException", args.Exception);

        base.OnStartup(e);
        LocalizationService.Initialize();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        var endpoint = e.Args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            window.Dispatcher.BeginInvoke(() => _ = window.OpenQuickConnectAsync(endpoint));
        }
    }
}

