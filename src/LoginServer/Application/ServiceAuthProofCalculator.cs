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
/// Wire format: HMAC-SHA256(ServiceToken, message), where
/// <c>message = UTF8("Athena.NET/CharServer/Auth/v1") + 0x1F + UTF8(serviceId) + 0x1F + nonce</c>.
/// The leading context string is domain separation, tying the proof
/// specifically to this protocol so it can never be confused with a proof
/// computed elsewhere from the same shared secret. 0x1F (ASCII Unit
/// Separator) delimits the variable-length ServiceId from its neighbors
/// unambiguously; the nonce is fixed-length (32 bytes) so it needs no
/// trailing delimiter.
/// </para>
/// </summary>
internal static class ServiceAuthProofCalculator
{
    private const string Context = "Athena.NET/CharServer/Auth/v1";
    private const byte FieldSeparator = 0x1F;

    internal static byte[] ComputeProof(byte[] serviceToken, string serviceId, ReadOnlySpan<byte> nonce)
    {
        var message = BuildMessage(serviceId, nonce);
        using var hmac = new HMACSHA256(serviceToken);
        return hmac.ComputeHash(message);
    }

    private static byte[] BuildMessage(string serviceId, ReadOnlySpan<byte> nonce)
    {
        var contextBytes = Encoding.UTF8.GetBytes(Context);
        var serviceIdBytes = Encoding.UTF8.GetBytes(serviceId ?? string.Empty);

        var message = new byte[contextBytes.Length + 1 + serviceIdBytes.Length + 1 + nonce.Length];
        var offset = 0;

        contextBytes.CopyTo(message, offset);
        offset += contextBytes.Length;

        message[offset++] = FieldSeparator;

        serviceIdBytes.CopyTo(message, offset);
        offset += serviceIdBytes.Length;

        message[offset++] = FieldSeparator;

        nonce.CopyTo(message.AsSpan(offset));

        return message;
    }
}
