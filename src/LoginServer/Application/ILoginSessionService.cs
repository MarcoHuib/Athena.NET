using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Application;

/// <summary>
/// Result of checking whether an account already has an active/pending Ragnarok
/// game session before issuing a new one.
/// </summary>
public enum DuplicateLoginCheckResult
{
    /// <summary>No conflicting session; the caller may issue a new one.</summary>
    Ok,

    /// <summary>The account is already online through a char server; the new login must be refused.</summary>
    AlreadyOnline,
}

/// <summary>
/// Owns Ragnarok game-session concerns that are distinct from human authentication:
/// LoginId1/LoginId2 issuance, AuthNode creation/consumption, and duplicate-login
/// handling for the pending Login -&gt; Char handoff. Existing legacy protocol
/// fields (LoginId1, LoginId2, AuthNode) must remain wire-compatible; this
/// abstraction must never be backed by ASP.NET Core Identity.
/// </summary>
public interface ILoginSessionService
{
    /// <summary>Generates a fresh LoginId1/LoginId2 pair for a new session.</summary>
    (uint LoginId1, uint LoginId2) GenerateLoginIds();

    /// <summary>Checks for and clears any stale pending session for this account.</summary>
    DuplicateLoginCheckResult CheckDuplicateLogin(uint accountId);

    /// <summary>Records the AuthNode and pending online-user state for a newly issued session.</summary>
    void BeginPendingHandoff(AuthNode node);

    /// <summary>
    /// Attempts to consume the pending AuthNode for the given account, matching the
    /// existing legacy LoginId1/LoginId2/sex tuple exactly once.
    /// </summary>
    bool TryConsumeAuthNode(uint accountId, uint loginId1, uint loginId2, byte sex, out byte clientType);
}
