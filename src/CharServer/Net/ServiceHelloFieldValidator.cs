namespace Athena.Net.CharServer.Net;

/// <summary>
/// The canonical, wire-safe ServiceId/ServerName pair CharServer presents in
/// LcServiceHello and binds into the HMAC-SHA256 service-auth proof (see
/// <see cref="ServiceAuthProofCalculator"/>). Both <c>SendServiceHelloAsync</c>
/// and the proof computation must be given exactly this same instance's
/// fields rather than independently re-reading <c>CharConfig.ServiceId</c>/
/// <c>ServerName</c>, so the two can never accidentally diverge.
/// </summary>
internal readonly record struct ValidatedServiceHelloFields(string ServiceId, string ServerName);

/// <summary>
/// Validates CharServer's configured ServiceId/ServerName before the service
/// authentication handshake starts, so the value CharServer writes into the
/// fixed-width <c>LcServiceHello</c> wire fields (see PacketConstants.NameLength/
/// ServerNameLength) is guaranteed to be exactly the same value it binds into
/// the HMAC-SHA256 proof (see <see cref="ServiceAuthProofCalculator"/>).
/// <para>
/// Without this check, a configured value that needs truncation or lossy
/// ASCII replacement to fit on the wire would silently diverge from what the
/// proof was computed over: LoginServer would decode a different string than
/// CharServer signed, and the proof would either mysteriously fail to verify
/// (safe, but a confusing config-time failure surfacing as a crypto failure)
/// or - if the two ends of that divergence coincidentally matched a shorter
/// truncated value in a targeted way - foreseeably violate the intent that
/// the whole registration payload is authenticated. Rejecting up front
/// instead is a config-time, non-cryptographic failure that is easy to
/// diagnose and cannot silently drift.
/// </para>
/// </summary>
internal static class ServiceHelloFieldValidator
{
    /// <summary>
    /// Reserves one byte for the wire field's NUL terminator, matching the
    /// repository's existing fixed-string convention (see
    /// PlayerAccountProvisioningService's stock-client username/password
    /// length ceiling on the LoginServer side).
    /// </summary>
    internal const int MaxServiceIdLength = PacketConstants.NameLength - 1;

    internal const int MaxServerNameLength = PacketConstants.ServerNameLength - 1;

    internal static bool TryValidate(string? serviceId, string? serverName, out ValidatedServiceHelloFields fields, out string error)
    {
        if (!TryValidateField(serviceId, "ServiceId", MaxServiceIdLength, out error))
        {
            fields = default;
            return false;
        }

        if (!TryValidateField(serverName, "ServerName", MaxServerNameLength, out error))
        {
            fields = default;
            return false;
        }

        fields = new ValidatedServiceHelloFields(serviceId!, serverName!);
        error = string.Empty;
        return true;
    }

    private static bool TryValidateField(string? value, string fieldName, int maxLength, out string error)
    {
        if (string.IsNullOrEmpty(value))
        {
            error = $"{fieldName} must not be empty.";
            return false;
        }

        foreach (var c in value)
        {
            // Printable ASCII only (0x20-0x7E): excludes non-ASCII characters
            // (which Encoding.ASCII would otherwise silently replace with '?'
            // on the wire) and control characters, including NUL - an
            // embedded NUL would still encode without complaint but would
            // cause the receiving side's NUL-terminated read to truncate the
            // string earlier than the sender's own (non-NUL-aware) proof
            // computation would, reintroducing exactly the kind of
            // wire/HMAC-input divergence this validator exists to prevent.
            if (c < 0x20 || c > 0x7E)
            {
                error = $"{fieldName} must contain only printable ASCII characters (configured value: '{value}').";
                return false;
            }
        }

        if (value.Length > maxLength)
        {
            error = $"{fieldName} must be at most {maxLength} characters to leave room for the fixed-width wire field's " +
                $"NUL terminator (configured value is {value.Length} characters: '{value}').";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
