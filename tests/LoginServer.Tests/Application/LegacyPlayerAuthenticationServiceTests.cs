using System.Security.Cryptography;
using System.Text;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Tests.Application;

public sealed class LegacyPlayerAuthenticationServiceTests
{
    private readonly LegacyPlayerAuthenticationService _service = new();

    [Fact]
    public void VerifyPassword_PlainText_MatchesExactly()
    {
        var account = new LoginAccount { UserPass = "hunter2" };

        Assert.True(_service.VerifyPassword(account, "hunter2", passwordEnc: 0, md5Key: null));
        Assert.False(_service.VerifyPassword(account, "wrong", passwordEnc: 0, md5Key: null));
    }

    [Fact]
    public void VerifyPassword_Md5KeyPrefix_MatchesRAthenaChallengeScheme()
    {
        var account = new LoginAccount { UserPass = "hunter2" };
        var md5Key = Encoding.ASCII.GetBytes("challenge-key");

        var expected = Md5Hex(Concat(md5Key, Encoding.ASCII.GetBytes(account.UserPass)));

        Assert.True(_service.VerifyPassword(account, expected, passwordEnc: 0x01, md5Key: md5Key));
    }

    [Fact]
    public void VerifyPassword_Md5KeySuffix_MatchesRAthenaChallengeScheme()
    {
        var account = new LoginAccount { UserPass = "hunter2" };
        var md5Key = Encoding.ASCII.GetBytes("challenge-key");

        var expected = Md5Hex(Concat(Encoding.ASCII.GetBytes(account.UserPass), md5Key));

        Assert.True(_service.VerifyPassword(account, expected, passwordEnc: 0x02, md5Key: md5Key));
    }

    [Fact]
    public void VerifyPassword_Md5Enc_WithoutKey_Fails()
    {
        var account = new LoginAccount { UserPass = "hunter2" };

        Assert.False(_service.VerifyPassword(account, "anything", passwordEnc: 0x01, md5Key: null));
    }

    [Fact]
    public void VerifyCredentials_ServiceAccount_IsSexRestricted()
    {
        var account = new LoginAccount { UserPass = "secret", Sex = "S" };

        var result = _service.VerifyCredentials(account, "secret", passwordEnc: 0, md5Key: null);

        Assert.Equal(PlayerCredentialOutcome.SexRestricted, result);
    }

    [Fact]
    public void VerifyCredentials_WrongPassword_IsInvalidPassword()
    {
        var account = new LoginAccount { UserPass = "secret", Sex = "M" };

        var result = _service.VerifyCredentials(account, "wrong", passwordEnc: 0, md5Key: null);

        Assert.Equal(PlayerCredentialOutcome.InvalidPassword, result);
    }

    [Fact]
    public void VerifyCredentials_CorrectPassword_Succeeds()
    {
        var account = new LoginAccount { UserPass = "secret", Sex = "F" };

        var result = _service.VerifyCredentials(account, "secret", passwordEnc: 0, md5Key: null);

        Assert.Equal(PlayerCredentialOutcome.Success, result);
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
        var hash = md5.ComputeHash(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
