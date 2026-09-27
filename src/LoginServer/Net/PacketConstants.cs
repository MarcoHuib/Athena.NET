namespace Athena.Net.LoginServer.Net;

public static class PacketConstants
{
    public const int PacketVer = 20220406;
    public const int NameLength = 24;
    public const int ServerNameLength = 20;
    public const int WebAuthTokenLength = 17;
    public const int ServiceNonceLength = 32;
    public const int ServiceProofLength = 32;

    public const short CaLogin = 0x64;
    public const short CaConnectInfoChanged = 0x200;
    public const short CaLoginPcBang = 0x277;
    public const short CaLoginChannel = 0x2b0;
    public const short CaSsoLoginReq = 0x825;

    // Athena.NET-internal CharServer <-> LoginServer service authentication
    // (HMAC-SHA256 challenge/response - see Application.ServiceAuthProofCalculator
    // and ai/login-server.md). Not stock-iRO packets; the wire IDs/layouts are
    // Athena.NET's own and may change without affecting client compatibility.
    // Replaces the legacy username/password LcCharServerLogin/LcCharServerLoginAck.
    /// <summary>CharServer -&gt; LoginServer: ServiceId + server registration info. 56 bytes.</summary>
    public const short LcServiceHello = 0x2750;
    /// <summary>LoginServer -&gt; CharServer: one-time 32-byte nonce challenge. 34 bytes.</summary>
    public const short LcServiceAuthChallenge = 0x2751;
    /// <summary>CharServer -&gt; LoginServer: 32-byte HMAC-SHA256 proof. 34 bytes.</summary>
    public const short LcServiceAuthProof = 0x2752;
    /// <summary>LoginServer -&gt; CharServer: 1-byte result (0 = success). 3 bytes.</summary>
    public const short LcServiceAuthResult = 0x2753;

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

    public const short AcAcceptLogin = 0x0a4d;
    public const short AcRefuseLogin = 0x83e;
    public const short ScNotifyBan = 0x81;
}
