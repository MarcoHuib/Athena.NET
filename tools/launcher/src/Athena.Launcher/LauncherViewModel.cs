using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Athena.Net.Launcher.Core;

namespace Athena.Net.Launcher;

public sealed class LauncherViewModel : INotifyPropertyChanged
{
    private readonly LauncherCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private string _status = "Ready to update and play";
    private bool _busy;
    private string _officialEndpoint = "Resolved after update";
    private string _proxyStatus = "Pending";
    public event PropertyChangedEventHandler? PropertyChanged;

    public string AthenaEndpoint { get; }
    public string OfficialEndpoint { get => _officialEndpoint; private set { _officialEndpoint = value; Changed(); } }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string ProxyStatus { get => _proxyStatus; private set { _proxyStatus = value; Changed(); } }
    public bool Busy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(CanStart)); } }
    public bool CanStart => !Busy;
    public bool CanClose { get; private set; } = true;
    public ICommand StartCommand { get; }

    public LauncherViewModel(LauncherCoordinator coordinator, LauncherOptions options)
    {
        _coordinator = coordinator;
        AthenaEndpoint = $"{options.AthenaHost}  ·  {options.LoginTargetPort} / {options.CharacterTargetPort} / {options.MapTargetPort}";
        _coordinator.StateChanged += (_, state) => Application.Current.Dispatcher.Invoke(() => ApplyState(state));
        StartCommand = new AsyncCommand(StartAsync, () => CanStart);
    }

    private async Task StartAsync()
    {
        Busy = true;
        CanClose = false;
        try
        {
            await _coordinator.RunAsync(_lifetime.Token);
            if (_coordinator.OfficialLoginEndpoint is { } endpoint) OfficialEndpoint = $"{endpoint.Host}:{endpoint.Port}";
        }
        catch (OperationCanceledException) { Status = "Cancelled safely"; }
        catch (Exception ex)
        {
            Status = "Could not start Ragnarok";
            MessageBox.Show(ex.Message, "Athena.NET Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy = false; CanClose = true; }
    }

    public async Task ShutdownAsync()
    {
        CanClose = false;
        _lifetime.Cancel();
        await _coordinator.ShutdownAsync();
        CanClose = true;
    }

    private void ApplyState(LauncherState state)
    {
        Status = state switch
        {
            LauncherState.Updating => "Starting official updater...",
            LauncherState.ValidatingClient => "Validating Ragnarok installation...",
            LauncherState.ResolvingOfficialEndpoint => "Reading updated service configuration...",
            LauncherState.RecoveringNetworkState => "Recovering previous network state...",
            LauncherState.ConfiguringNetwork => "Preparing network...",
            LauncherState.StartingProxy => "Starting secure TCP tunnels...",
            LauncherState.Ready => "Proxies ready",
            LauncherState.StartingAntiCheat => "Starting Easy Anti-Cheat...",
            LauncherState.WaitingForGame => "Waiting for Ragnarok...",
            LauncherState.Playing => "Ragnarok is running...",
            LauncherState.CleaningUp => "Cleaning up...",
            LauncherState.Faulted => "Launcher faulted; cleaning up...",
            _ => "Ready to update and play",
        };
        ProxyStatus = state switch
        {
            LauncherState.StartingProxy => "Starting",
            LauncherState.Ready or LauncherState.StartingAntiCheat or LauncherState.WaitingForGame or LauncherState.Playing => "Ready",
            LauncherState.CleaningUp => "Stopping",
            LauncherState.Faulted => "Stopped",
            _ => ProxyStatus,
        };
        if (_coordinator.OfficialLoginEndpoint is { } endpoint) OfficialEndpoint = $"{endpoint.Host}:{endpoint.Port}";
    }

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    public AsyncCommand(Func<Task> execute, Func<bool> canExecute) { _execute = execute; _canExecute = canExecute; }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute();
    public async void Execute(object? parameter) { try { await _execute(); } finally { CanExecuteChanged?.Invoke(this, EventArgs.Empty); } }
}
