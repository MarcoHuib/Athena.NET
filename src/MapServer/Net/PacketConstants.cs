namespace Athena.Net.MapServer.Net;

public static class PacketConstants
{
    public const int PacketVer = 20220406;
    public const int NameLength = 24;
    public const int MapNameLength = 16;

    public const short MapLogin = 0x2af8;
    public const short MapLoginAck = 0x2af9;
    public const short MapSendMaps = 0x2afa;
    public const short MapAuthRequest = 0x2b26;
    public const short MapAuthOk = 0x2afd;
    public const short MapAuthFail = 0x2b27;

    public const short CzEnter = 0x72;
    public const short CzEnter2 = 0x436;
    public const short CzNotifyActorInit = 0x7d;
    public const short CzClientVersion = 0x44a;
    public const short CzPingLive = 0x0b1c;

    public const short ZcAcceptEnter = 0x2eb;
    public const short ZcRefuseEnter = 0x74;
    public const short ZcNotifyActorInit = 0x0b1b;
    public const short ZcPingLive = 0x0b1d;
}
