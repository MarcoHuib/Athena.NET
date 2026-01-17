namespace Athena.Net.LoginServer.Net;

public static class PacketConstants
{
    public const int PacketVer = 20220406;
    public const int NameLength = 24;
    public const int Md5KeyLength = 20;
    public const int WebAuthTokenLength = 17;

    public const short CaLogin = 0x64;
    public const short CaLogin2 = 0x1dd;
    public const short CaLogin3 = 0x1fa;
    public const short CaConnectInfoChanged = 0x200;
    public const short CaExeHashCheck = 0x204;
    public const short CaReqHash = 0x1db;
    public const short CaLoginPcBang = 0x277;
    public const short CaLogin4 = 0x27c;
    public const short CaLoginChannel = 0x2b0;
    public const short CaSsoLoginReq = 0x825;

    public const short LcCharServerLogin = 0x2710;
    public const short LcCharServerLoginAck = 0x2711;
    public const short LcAuthRequest = 0x2712;
    public const short LcAuthResponse = 0x2713;
    public const short LcUserCount = 0x2714;
    public const short LcAccountDataRequest = 0x2716;
    public const short LcAccountDataResponse = 0x2717;
    public const short LcKeepAliveRequest = 0x2719;
    public const short LcKeepAliveResponse = 0x2718;
    public const short LcChangeEmailRequest = 0x2722;
    public const short LcUpdateAccountState = 0x2724;
    public const short LcBanAccount = 0x2725;
    public const short LcChangeSex = 0x2727;
    public const short LcUnbanAccount = 0x272a;
    public const short LcSetAccountOnline = 0x272b;
    public const short LcSetAccountOffline = 0x272c;
    public const short LcOnlineList = 0x272d;
    public const short LcAccountInfoRequest = 0x2720;
    public const short LcAccountInfoResponse = 0x2721;
    public const short LcAccountReg2Update = 0x2728;
    public const short LcGlobalAccRegResponse = 0x2726;
    public const short LcGlobalAccRegRequest = 0x272e;
    public const short LcCharIpUpdate = 0x2736;
    public const short LcIpSyncRequest = 0x2735;
    public const short LcSetAllOffline = 0x2737;
    public const short LcKickRequest = 0x2734;
    public const short LcPincodeUpdate = 0x2738;
    public const short LcPincodeAuthFail = 0x2739;
    public const short LcVipRequest = 0x2742;
    public const short LcVipResponse = 0x2743;
    public const short LcAccountStatusNotify = 0x2731;
    public const short LcAccountSexNotify = 0x2723;

    public const short AcAcceptLogin = 0xac4;
    public const short AcAckHash = 0x1dc;
    public const short AcRefuseLogin = 0x83e;
    public const short ScNotifyBan = 0x81;
}
