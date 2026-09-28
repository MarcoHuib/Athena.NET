namespace Athena.Net.MapServer.Net;

/// <summary>
/// Validates MapServer's configured ServiceId before the service
/// authentication handshake starts, so the value MapServer writes into the
/// fixed-width <c>MapServiceHello</c> wire field (see PacketConstants.NameLength)
/// is guaranteed to be exactly the same value it binds into the HMAC-SHA256
/// proof (see <see cref="ServiceAuthProofCalculator"/>).
/// <para>
/// Structurally identical to CharServer's own
/// <c>Athena.Net.CharServer.Net.ServiceHelloFieldValidator</c> (used for the
/// pre-existing CharServer &lt;-&gt; LoginServer handshake): without this
/// check, a configured value that needs truncation or lossy ASCII
/// replacement to fit on the wire would silently diverge from what the
/// proof was computed over.
/// </para>
/// </summary>
internal static class ServiceHelloFieldValidator
{
    /// <summary>Reserves one byte for the wire field's NUL terminator.</summary>
    internal const int MaxServiceIdLength = PacketConstants.NameLength - 1;

    internal static bool TryValidate(string? serviceId, out string validated, out string error)
    {
        if (string.IsNullOrEmpty(serviceId))
        {
            validated = string.Empty;
            error = "ServiceId must not be empty.";
            return false;
        }

        foreach (var c in serviceId)
        {
            if (c < 0x20 || c > 0x7E)
            {
                validated = string.Empty;
                error = $"ServiceId must contain only printable ASCII characters (configured value: '{serviceId}').";
                return false;
            }
        }

        if (serviceId.Length > MaxServiceIdLength)
        {
            validated = string.Empty;
            error = $"ServiceId must be at most {MaxServiceIdLength} characters to leave room for the fixed-width wire field's " +
                $"NUL terminator (configured value is {serviceId.Length} characters: '{serviceId}').";
            return false;
        }

        validated = serviceId;
        error = string.Empty;
        return true;
    }
}
