using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Tests.Application;

public sealed class ServiceAuthenticationServiceTests
{
    [Fact]
    public void Authenticate_ValidReservedServiceAccount_Succeeds_AndMarksConnectionAuthenticated()
    {
        var service = new ServiceAuthenticationService();
        Assert.False(service.IsAuthenticated);

        var result = service.Authenticate(new LoginAccount { AccountId = 1, Sex = "S" }, passwordMatches: true);

        Assert.True(result.Success);
        Assert.Equal(ServiceAuthenticationOutcome.Success, result.Outcome);
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
