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
    byte Sex)
{
    public const int MinimumLength = 2 + 2 + 4 + 4 + 4 + 4 + 4 + 1 + 4 + PacketConstants.MapNameLength + 2 + 2 + 1 + 2 + 1;
}
