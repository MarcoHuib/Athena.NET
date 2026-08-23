using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

public sealed class ServerAccountAuthenticationTests
{
    [Fact]
    public void ValidReservedServerAccount_IsAccepted()
    {
        Assert.Equal(ServerAccountFailure.None,
            ServerAccountAuthentication.Classify(new LoginAccount { AccountId = 1, Sex = "S" }, true));
    }

    [Fact]
    public void AbsentServerAccount_IsRejected()
    {
        Assert.Equal(ServerAccountFailure.NotFound, ServerAccountAuthentication.Classify(null, false));
    }

    [Fact]
    public void InvalidPassword_IsRejected()
    {
        Assert.Equal(ServerAccountFailure.InvalidCredential,
            ServerAccountAuthentication.Classify(new LoginAccount { AccountId = 1, Sex = "S" }, false));
    }

    [Theory]
    [InlineData(1, "M")]
    [InlineData(1, "F")]
    [InlineData(2000000, "S")]
    public void PlayerOrNonReservedAccount_CannotAuthenticateAsServer(uint accountId, string sex)
    {
        Assert.Equal(ServerAccountFailure.NotAuthorized,
            ServerAccountAuthentication.Classify(new LoginAccount { AccountId = accountId, Sex = sex }, true));
    }
}
