using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Net;

internal enum ServerAccountFailure
{
    None,
    NotFound,
    InvalidCredential,
    NotAuthorized,
}

internal static class ServerAccountAuthentication
{
    internal static ServerAccountFailure Classify(LoginAccount? account, bool passwordMatches)
    {
        if (account is null) return ServerAccountFailure.NotFound;
        if (!string.Equals(account.Sex, "S", StringComparison.OrdinalIgnoreCase) || account.AccountId >= 5)
            return ServerAccountFailure.NotAuthorized;
        return passwordMatches ? ServerAccountFailure.None : ServerAccountFailure.InvalidCredential;
    }
}
