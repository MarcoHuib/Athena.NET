using System.Net;

namespace Athena.Net.LoginServer.Config;

public sealed class LoginConfig
{
    public IPAddress BindIp { get; init; } = IPAddress.Any;
    public int LoginPort { get; init; } = 6900;
    public bool LogLogin { get; init; } = true;
    public bool UseMd5Passwords { get; init; }
    public string DateFormat { get; init; } = "yyyy-MM-dd HH:mm:ss";
    public bool NewAccountFlag { get; init; }
    public int AccountNameMinLength { get; init; } = 6;
    public int PasswordMinLength { get; init; } = 6;
    public int GroupIdToConnect { get; init; } = -1;
    public int MinGroupIdToConnect { get; init; } = -1;
    public bool UseWebAuthToken { get; init; }
    public int DisableWebTokenDelayMs { get; init; }
    public int MaxChars { get; init; } = 15;
    public int MaxCharVip { get; init; } = 6;
    public int MaxCharBilling { get; init; }
    public int CharPerAccount { get; init; } = 9;
    public int VipGroupId { get; init; } = 5;
    public int VipCharIncrease { get; init; } = 6;
    public bool IpBanEnabled { get; init; }
    public bool DynamicPassFailureBan { get; init; }
    public int DynamicPassFailureBanIntervalMinutes { get; init; } = 5;
    public int DynamicPassFailureBanLimit { get; init; } = 7;
    public int DynamicPassFailureBanDurationMinutes { get; init; } = 5;
    public bool UseDnsbl { get; init; }
    public string DnsblServers { get; init; } = string.Empty;
    public int IpBanCleanupIntervalSeconds { get; init; } = 60;
    public bool ConsoleEnabled { get; init; }
    public int AllowedRegistrations { get; init; } = 1;
    public int RegistrationWindowSeconds { get; init; } = 10;
    public int StartLimitedTimeSeconds { get; init; } = -1;
    public bool ClientHashCheck { get; init; }
    public IReadOnlyList<ClientHashRule> ClientHashRules { get; init; } = Array.Empty<ClientHashRule>();
    public int IpSyncIntervalMinutes { get; init; }
    public bool UsercountDisable { get; init; }
    public int UsercountLow { get; init; } = 200;
    public int UsercountMedium { get; init; } = 500;
    public int UsercountHigh { get; init; } = 1000;
}

public sealed class ClientHashRule
{
    public int GroupId { get; init; }
    public byte[]? Hash { get; init; }
    public bool AllowWithoutHash { get; init; }
}
