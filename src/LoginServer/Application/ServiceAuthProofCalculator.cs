using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Computes the HMAC-SHA256 proof used by Athena.NET's internal CharServer
/// &lt;-&gt; LoginServer service authentication (see ai/login-server.md).
/// <para>
/// This exact algorithm is independently implemented on the CharServer side
/// (Athena.Net.CharServer.Net.ServiceAuthProofCalculator) - both must stay
/// byte-for-byte identical. The ServiceToken (the shared secret) never
/// travels over the network; only this proof does.
/// </para>
/// <para>
/// v2 binds the complete canonical LcServiceHello registration payload -
/// not just the ServiceId - to the proof, so an active attacker who cannot
/// compute the HMAC cannot tamper with the advertised IP/port/server
/// name/maintenance/new-display fields of an in-flight hello while leaving
/// the ServiceId (and therefore the proof) unchanged. v1 only bound
/// ServiceId + nonce; see the "Auth/v2" domain-separation string below.
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
/// The leading context string is domain separation, tying the proof to
/// this exact protocol/version. 0x1F (ASCII Unit Separator) delimits each
/// logical section. The two textual fields (ServiceId, ServerName) are
/// additionally length-prefixed with a single byte (both are always far
/// shorter than 255 bytes on the wire), which makes their boundaries
/// unambiguous regardless of content - this is deliberately not built by
/// concatenating bare strings. The fixed-width fields (IP, port,
/// maintenance, new-display, nonce) need no delimiter since every reader
/// of this format already knows their exact width.
/// </para>
/// </summary>
internal static class ServiceAuthProofCalculator
{
    private const string Context = "Athena.NET/CharServer/Auth/v2";
    private const byte FieldSeparator = 0x1F;

    internal static byte[] ComputeProof(byte[] serviceToken, ServiceHelloInfo hello, ReadOnlySpan<byte> nonce)
    {
        var message = BuildMessage(hello, nonce);
        using var hmac = new HMACSHA256(serviceToken);
        return hmac.ComputeHash(message);
    }

    private static byte[] BuildMessage(ServiceHelloInfo hello, ReadOnlySpan<byte> nonce)
    {
        var contextBytes = Encoding.UTF8.GetBytes(Context);
        var serviceIdBytes = Encoding.UTF8.GetBytes(hello.ServiceId ?? string.Empty);
        var serverNameBytes = Encoding.UTF8.GetBytes(hello.ServerName ?? string.Empty);
        var ipBytes = hello.Ip.MapToIPv4().GetAddressBytes();

        if (serviceIdBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("ServiceId is too long to encode in the service-auth proof message.", nameof(hello));
        }

        if (serverNameBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException("ServerName is too long to encode in the service-auth proof message.", nameof(hello));
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
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), hello.Port);
        offset += 2;

        message[offset++] = (byte)serverNameBytes.Length;
        serverNameBytes.CopyTo(message, offset);
        offset += serverNameBytes.Length;
        message[offset++] = FieldSeparator;

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), hello.CharMaintenance);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset, 2), hello.CharNewDisplay);
        offset += 2;

        nonce.CopyTo(message.AsSpan(offset));

        return message;
    }
}

/// <summary>
/// The complete canonical CharServer registration payload presented in
/// LcServiceHello - the exact set of fields the HMAC-SHA256 service-auth
/// proof binds (see <see cref="ServiceAuthProofCalculator"/>), and the same
/// values LoginServer registers into <see cref="Net.CharServerRegistry"/> on
/// a successful handshake. Shared by <see cref="IServiceAuthenticationService"/>
/// (which needs it to compute the expected proof) and <see cref="Net.ClientSession"/>
/// (which needs it to register the CharServer) so there is a single source
/// of truth - the hello a caller registers is always exactly the hello whose
/// fields were cryptographically bound into the verified proof.
/// </summary>
public sealed record ServiceHelloInfo(
    string ServiceId,
    IPAddress Ip,
    ushort Port,
    string ServerName,
    ushort CharMaintenance,
    ushort CharNewDisplay);
