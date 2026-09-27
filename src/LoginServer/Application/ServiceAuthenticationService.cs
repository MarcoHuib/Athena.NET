using System.Security.Cryptography;
using System.Text;
using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Default per-connection <see cref="IServiceAuthenticationService"/>. Wraps the
/// existing reserved-account-range classification (sex='S', account_id &lt; 5)
/// in <see cref="ServerAccountAuthentication"/> without changing its rules, and
/// tracks whether this specific connection has successfully authenticated as a
/// service (CharServer) so packet handlers can gate privileged operations on it.
/// Also owns legacy UserPass/MD5 verification for service-account rows, which -
/// unlike player credentials - are intentionally never migrated to ASP.NET Core
/// Identity.
/// </summary>
public sealed class ServiceAuthenticationService : IServiceAuthenticationService
{
    public bool IsAuthenticated { get; private set; }

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

    public ServiceAuthenticationResult Authenticate(LoginAccount? account, bool passwordMatches)
    {
        var failure = ServerAccountAuthentication.Classify(account, passwordMatches);
        var outcome = failure switch
        {
            ServerAccountFailure.None => ServiceAuthenticationOutcome.Success,
            ServerAccountFailure.NotFound => ServiceAuthenticationOutcome.AccountNotFound,
            ServerAccountFailure.InvalidCredential => ServiceAuthenticationOutcome.InvalidCredential,
            ServerAccountFailure.NotAuthorized => ServiceAuthenticationOutcome.NotAuthorized,
            _ => ServiceAuthenticationOutcome.NotAuthorized,
        };

        if (outcome == ServiceAuthenticationOutcome.Success)
        {
            IsAuthenticated = true;
        }

        return new ServiceAuthenticationResult(outcome);
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
