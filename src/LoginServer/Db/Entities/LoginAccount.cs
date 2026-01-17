namespace Rathena.LoginServer.Db.Entities;

public sealed class LoginAccount
{
    public uint AccountId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserPass { get; set; } = string.Empty;
    public string Sex { get; set; } = "M";
    public string Email { get; set; } = string.Empty;
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
