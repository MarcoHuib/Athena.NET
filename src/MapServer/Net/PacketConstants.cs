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
    public const short MapQuestStateRequest = 0x2b29;
    public const short MapQuestStateResponse = 0x2b2a;
    public const short MapSavePointRequest = 0x2b2b;
    public const short MapSavePointResponse = 0x2b2c;
    public const short MapGameplayStateGetRequest = 0x2b2d;
    public const short MapGameplayStateGetResponse = 0x2b2e;
    public const short MapGameplayStateUpdateRequest = 0x2b2f;
    public const short MapGameplayStateUpdateResponse = 0x2b30;

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
    public const short IroCzChangeDirection = 0x0361;
    public const int IroCzChangeDirectionLength = 6;
    public const short IroCzNpcInteraction = 0x0090;
    public const int IroCzNpcInteractionLength = 8;
    public const short IroCzNpcNext = 0x00b9;
    public const int IroCzNpcNextLength = 7;
    public const short IroCzNpcClose = 0x0146;
    public const int IroCzNpcCloseLength = 7;
    public const short IroCzNpcSelection = 0x00b8;
    public const int IroCzNpcSelectionLength = 8;

    public const short ZcAcceptEnter = 0x2eb;
    public const short ZcNotifyPlayerMove = 0x0087;
    public const short ZcNpcAckMapMove = 0x0091;
    public const short ZcNotifyStandEntry = 0x09ff;
    public const short ZcNpcMessage = 0x00b4;
    public const short ZcNpcNext = 0x00b5;
    public const short ZcNpcClose = 0x00b6;
    public const short ZcNpcMenu = 0x00b7;
    public const short ZcShowImage = 0x01b3;
    public const short ZcParameterChange = 0x00b0;
    public const short ZcLongLongParameterChange = 0x0acb;
    public const short ZcRefuseEnter = 0x74;
    public const short ZcNotifyActorInit = 0x0b1b;
    public const short ZcPingLive = 0x0b1d;
}
