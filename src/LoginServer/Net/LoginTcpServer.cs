using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Telemetry;

namespace Athena.Net.LoginServer.Net;

public sealed class LoginTcpServer
{
    private readonly LoginConfigStore _configStore;
    private readonly LoginMessageStore _messageStore;
    private readonly Func<Db.LoginDbContext?> _dbFactory;
    private readonly Func<AthenaIdentityDbContext?> _identityDbFactory;
    private readonly CharServerRegistry _charServers;
    private readonly LoginState _state;
    private readonly Config.SubnetConfig _subnetConfig;
    private readonly IServiceProvider _serviceProvider;
    private readonly TcpListener _listener;

    public int BoundPort { get; private set; }

    public LoginTcpServer(
        LoginConfigStore configStore,
        LoginMessageStore messageStore,
        Func<Db.LoginDbContext?> dbFactory,
        Func<AthenaIdentityDbContext?> identityDbFactory,
        CharServerRegistry charServers,
        LoginState state,
        Config.SubnetConfig subnetConfig,
        IServiceProvider serviceProvider
    )
    {
        _configStore = configStore;
        _messageStore = messageStore;
        _dbFactory = dbFactory;
        _identityDbFactory = identityDbFactory;
        _charServers = charServers;
        _state = state;
        _subnetConfig = subnetConfig;
        _serviceProvider = serviceProvider;

        var config = _configStore.Current;
        _listener = new TcpListener(config.BindIp, config.LoginPort);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();

        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        LoginLogger.Status(
            $"Login server listening on {_configStore.Current.BindIp}:{BoundPort}..."
        );

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

        using var activity = LoginTelemetry.ActivitySource.StartActivity(
            "login.client.session",
            ActivityKind.Server
        );

        activity?.SetTag("net.peer.ip", endpoint?.Address.ToString());
        activity?.SetTag("net.peer.port", endpoint?.Port);

        LoginLogger.Info($"Client connected: {endpoint}");

        using (client)
        // A DI scope is created per TCP connection solely to resolve the
        // lightweight per-connection auth services below (never an EF
        // DbContext - those continue to come from short-lived _dbFactory()
        // calls scoped to a single operation, not this connection's lifetime).
        using (var scope = _serviceProvider.CreateScope())
        {
            try
            {
                // ---------------------------------------------------------
                // TEMPORARY iRO PROTOCOL DIAGNOSTIC
                //
                // Peek at the first bytes sent by the client.
                // SocketFlags.Peek means the bytes remain in the socket,
                // so ClientSession will still receive the complete packet.
                // ---------------------------------------------------------
                await LogInitialPacketAsync(client, cancellationToken);

                var playerAuth = scope.ServiceProvider.GetRequiredService<IPlayerAuthenticationService>();
                var serviceAuth = scope.ServiceProvider.GetRequiredService<IServiceAuthenticationService>();

                using var session = new ClientSession(
                    client,
                    _configStore,
                    _messageStore,
                    _dbFactory,
                    _identityDbFactory,
                    _charServers,
                    _state,
                    _subnetConfig,
                    playerAuth,
                    serviceAuth
                );

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
            catch (SocketException ex)
            {
                LoginLogger.Warning(
                    $"Socket error for {endpoint}: {ex.SocketErrorCode} - {ex.Message}"
                );
            }
            catch (Exception ex)
            {
                LoginLogger.Warning($"Client session error for {endpoint}: {ex.Message}");
            }
        }

        LoginLogger.Info($"Client disconnected: {endpoint}");
    }

    private static async Task LogInitialPacketAsync(
        TcpClient client,
        CancellationToken cancellationToken
    )
    {
        const int packetLength = 55;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            // CA_LOGIN (0x0064) is 55 bytes.
            // Wait until the complete packet is available so TCP fragmentation
            // does not give us a misleading partial dump.
            while (client.Available < packetLength)
            {
                await Task.Delay(10, timeoutCts.Token);
            }

            var buffer = new byte[packetLength];

            var bytesPeeked = await client.Client.ReceiveAsync(
                buffer.AsMemory(0, packetLength),
                SocketFlags.Peek,
                timeoutCts.Token
            );

            if (bytesPeeked < packetLength)
            {
                LoginLogger.Warning(
                    $"[iRO DEBUG] Expected {packetLength} bytes, "
                        + $"but only {bytesPeeked} bytes were available."
                );

                return;
            }

            ushort packetId = BitConverter.ToUInt16(buffer, 0);

            uint version = BitConverter.ToUInt32(buffer, 2);

            byte clientType = buffer[54];

            LoginLogger.Info($"[iRO DEBUG] Packet ID : 0x{packetId:X4}");

            LoginLogger.Info($"[iRO DEBUG] Version   : {version}");

            LoginLogger.Info($"[iRO DEBUG] ClientType: {clientType}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LoginLogger.Warning("[iRO DEBUG] Timed out waiting for complete CA_LOGIN packet.");
        }
    }
}
