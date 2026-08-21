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
    public const short MapSavePosition = 0x2b28;

    public const short CzEnter = 0x72;
    public const short CzEnter2 = 0x436;
    public const short CzNotifyActorInit = 0x7d;
    public const short CzClientVersion = 0x44a;
    public const short CzPingLive = 0x0b1c;
    public const short IroCzMapAuth = 0x0c1f;
    public const int IroCzMapAuthLength = 1001;
    public const short IroCzPostEnter0360 = 0x0360;
    public const int IroCzPostEnter0360Length = 7;
    public const short IroCzPostEnter08c9 = 0x08c9;
    public const int IroCzPostEnter08c9Length = 3;
    public const short IroCzRequestMove = 0x035f;
    public const int IroCzRequestMoveLength = 6;
    public const short IroCzActorInfoRequest = 0x0368;
    public const int IroCzActorInfoRequestLength = 7;

    public const short ZcAcceptEnter = 0x2eb;
    public const short ZcNotifyPlayerMove = 0x0087;
    public const short ZcNpcAckMapMove = 0x0091;
    public const short ZcNotifyStandEntry = 0x09ff;
    public const short ZcRefuseEnter = 0x74;
    public const short ZcNotifyActorInit = 0x0b1b;
    public const short ZcPingLive = 0x0b1d;
}
