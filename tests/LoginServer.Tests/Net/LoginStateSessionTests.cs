using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

public sealed class LoginStateSessionTests
{
    [Fact]
    public void GenerateLoginIds_ReturnsNonZeroDistinctValues()
    {
        var state = new LoginState();

        var (loginId1, loginId2) = state.GenerateLoginIds();

        Assert.NotEqual(0u, loginId1);
        Assert.NotEqual(0u, loginId2);
    }

    [Fact]
    public void CheckDuplicateLogin_NoExistingSession_ReturnsOk()
    {
        var state = new LoginState();

        var result = state.CheckDuplicateLogin(42);

        Assert.Equal(DuplicateLoginCheckResult.Ok, result);
    }

    [Fact]
    public void CheckDuplicateLogin_AlreadyOnlineThroughCharServer_ReturnsAlreadyOnline()
    {
        var state = new LoginState();
        state.AddOnlineUser(charServerId: 1, accountId: 42);

        var result = state.CheckDuplicateLogin(42);

        Assert.Equal(DuplicateLoginCheckResult.AlreadyOnline, result);
    }

    [Fact]
    public void CheckDuplicateLogin_StalePendingSession_IsClearedAndReturnsOk()
    {
        var state = new LoginState();
        state.AddAuthNode(new AuthNode { AccountId = 42, LoginId1 = 1, LoginId2 = 2, Sex = 1, ClientType = 0, Ip = 0 });
        state.AddOnlineUser(charServerId: -1, accountId: 42); // pending, never reached CharServer

        var result = state.CheckDuplicateLogin(42);

        Assert.Equal(DuplicateLoginCheckResult.Ok, result);
        Assert.False(state.TryGetOnlineUser(42, out _));
        Assert.False(state.TryGetAuthNode(42, out _));
    }

    [Fact]
    public void BeginPendingHandoff_RegistersAuthNodeAndPendingOnlineUser()
    {
        var state = new LoginState();
        var node = new AuthNode { AccountId = 7, LoginId1 = 111, LoginId2 = 222, Sex = 1, ClientType = 3, Ip = 0 };

        state.BeginPendingHandoff(node);

        Assert.True(state.TryGetAuthNode(7, out var stored));
        Assert.Equal(111u, stored.LoginId1);
        Assert.True(state.TryGetOnlineUser(7, out var online));
        Assert.Equal(-1, online.CharServerId);
    }

    [Fact]
    public void TryConsumeAuthNode_MatchingTuple_SucceedsOnceThenFails()
    {
        var state = new LoginState();
        state.AddAuthNode(new AuthNode { AccountId = 9, LoginId1 = 10, LoginId2 = 20, Sex = 1, ClientType = 4, Ip = 0 });

        Assert.True(state.TryConsumeAuthNode(9, 10, 20, 1, out var clientType));
        Assert.Equal((byte)4, clientType);

        Assert.False(state.TryConsumeAuthNode(9, 10, 20, 1, out _));
    }

    [Theory]
    [InlineData(999u, 20u, (byte)1)]
    [InlineData(10u, 999u, (byte)1)]
    [InlineData(10u, 20u, (byte)0)]
    public void TryConsumeAuthNode_MismatchedTuple_Fails(uint loginId1, uint loginId2, byte sex)
    {
        var state = new LoginState();
        state.AddAuthNode(new AuthNode { AccountId = 9, LoginId1 = 10, LoginId2 = 20, Sex = 1, ClientType = 4, Ip = 0 });

        Assert.False(state.TryConsumeAuthNode(9, loginId1, loginId2, sex, out _));
        Assert.True(state.TryGetAuthNode(9, out _)); // untouched on mismatch
    }
}
