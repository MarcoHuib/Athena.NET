using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Athena.Net.CharServer.Net;

/// <summary>
/// Computes the HMAC-SHA256 proof used by Athena.NET's internal MapServer
/// &lt;-&gt; CharServer service authentication (see ai/char-server.md,
/// "Inter-server service authentication (MapServer)").
/// <para>
/// This exact algorithm is independently implemented on the MapServer side
/// (Athena.Net.MapServer.Net.ServiceAuthProofCalculator) - both must stay
/// byte-for-byte identical. The MapServer ServiceToken (the shared secret)
/// never travels over the network; only this proof does. This handshake and
/// its ServiceToken are completely independent of CharServer's own
/// HMAC-SHA256 handshake against LoginServer (see
/// <see cref="ServiceAuthProofCalculator"/>) - a different token, a
/// different domain-separation context string, and a different packet
/// range.
/// </para>
/// <para>
/// Wire format: HMAC-SHA256(ServiceToken, message) - see
/// Athena.Net.MapServer.Net.ServiceAuthProofCalculator's own doc comment for
/// the exact byte layout this must match.
/// </para>
/// </summary>
internal static class MapServiceAuthProofCalculator
{
    private const string Context = "Athena.NET/MapServer/Auth/v1";
    private const byte FieldSeparator = 0x1F;

    internal static byte[] ComputeProof(byte[] serviceToken, MapServiceHelloInfo hello, ReadOnlySpan<byte> nonce)
    {
        var message = BuildMessage(hello, nonce);
        using var hmac = new HMACSHA256(serviceToken);
        return hmac.ComputeHash(message);
    }

    private static byte[] BuildMessage(MapServiceHelloInfo hello, ReadOnlySpan<byte> nonce)
    {
        var contextBytes = Encoding.UTF8.GetBytes(Context);
        var serviceIdBytes = Encoding.UTF8.GetBytes(hello.ServiceId ?? string.Empty);
        var ipBytes = hello.Ip.MapToIPv4().GetAddressBytes();

        if (serviceIdBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("ServiceId is too long to encode in the service-auth proof message.", nameof(hello));
        }

        var length = contextBytes.Length + 1
            + 1 + serviceIdBytes.Length + 1
            + ipBytes.Length + 2
            + nonce.Length;

        var message = new byte[length];
        var offset = 0;

        contextBytes.CopyTo(message, offset);
        offset += contextBytes.Length;
        message[offset++] = FieldSeparator;

        message[offset++] = (byte)serviceIdBytes.Length;
        serviceIdBytes.CopyTo(message, offset);
        offset += serviceIdBytes.Length;
        message[offset++] = FieldSeparator;

        ipBytes.CopyTo(message, offset);
        offset += ipBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), hello.Port);
        offset += 2;

        nonce.CopyTo(message.AsSpan(offset));

        return message;
    }
}

/// <summary>
/// The canonical MapServer registration payload presented in
/// <c>MapServiceHello</c> - the exact set of fields the HMAC-SHA256
/// service-auth proof binds (see <see cref="MapServiceAuthProofCalculator"/>).
/// Shared by <see cref="IMapServiceAuthenticationService"/> (which needs it
/// to compute the expected proof) and <see cref="MapServerSession"/> (which
/// needs it to register the MapServer) so there is a single source of truth.
/// </summary>
public sealed record MapServiceHelloInfo(string ServiceId, IPAddress Ip, ushort Port);
