namespace Athena.Net.LoginServer.Db.Identity;

/// <summary>
/// Athena's modern game-account identity: strictly 1:1 with one
/// <see cref="AthenaIdentityUser"/>. <see cref="Id"/> is the canonical internal
/// Athena game-account identifier; <see cref="RagnarokAccountId"/> is the legacy
/// uint32 compatibility identifier required by the unmodified stock iRO
/// client/CharServer/MapServer wire protocol and existing Login -&gt; Char -&gt; Map
/// handoff. Do not treat RagnarokAccountId as the long-term canonical identity -
/// it exists solely for stock-protocol compatibility.
/// <para>
/// This entity owns all Ragnarok/game-specific account state (sex, group,
/// character slots, bans, expiration, VIP, PIN, web-auth-token). It must never be
/// represented by or coupled to ASP.NET Core Identity concepts.
/// </para>
/// </summary>
public sealed class AthenaGameAccount
{
    public Guid Id { get; set; }

    /// <summary>Foreign key to the owning <see cref="AthenaIdentityUser"/>. Unique (strict 1:1).</summary>
    public Guid IdentityUserId { get; set; }

    /// <summary>Legacy uint32 compatibility identifier for the stock iRO wire protocol. Unique.</summary>
    public uint RagnarokAccountId { get; set; }

    public string Sex { get; set; } = "M";
    public int GroupId { get; set; }
    public uint State { get; set; }
    public uint UnbanTime { get; set; }
    public uint ExpirationTime { get; set; }
    public int LoginCount { get; set; }
    public DateTime? LastLogin { get; set; }
    public string LastIp { get; set; } = string.Empty;
    public DateTime? Birthdate { get; set; }
    public byte CharacterSlots { get; set; }
    public string Pincode { get; set; } = string.Empty;
    public uint PincodeChange { get; set; }
    public uint VipTime { get; set; }
    public int OldGroup { get; set; }
    public string? WebAuthToken { get; set; }
    public bool WebAuthTokenEnabled { get; set; }
}
