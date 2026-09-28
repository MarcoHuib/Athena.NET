using System.Buffers.Binary;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

/// <summary>
/// Covers MapServer's client-side of the MapServer &lt;-&gt; CharServer
/// HMAC-SHA256 service authentication handshake (see ai/map-server.md,
/// "Inter-server service authentication") by driving
/// <see cref="CharServerConnector.RunAsync"/> against a fake TCP listener
/// standing in for CharServer, verifying the exact packets MapServer sends
/// and how it reacts to challenge/result responses. Deterministic 32-byte
/// test tokens only - never a real secret.
/// </summary>
public sealed class CharServerConnectorServiceAuthTests
{
    private const int NameLength = 24;
    private const int ServiceNonceLength = 32;
    private const int ServiceProofLength = 32;

    private static readonly string ValidToken = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public async Task Connector_SendsMapServiceHello_ThenProof_OnValidChallenge_AndBecomesReady()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var configStore = new MapConfigStore(
            new MapConfig { CharIp = IPAddress.Loopback, CharPort = port, ServiceId = "TestMapServer", MapIp = IPAddress.Loopback, MapPort = 5121 },
            "unused.conf");
        var tokenProvider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = ValidToken });
        var connector = new CharServerConnector(configStore, tokenProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = connector.RunAsync(cts.Token);

        using var serverSide = await listener.AcceptTcpClientAsync(cts.Token);
        var stream = serverSide.GetStream();

        var hello = await ReadExactAsync(stream, 2 + NameLength + 4 + 2 + 2, cts.Token);
        Assert.Equal(PacketConstants.MapServiceHello, BinaryPrimitives.ReadInt16LittleEndian(hello.AsSpan(0, 2)));
        var serviceId = ReadFixedString(hello.AsSpan(2, NameLength));
        Assert.Equal("TestMapServer", serviceId);

        var nonce = new byte[ServiceNonceLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var challenge = new byte[2 + ServiceNonceLength];
        BinaryPrimitives.WriteInt16LittleEndian(challenge.AsSpan(0, 2), PacketConstants.MapServiceAuthChallenge);
        nonce.CopyTo(challenge, 2);
        await stream.WriteAsync(challenge, cts.Token);

        var proofPacket = await ReadExactAsync(stream, 2 + ServiceProofLength, cts.Token);
        Assert.Equal(PacketConstants.MapServiceAuthProof, BinaryPrimitives.ReadInt16LittleEndian(proofPacket.AsSpan(0, 2)));
        var proof = proofPacket.AsSpan(2, ServiceProofLength).ToArray();

        var expectedProof = ComputeExpectedProof(Convert.FromBase64String(ValidToken), "TestMapServer", IPAddress.Loopback, 5121, nonce);
        Assert.Equal(expectedProof, proof);

        var result = new byte[3];
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(0, 2), PacketConstants.MapServiceAuthResult);
        result[2] = 0;
        await stream.WriteAsync(result, cts.Token);

        await connector.WaitUntilReadyAsync(cts.Token);
        Assert.True(connector.IsConnected);

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Connector_NeverSendsRawTokenBytes_OnlyProof()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var configStore = new MapConfigStore(
            new MapConfig { CharIp = IPAddress.Loopback, CharPort = port, ServiceId = "TestMapServer", MapIp = IPAddress.Loopback, MapPort = 5121 },
            "unused.conf");
        var tokenBytes = Convert.FromBase64String(ValidToken);
        var tokenProvider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = ValidToken });
        var connector = new CharServerConnector(configStore, tokenProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = connector.RunAsync(cts.Token);

        using var serverSide = await listener.AcceptTcpClientAsync(cts.Token);
        var stream = serverSide.GetStream();

        var hello = await ReadExactAsync(stream, 2 + NameLength + 4 + 2 + 2, cts.Token);
        AssertDoesNotContain(hello, tokenBytes);

        var nonce = new byte[ServiceNonceLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var challenge = new byte[2 + ServiceNonceLength];
        BinaryPrimitives.WriteInt16LittleEndian(challenge.AsSpan(0, 2), PacketConstants.MapServiceAuthChallenge);
        nonce.CopyTo(challenge, 2);
        await stream.WriteAsync(challenge, cts.Token);

        var proofPacket = await ReadExactAsync(stream, 2 + ServiceProofLength, cts.Token);
        AssertDoesNotContain(proofPacket, tokenBytes);

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Connector_MissingToken_NeverConnects()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var configStore = new MapConfigStore(
            new MapConfig { CharIp = IPAddress.Loopback, CharPort = port, ServiceId = "TestMapServer", MapIp = IPAddress.Loopback, MapPort = 5121 },
            "unused.conf");
        var tokenProvider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = string.Empty });
        var connector = new CharServerConnector(configStore, tokenProvider);

        using var cts = new CancellationTokenSource();
        var runTask = connector.RunAsync(cts.Token);

        // The connector must never even attempt to connect/authenticate
        // without a configured token - no client connection should reach
        // the listener within a short window.
        var acceptTask = listener.AcceptTcpClientAsync();
        var completed = await Task.WhenAny(acceptTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(acceptTask, completed);
        Assert.False(connector.IsConnected);

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Connector_RejectedResult_DoesNotBecomeReady()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var configStore = new MapConfigStore(
            new MapConfig { CharIp = IPAddress.Loopback, CharPort = port, ServiceId = "TestMapServer", MapIp = IPAddress.Loopback, MapPort = 5121 },
            "unused.conf");
        var tokenProvider = new MapServerServiceTokenProvider(new SecretConfig { MapServerServiceToken = ValidToken });
        var connector = new CharServerConnector(configStore, tokenProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = connector.RunAsync(cts.Token);

        using (var serverSide = await listener.AcceptTcpClientAsync(cts.Token))
        {
            var stream = serverSide.GetStream();
            await ReadExactAsync(stream, 2 + NameLength + 4 + 2 + 2, cts.Token);

            var nonce = new byte[ServiceNonceLength];
            var challenge = new byte[2 + ServiceNonceLength];
            BinaryPrimitives.WriteInt16LittleEndian(challenge.AsSpan(0, 2), PacketConstants.MapServiceAuthChallenge);
            nonce.CopyTo(challenge, 2);
            await stream.WriteAsync(challenge, cts.Token);

            await ReadExactAsync(stream, 2 + ServiceProofLength, cts.Token);

            var result = new byte[3];
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(0, 2), PacketConstants.MapServiceAuthResult);
            result[2] = 1; // rejected
            await stream.WriteAsync(result, cts.Token);
        }

        var readyTask = connector.WaitUntilReadyAsync(cts.Token);
        var completed = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(readyTask, completed);
        Assert.False(connector.IsConnected);

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { }
    }

    private static byte[] ComputeExpectedProof(byte[] token, string serviceId, IPAddress ip, ushort port, byte[] nonce)
    {
        const string Context = "Athena.NET/MapServer/Auth/v1";
        const byte FieldSeparator = 0x1F;

        var contextBytes = Encoding.UTF8.GetBytes(Context);
        var serviceIdBytes = Encoding.UTF8.GetBytes(serviceId);
        var ipBytes = ip.MapToIPv4().GetAddressBytes();

        var length = contextBytes.Length + 1 + 1 + serviceIdBytes.Length + 1 + ipBytes.Length + 2 + nonce.Length;
        var message = new byte[length];
        var offset = 0;
        contextBytes.CopyTo(message, offset); offset += contextBytes.Length;
        message[offset++] = FieldSeparator;
        message[offset++] = (byte)serviceIdBytes.Length;
        serviceIdBytes.CopyTo(message, offset); offset += serviceIdBytes.Length;
        message[offset++] = FieldSeparator;
        ipBytes.CopyTo(message, offset); offset += ipBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), port); offset += 2;
        nonce.CopyTo(message, offset);

        using var hmac = new System.Security.Cryptography.HMACSHA256(token);
        return hmac.ComputeHash(message);
    }

    private static void AssertDoesNotContain(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            Assert.False(match, "Raw ServiceToken bytes must never appear on the wire.");
        }
    }

    private static string ReadFixedString(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        if (end < 0) end = buffer.Length;
        return Encoding.ASCII.GetString(buffer[..end]);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var bytes = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            if (bytes == 0) throw new IOException("Connection closed early.");
            read += bytes;
        }
        return buffer;
    }
}
