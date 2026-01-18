using System.Net;
using System.Net.Sockets;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Telemetry;
using System.Diagnostics;

namespace Athena.Net.LoginServer.Net;

public sealed class LoginTcpServer
{
    private readonly LoginConfigStore _configStore;
    private readonly LoginMessageStore _messageStore;
    private readonly Func<Db.LoginDbContext?> _dbFactory;
    private readonly CharServerRegistry _charServers;
    private readonly LoginState _state;
    private readonly Config.SubnetConfig _subnetConfig;
    private readonly TcpListener _listener;

    public int BoundPort { get; private set; }

    public LoginTcpServer(LoginConfigStore configStore, LoginMessageStore messageStore, Func<Db.LoginDbContext?> dbFactory, CharServerRegistry charServers, LoginState state, Config.SubnetConfig subnetConfig)
    {
        _configStore = configStore;
        _messageStore = messageStore;
        _dbFactory = dbFactory;
        _charServers = charServers;
        _state = state;
        _subnetConfig = subnetConfig;
        var config = _configStore.Current;
        _listener = new TcpListener(config.BindIp, config.LoginPort);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LoginLogger.Status("Login server listening...");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
        LoginTelemetry.ConnectionsAccepted.Add(1);
        using var activity = LoginTelemetry.ActivitySource.StartActivity("login.client.session", ActivityKind.Server);
        activity?.SetTag("net.peer.ip", endpoint?.Address.ToString());
        activity?.SetTag("net.peer.port", endpoint?.Port);
        LoginLogger.Info($"Client connected: {endpoint}");

        using (client)
        using (var session = new ClientSession(client, _configStore, _messageStore, _dbFactory, _charServers, _state, _subnetConfig))
        {
            try
            {
                await session.RunAsync(cancellationToken);
            }
            catch (IOException)
            {
                // Client disconnected.
            }
            catch (OperationCanceledException)
            {
                // Server shutdown.
            }
            catch (Exception ex)
            {
                LoginLogger.Warning($"Client session error: {ex.Message}");
            }
        }

        LoginLogger.Info($"Client disconnected: {endpoint}");
    }
}
