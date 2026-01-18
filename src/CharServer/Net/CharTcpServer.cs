using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Logging;
using Athena.Net.CharServer.Telemetry;

namespace Athena.Net.CharServer.Net;

public sealed class CharTcpServer
{
    private readonly CharConfigStore _configStore;
    private readonly LoginServerConnector _loginConnector;
    private readonly Func<CharDbContext?> _dbFactory;
    private readonly TcpListener _listener;
    private readonly int _startStatusPoints;
    private readonly MapServerRegistry _mapRegistry = new();
    private readonly MapAuthManager _mapAuthManager = new();
    private int _nextSessionId;

    public CharTcpServer(CharConfigStore configStore, LoginServerConnector loginConnector, Func<CharDbContext?> dbFactory, int startStatusPoints)
    {
        _configStore = configStore;
        _loginConnector = loginConnector;
        _dbFactory = dbFactory;
        _startStatusPoints = startStatusPoints;
        var config = _configStore.Current;
        _listener = new TcpListener(config.BindIp, config.CharPort);
    }

    public int BoundPort { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        CharLogger.Status($"Char server listening on {_configStore.Current.BindIp}:{BoundPort}...");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                var sessionId = Interlocked.Increment(ref _nextSessionId);
                _ = HandleClientAsync(sessionId, client, cancellationToken);
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

    private async Task HandleClientAsync(int sessionId, TcpClient client, CancellationToken cancellationToken)
    {
        var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
        CharTelemetry.ConnectionsAccepted.Add(1);
        using var activity = CharTelemetry.ActivitySource.StartActivity("char.client.session", ActivityKind.Server);
        activity?.SetTag("net.peer.ip", endpoint?.Address.ToString());
        activity?.SetTag("net.peer.port", endpoint?.Port);
        CharLogger.Info($"Client connected: {endpoint}");

        using (client)
        using (var session = await CreateSessionAsync(sessionId, client, cancellationToken))
        {
            try
            {
                if (session != null)
                {
                    await session.RunAsync(cancellationToken);
                }
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
                CharLogger.Warning($"Client session error: {ex.Message}");
            }
        }

        CharLogger.Info($"Client disconnected: {endpoint}");
    }

    private async Task<ISession?> CreateSessionAsync(int sessionId, TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header.Length == 0)
        {
            return null;
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header);
        if (packetType == PacketConstants.MapLogin)
        {
            return new MapServerSession(sessionId, client, _configStore, _mapRegistry, _mapAuthManager, header);
        }

        return new ClientSession(sessionId, client, _configStore, _loginConnector, _dbFactory, _startStatusPoints, _mapRegistry, _mapAuthManager, header);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var bytes = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            if (bytes == 0)
            {
                return Array.Empty<byte>();
            }

            read += bytes;
        }

        return buffer;
    }
}
