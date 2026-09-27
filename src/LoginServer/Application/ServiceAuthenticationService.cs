using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Default per-connection <see cref="IServiceAuthenticationService"/>. Wraps the
/// existing reserved-account-range classification (sex='S', account_id &lt; 5)
/// in <see cref="ServerAccountAuthentication"/> without changing its rules, and
/// tracks whether this specific connection has successfully authenticated as a
/// service (CharServer) so packet handlers can gate privileged operations on it.
/// </summary>
public sealed class ServiceAuthenticationService : IServiceAuthenticationService
{
    public bool IsAuthenticated { get; private set; }

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
}
