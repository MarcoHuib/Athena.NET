using System.Reflection;
using System.IO;
using System.Threading;
using System.Windows;
using Athena.Net.Launcher.Core;
using Athena.Net.Launcher.Networking;

namespace Athena.Net.Launcher;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private JsonLineLauncherLog? _log;
    private LauncherViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = @"Global\Athena.NET.Launcher.SingleInstance";
        _mutex = new Mutex(true, mutexName, out var created);
        _ownsMutex = created;
        if (!created)
        {
            MessageBox.Show("Athena.NET Launcher is already running.", "Athena.NET", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        base.OnStartup(e);
        try
        {
            _log = new JsonLineLauncherLog();
            var assembly = Assembly.GetExecutingAssembly();
            var buildVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            _log.Information("launcher.startup", "Athena.NET Launcher starting.", new Dictionary<string, object?> { ["version"] = buildVersion });
            var basePath = AppContext.BaseDirectory;
            var options = LauncherOptions.Load(Path.Combine(basePath, "launcher.settings.json"));
            var ipManager = new WindowsTemporaryIpManager(new PowerShellNetworkCommandRunner(), _log);
            var proxyManager = new TcpProxyManager(_log);
            var coordinator = new LauncherCoordinator(
                options,
                new RagnarokInstallationLocator(_log),
                new RagnarokUpdater(_log),
                new RagnarokInstallationValidator(),
                new RagnarokClientConfigurationReader(new GrfClientDataSourceFactory(), _log),
                new EndpointResolver(),
                ipManager,
                proxyManager,
                new WatchdogLauncher(_log, Path.Combine(basePath, "Athena.Launcher.Watchdog.exe")),
                new EasyAntiCheatLauncher(_log),
                new GameProcessMonitor(_log),
                _log);
            _viewModel = new LauncherViewModel(coordinator, options);
            var window = new MainWindow { DataContext = _viewModel };
            window.Closing += async (_, args) =>
            {
                if (!_viewModel.CanClose)
                {
                    args.Cancel = true;
                    await _viewModel.ShutdownAsync();
                    window.Close();
                }
            };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _log?.Error("launcher.startup.failed", ex, "Launcher startup failed.");
            MessageBox.Show(ex.Message, "Athena.NET Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
