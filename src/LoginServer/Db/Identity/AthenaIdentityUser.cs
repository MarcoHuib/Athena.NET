using Microsoft.AspNetCore.Identity;

namespace Athena.Net.LoginServer.Db.Identity;

/// <summary>
/// Human identity for Athena.NET, owned entirely by ASP.NET Core Identity.
/// <para>
/// <see cref="Microsoft.AspNetCore.Identity.IdentityUser{TKey}.UserName"/> is the
/// Ragnarok login username exactly as entered in the stock client's 0x0064
/// request. <see cref="Microsoft.AspNetCore.Identity.IdentityUser{TKey}.Email"/> is
/// a separate identity intended for future website/account authentication and is
/// never coupled to UserName.
/// </para>
/// <para>
/// This type must never own Ragnarok game-authorization concepts (GroupId,
/// character slots, VIP state, bans) - those belong to <see cref="AthenaGameAccount"/>.
/// </para>
/// </summary>
public sealed class AthenaIdentityUser : IdentityUser<Guid>
{
}
