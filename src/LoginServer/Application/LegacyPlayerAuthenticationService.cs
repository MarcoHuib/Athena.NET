using System.Security.Cryptography;
using System.Text;
using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Legacy (pre-Identity) implementation of <see cref="IPlayerAuthenticationService"/>,
/// verifying credentials against the plaintext/MD5 UserPass column exactly as the
/// original ClientSession.CheckPassword did. This is intentionally the only place
/// left that understands that storage format; it exists solely to be replaced by an
/// ASP.NET Core Identity-backed implementation.
/// </summary>
public sealed class LegacyPlayerAuthenticationService : IPlayerAuthenticationService
{
    public bool VerifyPassword(LoginAccount account, string suppliedPassword, int passwordEnc, byte[]? md5Key)
    {
        if (passwordEnc == 0)
        {
            return string.Equals(suppliedPassword, account.UserPass, StringComparison.Ordinal);
        }

        if (md5Key == null || md5Key.Length == 0)
        {
            return false;
        }

        if ((passwordEnc & 0x01) != 0)
        {
            var hash = Md5Hex(Concat(md5Key, Encoding.ASCII.GetBytes(account.UserPass)));
            if (string.Equals(suppliedPassword, hash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if ((passwordEnc & 0x02) != 0)
        {
            var hash = Md5Hex(Concat(Encoding.ASCII.GetBytes(account.UserPass), md5Key));
            if (string.Equals(suppliedPassword, hash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public PlayerCredentialOutcome VerifyCredentials(LoginAccount account, string suppliedPassword, int passwordEnc, byte[]? md5Key)
    {
        if (string.Equals(account.Sex, "S", StringComparison.OrdinalIgnoreCase))
        {
            return PlayerCredentialOutcome.SexRestricted;
        }

        return VerifyPassword(account, suppliedPassword, passwordEnc, md5Key)
            ? PlayerCredentialOutcome.Success
            : PlayerCredentialOutcome.InvalidPassword;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var buffer = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, buffer, 0, first.Length);
        Buffer.BlockCopy(second, 0, buffer, first.Length, second.Length);
        return buffer;
    }

    private static string Md5Hex(byte[] data)
    {
        using var md5 = MD5.Create();
        return BytesToHex(md5.ComputeHash(data));
    }

    private static string BytesToHex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
