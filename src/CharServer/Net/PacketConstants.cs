namespace Athena.Net.CharServer.Net;

public static class PacketConstants
{
    public const int PacketVer = 20220406;
    public const int NameLength = 24;
    public const int ServerNameLength = 20;
    public const int MapNameLength = 16;
    public const int DomainLength = 128;

    public const short LcCharServerLogin = 0x2710;
    public const short LcCharServerLoginAck = 0x2711;
    public const short LcAuthRequest = 0x2712;
    public const short LcAuthResponse = 0x2713;
    public const short LcAccountDataRequest = 0x2716;
    public const short LcAccountDataResponse = 0x2717;
    public const short LcKeepAliveResponse = 0x2718;
    public const short LcIpSyncRequest = 0x2735;
    public const short LcPincodeUpdate = 0x2738;
    public const short LcPincodeAuthFail = 0x2739;

    public const short MapLogin = 0x2af8;
    public const short MapLoginAck = 0x2af9;
    public const short MapSendMaps = 0x2afa;
    public const short MapAuthRequest = 0x2b26;
    public const short MapAuthOk = 0x2afd;
    public const short MapAuthFail = 0x2b27;

    public const short ChReqConnect = 0x65;
    public const short ChSelectChar = 0x66;
    public const short ChMakeChar = 0xa39;
    public const short ChDeleteChar = 0x1fb;
    public const short ChDeleteChar3Reserved = 0x827;
    public const short ChDeleteChar3 = 0x829;
    public const short ChDeleteChar3Cancel = 0x82b;
    public const short ChCharListReq = 0x9a1;
    public const short ChPing = 0x187;
    public const short ChReqIsValidCharName = 0x28d;
    public const short ChReqChangeCharName = 0x8fc;
    public const short ChReqChangeCharacterSlot = 0x8d4;
    public const short ChAvailableSecondPassword = 0x8c5;
    public const short ChSecondPasswordAck = 0x8b8;
    public const short ChMakeSecondPassword = 0x8ba;
    public const short ChEditSecondPassword = 0x8be;

    public const short HcAcceptEnter = 0x6b;
    public const short HcRefuseEnter = 0x6c;
    public const short HcAcceptEnter2 = 0x82d;
    public const short HcCharListNotify = 0x9a0;
    public const short HcAckCharInfoPerPage = 0xb72;
    public const short HcRefuseMakeChar = 0x6e;
    public const short HcAcceptMakeChar = 0xb6f;
    public const short HcAcceptDeleteChar = 0x6f;
    public const short HcRefuseDeleteChar = 0x70;
    public const short HcDeleteChar3Reserved = 0x828;
    public const short HcDeleteChar3 = 0x82a;
    public const short HcDeleteChar3Cancel = 0x82c;
    public const short HcAckIsValidCharName = 0x28e;
    public const short HcAckChangeCharName = 0x8fd;
    public const short HcSecondPasswordLogin = 0x8b9;
    public const short HcAckChangeCharacterSlot = 0xb70;
    public const short HcNotifyZoneServer = 0xac5;
}
