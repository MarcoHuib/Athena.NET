using System.Security.Cryptography;
using System.Text;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Tests.Application;

public sealed class ServiceAuthenticationServiceTests
{
    [Fact]
    public void VerifyPassword_PlainText_MatchesExactly()
    {
        var service = new ServiceAuthenticationService();
        var account = new LoginAccount { UserPass = "hunter2" };

        Assert.True(service.VerifyPassword(account, "hunter2", passwordEnc: 0, md5Key: null));
        Assert.False(service.VerifyPassword(account, "wrong", passwordEnc: 0, md5Key: null));
    }

    [Fact]
    public void VerifyPassword_Md5KeyPrefix_MatchesRAthenaChallengeScheme()
    {
        var service = new ServiceAuthenticationService();
        var account = new LoginAccount { UserPass = "hunter2" };
        var md5Key = Encoding.ASCII.GetBytes("challenge-key");
        var expected = Md5Hex(Concat(md5Key, Encoding.ASCII.GetBytes(account.UserPass)));

        Assert.True(service.VerifyPassword(account, expected, passwordEnc: 0x01, md5Key: md5Key));
    }

    [Fact]
    public void VerifyPassword_Md5Enc_WithoutKey_Fails()
    {
        var service = new ServiceAuthenticationService();
        var account = new LoginAccount { UserPass = "hunter2" };

        Assert.False(service.VerifyPassword(account, "anything", passwordEnc: 0x01, md5Key: null));
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

    [Fact]
    public void Authenticate_ValidReservedServiceAccount_Succeeds_ButDoesNotYetMarkConnectionAuthenticated()
    {
        // Authenticate() only classifies credentials. The caller (ClientSession)
        // still has to run expiration/ban/state checks and reach a final
        // successful login before the connection is trusted - see
        // MarkAuthenticated_OnlyExplicitCall_SetsIsAuthenticated below.
        var service = new ServiceAuthenticationService();
        Assert.False(service.IsAuthenticated);

        var result = service.Authenticate(new LoginAccount { AccountId = 1, Sex = "S" }, passwordMatches: true);

        Assert.True(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.Success, result.Outcome);
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public void MarkAuthenticated_OnlyExplicitCall_SetsIsAuthenticated()
    {
        var service = new ServiceAuthenticationService();
        Assert.False(service.IsAuthenticated);

        service.MarkAuthenticated();

        Assert.True(service.IsAuthenticated);
    }

    [Fact]
    public void Authenticate_UnknownAccount_Fails_AndLeavesConnectionUnauthenticated()
    {
        var service = new ServiceAuthenticationService();

        var result = service.Authenticate(null, passwordMatches: false);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.AccountNotFound, result.Outcome);
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public void Authenticate_WrongPassword_Fails_AndLeavesConnectionUnauthenticated()
    {
        var service = new ServiceAuthenticationService();

        var result = service.Authenticate(new LoginAccount { AccountId = 1, Sex = "S" }, passwordMatches: false);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.InvalidCredential, result.Outcome);
        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public void Authenticate_PlayerAccount_IsNeverAuthorizedAsService()
    {
        var service = new ServiceAuthenticationService();

        var result = service.Authenticate(new LoginAccount { AccountId = 2000001, Sex = "M" }, passwordMatches: true);

        Assert.False(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.NotAuthorized, result.Outcome);
        Assert.False(service.IsAuthenticated);
    }
}
