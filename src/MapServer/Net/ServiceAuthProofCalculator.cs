using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Athena.Net.MapServer.Net;

/// <summary>
/// Computes the HMAC-SHA256 proof used by Athena.NET's internal MapServer
/// &lt;-&gt; CharServer service authentication (see ai/map-server.md,
/// "Inter-server service authentication").
/// <para>
/// This exact algorithm is independently implemented on the CharServer side
/// (Athena.Net.CharServer.Net.MapServiceAuthProofCalculator) - both must stay
/// byte-for-byte identical. The MapServer ServiceToken (the shared secret)
/// never travels over the network; only this proof does. This handshake and
/// its ServiceToken are completely independent of the pre-existing
/// CharServer &lt;-&gt; LoginServer HMAC-SHA256 handshake.
/// </para>
/// <para>
/// Wire format: HMAC-SHA256(ServiceToken, message), where message is a
/// deterministic, unambiguous binary serialization of every field in a
/// fixed order:
/// <code>
/// UTF8("Athena.NET/MapServer/Auth/v1")
/// + 0x1F
/// + byte(len(UTF8(serviceId))) + UTF8(serviceId)
/// + 0x1F
/// + ip.GetAddressBytes() (4 bytes, IPv4)
/// + port (UInt16, big-endian)
/// + nonce (32 bytes)
/// </code>
/// The textual ServiceId field is length-prefixed with a single byte (always
/// far shorter than 255 bytes on the wire), making its boundary unambiguous
/// regardless of content. The fixed-width fields need no delimiter since
/// every reader of this format already knows their exact width.
/// </para>
/// </summary>
internal static class ServiceAuthProofCalculator
{
    private const string Context = "Athena.NET/MapServer/Auth/v1";
    private const byte FieldSeparator = 0x1F;

    internal static byte[] ComputeProof(
        byte[] serviceToken,
        string serviceId,
        IPAddress ip,
        ushort port,
        ReadOnlySpan<byte> nonce)
    {
        var message = BuildMessage(serviceId, ip, port, nonce);
        using var hmac = new HMACSHA256(serviceToken);
        return hmac.ComputeHash(message);
    }

    private static byte[] BuildMessage(string serviceId, IPAddress ip, ushort port, ReadOnlySpan<byte> nonce)
    {
        var contextBytes = Encoding.UTF8.GetBytes(Context);
        var serviceIdBytes = Encoding.UTF8.GetBytes(serviceId ?? string.Empty);
        var ipBytes = ip.MapToIPv4().GetAddressBytes();

        if (serviceIdBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("ServiceId is too long to encode in the service-auth proof message.", nameof(serviceId));
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
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), port);
        offset += 2;

        nonce.CopyTo(message.AsSpan(offset));

        return message;
    }
}
