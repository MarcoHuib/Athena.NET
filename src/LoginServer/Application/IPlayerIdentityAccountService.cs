namespace Athena.Net.LoginServer.Application;

public enum ChangeEmailFailureReason
{
    None,
    GameAccountNotFound,
    CurrentEmailMismatch,
    RejectedByIdentity,
}

public sealed record ChangeEmailResult(bool Success, ChangeEmailFailureReason FailureReason)
{
    public static ChangeEmailResult Ok() => new(true, ChangeEmailFailureReason.None);

    public static ChangeEmailResult Fail(ChangeEmailFailureReason reason) => new(false, reason);
}

/// <summary>
/// Application-level boundary for CharServer-driven changes to a player's
/// Identity account (currently just email). ClientSession/the packet layer
/// must never reproduce ASP.NET Core Identity's own normalization/validation
/// behavior (normalized-email casing, email-confirmed reset, uniqueness) by
/// hand - this is the one place that goes through UserManager instead.
/// </summary>
public interface IPlayerIdentityAccountService
{
    Task<ChangeEmailResult> ChangeEmailAsync(uint ragnarokAccountId, string currentEmail, string newEmail, CancellationToken cancellationToken);
}
