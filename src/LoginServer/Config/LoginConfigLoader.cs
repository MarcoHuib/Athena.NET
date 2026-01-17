using System.Globalization;
using System.Net;

namespace Rathena.LoginServer.Config;

public static class LoginConfigLoader
{
    public static LoginConfig Load(string path)
    {
        var bindIp = IPAddress.Any;
        var loginPort = 6900;
        var logLogin = true;
        var useMd5Passwords = false;
        var dateFormat = "yyyy-MM-dd HH:mm:ss";
        var newAccountFlag = true;
        var accountNameMinLength = 6;
        var passwordMinLength = 6;
        var groupIdToConnect = -1;
        var minGroupIdToConnect = -1;
        var useWebAuthToken = true;
        var disableWebTokenDelayMs = 10000;
        var maxChars = 15;
        var maxCharVip = 6;
        var maxCharBilling = 0;
        var charPerAccount = maxChars - maxCharVip - maxCharBilling;
        var vipGroupId = 5;
        var vipCharIncrease = maxCharVip;
        var ipBanEnabled = true;
        var dynamicPassFailureBan = true;
        var dynamicPassFailureBanInterval = 5;
        var dynamicPassFailureBanLimit = 7;
        var dynamicPassFailureBanDuration = 5;
        var useDnsbl = false;
        var dnsblServers = string.Empty;
        var ipBanCleanupInterval = 60;
        var consoleEnabled = false;
        var allowedRegistrations = 1;
        var registrationWindowSeconds = 10;
        var startLimitedTimeSeconds = -1;
        var clientHashCheck = false;
        var clientHashRules = new List<ClientHashRule>();
        var ipSyncIntervalMinutes = 0;
        var usercountDisable = false;
        var usercountLow = 200;
        var usercountMedium = 500;
        var usercountHigh = 1000;

        if (!File.Exists(path))
        {
            Console.WriteLine($"Config not found: {path}. Using defaults.");
            return new LoginConfig
            {
                BindIp = bindIp,
                LoginPort = loginPort,
                LogLogin = logLogin,
                UseMd5Passwords = useMd5Passwords,
                DateFormat = dateFormat,
                NewAccountFlag = newAccountFlag,
                AccountNameMinLength = accountNameMinLength,
                PasswordMinLength = passwordMinLength,
                GroupIdToConnect = groupIdToConnect,
                MinGroupIdToConnect = minGroupIdToConnect,
                UseWebAuthToken = useWebAuthToken,
                DisableWebTokenDelayMs = disableWebTokenDelayMs,
                MaxChars = maxChars,
                MaxCharVip = maxCharVip,
                MaxCharBilling = maxCharBilling,
                CharPerAccount = charPerAccount,
                VipGroupId = vipGroupId,
                VipCharIncrease = vipCharIncrease,
                IpBanEnabled = ipBanEnabled,
                DynamicPassFailureBan = dynamicPassFailureBan,
                DynamicPassFailureBanIntervalMinutes = dynamicPassFailureBanInterval,
                DynamicPassFailureBanLimit = dynamicPassFailureBanLimit,
                DynamicPassFailureBanDurationMinutes = dynamicPassFailureBanDuration,
                UseDnsbl = useDnsbl,
                DnsblServers = dnsblServers,
                IpBanCleanupIntervalSeconds = ipBanCleanupInterval,
                ConsoleEnabled = consoleEnabled,
                AllowedRegistrations = allowedRegistrations,
                RegistrationWindowSeconds = registrationWindowSeconds,
                StartLimitedTimeSeconds = startLimitedTimeSeconds,
                ClientHashCheck = clientHashCheck,
                ClientHashRules = clientHashRules,
                IpSyncIntervalMinutes = ipSyncIntervalMinutes,
                UsercountDisable = usercountDisable,
                UsercountLow = usercountLow,
                UsercountMedium = usercountMedium,
                UsercountHigh = usercountHigh,
            };
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Equals("bind_ip", StringComparison.OrdinalIgnoreCase))
            {
                if (IPAddress.TryParse(value, out var ip))
                {
                    bindIp = ip;
                }
            }
            else if (key.Equals("login_port", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                {
                    loginPort = port;
                }
            }
            else if (key.Equals("log_login", StringComparison.OrdinalIgnoreCase))
            {
                logLogin = ParseBool(value, logLogin);
            }
            else if (key.Equals("use_MD5_passwords", StringComparison.OrdinalIgnoreCase))
            {
                useMd5Passwords = ParseBool(value, useMd5Passwords);
            }
            else if (key.Equals("date_format", StringComparison.OrdinalIgnoreCase))
            {
                dateFormat = value;
            }
            else if (key.Equals("new_account", StringComparison.OrdinalIgnoreCase))
            {
                newAccountFlag = ParseBool(value, newAccountFlag);
            }
            else if (key.Equals("acc_name_min_length", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    accountNameMinLength = parsed;
                }
            }
            else if (key.Equals("password_min_length", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    passwordMinLength = parsed;
                }
            }
            else if (key.Equals("group_id_to_connect", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    groupIdToConnect = parsed;
                }
            }
            else if (key.Equals("min_group_id_to_connect", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    minGroupIdToConnect = parsed;
                }
            }
            else if (key.Equals("use_web_auth_token", StringComparison.OrdinalIgnoreCase))
            {
                useWebAuthToken = ParseBool(value, useWebAuthToken);
            }
            else if (key.Equals("disable_webtoken_delay", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    disableWebTokenDelayMs = parsed;
                }
            }
            else if (key.Equals("chars_per_account", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    charPerAccount = parsed;
                }
            }
            else if (key.Equals("vip_group", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    vipGroupId = parsed;
                }
            }
            else if (key.Equals("vip_char_increase", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    vipCharIncrease = parsed;
                }
            }
            else if (key.Equals("ipban", StringComparison.OrdinalIgnoreCase))
            {
                ipBanEnabled = ParseBool(value, ipBanEnabled);
            }
            else if (key.Equals("dynamic_pass_failure_ban", StringComparison.OrdinalIgnoreCase))
            {
                dynamicPassFailureBan = ParseBool(value, dynamicPassFailureBan);
            }
            else if (key.Equals("dynamic_pass_failure_ban_interval", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    dynamicPassFailureBanInterval = parsed;
                }
            }
            else if (key.Equals("dynamic_pass_failure_ban_limit", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    dynamicPassFailureBanLimit = parsed;
                }
            }
            else if (key.Equals("dynamic_pass_failure_ban_duration", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    dynamicPassFailureBanDuration = parsed;
                }
            }
            else if (key.Equals("use_dnsbl", StringComparison.OrdinalIgnoreCase))
            {
                useDnsbl = ParseBool(value, useDnsbl);
            }
            else if (key.Equals("dnsbl_servs", StringComparison.OrdinalIgnoreCase))
            {
                dnsblServers = value;
            }
            else if (key.Equals("ipban_cleanup_interval", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    ipBanCleanupInterval = parsed;
                }
            }
            else if (key.Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                consoleEnabled = ParseBool(value, consoleEnabled);
            }
            else if (key.Equals("allowed_regs", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    allowedRegistrations = parsed;
                }
            }
            else if (key.Equals("time_allowed", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    registrationWindowSeconds = parsed;
                }
            }
            else if (key.Equals("start_limited_time", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    startLimitedTimeSeconds = parsed;
                }
            }
            else if (key.Equals("client_hash_check", StringComparison.OrdinalIgnoreCase))
            {
                clientHashCheck = ParseBool(value, clientHashCheck);
            }
            else if (key.Equals("client_hash", StringComparison.OrdinalIgnoreCase))
            {
                var rule = ParseClientHashRule(value);
                if (rule != null)
                {
                    clientHashRules.Add(rule);
                }
            }
            else if (key.Equals("ip_sync_interval", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    ipSyncIntervalMinutes = parsed;
                }
            }
            else if (key.Equals("usercount_disable", StringComparison.OrdinalIgnoreCase))
            {
                usercountDisable = ParseBool(value, usercountDisable);
            }
            else if (key.Equals("usercount_low", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    usercountLow = parsed;
                }
            }
            else if (key.Equals("usercount_medium", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    usercountMedium = parsed;
                }
            }
            else if (key.Equals("usercount_high", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    usercountHigh = parsed;
                }
            }
        }

        if (charPerAccount <= 0)
        {
            charPerAccount = maxChars - maxCharVip - maxCharBilling;
        }

        if (charPerAccount > maxChars)
        {
            charPerAccount = maxChars;
        }

        if (vipCharIncrease < 0)
        {
            vipCharIncrease = maxChars - charPerAccount;
        }
        else if (vipCharIncrease > maxChars - charPerAccount)
        {
            vipCharIncrease = maxChars - charPerAccount;
        }

        return new LoginConfig
        {
            BindIp = bindIp,
            LoginPort = loginPort,
            LogLogin = logLogin,
            UseMd5Passwords = useMd5Passwords,
            DateFormat = dateFormat,
            NewAccountFlag = newAccountFlag,
            AccountNameMinLength = accountNameMinLength,
            PasswordMinLength = passwordMinLength,
            GroupIdToConnect = groupIdToConnect,
            MinGroupIdToConnect = minGroupIdToConnect,
            UseWebAuthToken = useWebAuthToken,
            DisableWebTokenDelayMs = disableWebTokenDelayMs,
            MaxChars = maxChars,
            MaxCharVip = maxCharVip,
            MaxCharBilling = maxCharBilling,
            CharPerAccount = charPerAccount,
            VipGroupId = vipGroupId,
            VipCharIncrease = vipCharIncrease,
            IpBanEnabled = ipBanEnabled,
            DynamicPassFailureBan = dynamicPassFailureBan,
            DynamicPassFailureBanIntervalMinutes = dynamicPassFailureBanInterval,
            DynamicPassFailureBanLimit = dynamicPassFailureBanLimit,
            DynamicPassFailureBanDurationMinutes = dynamicPassFailureBanDuration,
            UseDnsbl = useDnsbl,
            DnsblServers = dnsblServers,
            IpBanCleanupIntervalSeconds = ipBanCleanupInterval,
            ConsoleEnabled = consoleEnabled,
            AllowedRegistrations = allowedRegistrations,
            RegistrationWindowSeconds = registrationWindowSeconds,
            StartLimitedTimeSeconds = startLimitedTimeSeconds,
            ClientHashCheck = clientHashCheck,
            ClientHashRules = clientHashRules,
            IpSyncIntervalMinutes = ipSyncIntervalMinutes,
            UsercountDisable = usercountDisable,
            UsercountLow = usercountLow,
            UsercountMedium = usercountMedium,
            UsercountHigh = usercountHigh,
        };
    }

    private static ClientHashRule? ParseClientHashRule(string value)
    {
        var parts = value.Split(',', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return null;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var groupId))
        {
            return null;
        }

        var hashPart = parts[1].Trim();
        if (hashPart.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new ClientHashRule
            {
                GroupId = groupId,
                AllowWithoutHash = true,
            };
        }

        if (!TryParseHex(hashPart, out var hash))
        {
            return null;
        }

        return new ClientHashRule
        {
            GroupId = groupId,
            Hash = hash,
            AllowWithoutHash = false,
        };
    }

    private static bool TryParseHex(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value.Length % 2 != 0)
        {
            return false;
        }

        var buffer = new byte[value.Length / 2];
        for (var i = 0; i < buffer.Length; i++)
        {
            if (!byte.TryParse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return false;
            }

            buffer[i] = b;
        }

        bytes = buffer;
        return true;
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index < 0 ? line : line[..index];
    }

    private static bool ParseBool(string value, bool fallback)
    {
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }
}
