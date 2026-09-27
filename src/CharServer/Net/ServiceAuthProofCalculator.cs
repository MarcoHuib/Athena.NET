using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Athena.Net.CharServer.Net;

/// <summary>
/// Computes the HMAC-SHA256 proof used by Athena.NET's internal CharServer
/// &lt;-&gt; LoginServer service authentication (see ai/login-server.md).
/// <para>
/// This exact algorithm is independently implemented on the LoginServer side
/// (Athena.Net.LoginServer.Application.ServiceAuthProofCalculator) - both
/// must stay byte-for-byte identical. The ServiceToken (the shared secret)
/// never travels over the network; only this proof does.
/// </para>
/// <para>
/// v2 binds the complete LcServiceHello registration payload - ServiceId,
/// advertised IP, port, server name, maintenance/type, and new-display flag -
/// not just the ServiceId, so an active attacker who cannot compute the HMAC
/// cannot tamper with any of those fields in-flight while leaving the proof
/// valid. See the "Auth/v2" domain-separation string below.
/// </para>
/// <para>
/// Wire format: HMAC-SHA256(ServiceToken, message), where message is a
/// deterministic, unambiguous binary serialization of every field in a
/// fixed order:
/// <code>
/// UTF8("Athena.NET/CharServer/Auth/v2")
/// + 0x1F
/// + byte(len(UTF8(serviceId))) + UTF8(serviceId)
/// + 0x1F
/// + ip.GetAddressBytes() (4 bytes, IPv4)
/// + port (UInt16, big-endian)
/// + byte(len(UTF8(serverName))) + UTF8(serverName)
/// + 0x1F
/// + charMaintenance (UInt16, big-endian)
/// + charNewDisplay (UInt16, big-endian)
/// + nonce (32 bytes)
/// </code>
/// The two textual fields (ServiceId, ServerName) are length-prefixed with a
/// single byte (both are always far shorter than 255 bytes on the wire),
/// making their boundaries unambiguous regardless of content - this is
/// deliberately not built by concatenating bare strings. The fixed-width
/// fields need no delimiter since every reader of this format already knows
/// their exact width.
/// </para>
/// </summary>
internal static class ServiceAuthProofCalculator
{
    private const string Context = "Athena.NET/CharServer/Auth/v2";
    private const byte FieldSeparator = 0x1F;

    internal static byte[] ComputeProof(
        byte[] serviceToken,
        string serviceId,
        IPAddress ip,
        ushort port,
        string serverName,
        ushort charMaintenance,
        ushort charNewDisplay,
        ReadOnlySpan<byte> nonce)
    {
        var message = BuildMessage(serviceId, ip, port, serverName, charMaintenance, charNewDisplay, nonce);
        using var hmac = new HMACSHA256(serviceToken);
        return hmac.ComputeHash(message);
    }

    private static byte[] BuildMessage(
        string serviceId,
        IPAddress ip,
        ushort port,
        string serverName,
        ushort charMaintenance,
        ushort charNewDisplay,
        ReadOnlySpan<byte> nonce)
    {
        var contextBytes = Encoding.UTF8.GetBytes(Context);
        var serviceIdBytes = Encoding.UTF8.GetBytes(serviceId ?? string.Empty);
        var serverNameBytes = Encoding.UTF8.GetBytes(serverName ?? string.Empty);
        var ipBytes = ip.MapToIPv4().GetAddressBytes();

        if (serviceIdBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("ServiceId is too long to encode in the service-auth proof message.", nameof(serviceId));
        }

        if (serverNameBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("ServerName is too long to encode in the service-auth proof message.", nameof(serverName));
        }

        var length = contextBytes.Length + 1
            + 1 + serviceIdBytes.Length + 1
            + ipBytes.Length + 2
            + 1 + serverNameBytes.Length + 1
            + 2 + 2
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

        message[offset++] = (byte)serverNameBytes.Length;
        serverNameBytes.CopyTo(message, offset);
        offset += serverNameBytes.Length;
        message[offset++] = FieldSeparator;

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), charMaintenance);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), charNewDisplay);
        offset += 2;

        nonce.CopyTo(message.AsSpan(offset));

        return message;
    }
}
