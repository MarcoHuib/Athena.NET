namespace Athena.Net.MapServer.Net;

public sealed record MapAuthOkData(
    uint AccountId,
    uint CharId,
    uint LoginId1,
    uint LoginId2,
    uint ExpirationTime,
    uint GroupId,
    bool ChangingMapServers,
    string MapName,
    ushort X,
    ushort Y,
    byte Direction,
    ushort Font,
    byte Sex,
    string CharacterName = "",
    ushort HairStyle = 0,
    ushort HairColor = 0,
    ushort ClothesColor = 0,
    ushort BodyStyle = 0,
    uint WeaponAppearance = 0,
    uint ShieldAppearance = 0,
    ushort HeadBottomAppearance = 0,
    ushort HeadTopAppearance = 0,
    ushort HeadMidAppearance = 0,
    ushort RobeAppearance = 0,
    uint Option = 0,
    byte Karma = 0,
    short Manner = 0)
{
    public const int MinimumLength = 108;
}
