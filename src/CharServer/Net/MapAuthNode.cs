namespace Athena.Net.CharServer.Net;

public sealed record MapAuthNode(
    uint AccountId,
    uint CharId,
    uint LoginId1,
    uint LoginId2,
    byte Sex,
    string MapName,
    ushort X,
    ushort Y,
    byte Direction,
    ushort Font,
    uint ExpirationTime,
    uint GroupId,
    bool ChangingMapServers);
