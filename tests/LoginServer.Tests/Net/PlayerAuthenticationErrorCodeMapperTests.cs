using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Tests.Net;

/// <summary>
/// PlayerAuthenticationErrorCodeMapper is the only place a
/// PlayerAuthenticationFailureReason (domain/application-level) is translated
/// into an iRO AC_REFUSE_LOGIN error code (wire-protocol-level). These tests
/// pin that mapping so IPlayerAuthenticationService implementations can stay
/// protocol-independent without silently changing observable stock-client
/// behavior.
/// </summary>
public sealed class PlayerAuthenticationErrorCodeMapperTests
{
    [Theory]
    [InlineData(PlayerAuthenticationFailureReason.AccountNotFound, 0u)]
    [InlineData(PlayerAuthenticationFailureReason.InvalidPassword, 1u)]
    [InlineData(PlayerAuthenticationFailureReason.GameAccountMissing, 0u)]
    [InlineData(PlayerAuthenticationFailureReason.AccountExpired, 2u)]
    [InlineData(PlayerAuthenticationFailureReason.AccountBanned, 6u)]
    [InlineData(PlayerAuthenticationFailureReason.LockedOut, 6u)]
    public void ToErrorCode_MapsFailureReasonToTheExpectedWireCode(PlayerAuthenticationFailureReason reason, uint expectedErrorCode)
    {
        var result = PlayerAuthenticationResult.Fail(reason);

        Assert.Equal(expectedErrorCode, PlayerAuthenticationErrorCodeMapper.ToErrorCode(result));
    }

    [Theory]
    [InlineData(1u, 0u)] // state 1 (unregistered ID) -> error code 0
    [InlineData(2u, 1u)] // state 2 (incorrect password) -> error code 1
    [InlineData(6u, 5u)] // state 6 (not permitted to use this ID) -> error code 5
    [InlineData(9u, 8u)] // state 9 (IP capacity of this Internet Cafe is full) -> error code 8
    public void ToErrorCode_AccountStateRestricted_SubtractsOneFromTheStateCode(uint stateCode, uint expectedErrorCode)
    {
        var result = PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.AccountStateRestricted, accountStateCode: stateCode);

        Assert.Equal(expectedErrorCode, PlayerAuthenticationErrorCodeMapper.ToErrorCode(result));
    }

    [Fact]
    public void ToErrorCode_AccountStateRestricted_MissingStateCode_ClampsToZero()
    {
        var result = PlayerAuthenticationResult.Fail(PlayerAuthenticationFailureReason.AccountStateRestricted);

        Assert.Equal(0u, PlayerAuthenticationErrorCodeMapper.ToErrorCode(result));
    }
}
