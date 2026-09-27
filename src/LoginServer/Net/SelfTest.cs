using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Entities;
using Athena.Net.LoginServer.Logging;

namespace Athena.Net.LoginServer.Net;

public static class SelfTest
{
    public static async Task<int> RunAsync(LoginConfig config, Func<LoginDbContext?> dbFactory, IServiceProvider serviceProvider)
    {
        var testConfig = CreateTestConfig(config);
        var configStore = new LoginConfigStore(testConfig);
        var charServers = new CharServerRegistry();
        var state = new LoginState();
        var subnetConfig = new SubnetConfig();

        using var cts = new CancellationTokenSource();
        var messageStore = new LoginMessageStore(new LoginMessageCatalog(new Dictionary<uint, string>()));
        var server = new LoginTcpServer(configStore, messageStore, dbFactory, () => null, charServers, state, subnetConfig, serviceProvider);
        var serverTask = server.RunAsync(cts.Token);

        var ready = await WaitForPortAsync(server, TimeSpan.FromSeconds(2));
        if (!ready)
        {
            cts.Cancel();
            return 1;
        }

        var ok = true;
        var hashOk = await ProbeHashAsync(server.BoundPort);
        LoginLogger.Status($"Self-test: hash {(hashOk ? "ok" : "failed")}");
        ok &= hashOk;

        var canUseDb = await CanUseLoginDbAsync(dbFactory);
        if (canUseDb)
        {
            var refuseOk = await ProbeLoginRefuseAsync(server.BoundPort);
            LoginLogger.Status($"Self-test: login-refuse {(refuseOk ? "ok" : "failed")}");
            ok &= refuseOk;
        }
        else
        {
            LoginLogger.Info("Self-test: login-refuse skipped (db unavailable or missing tables).");
        }

        var dbTests = await ProbeDbLoginFlowAsync(server.BoundPort, configStore, dbFactory, charServers, state, serviceProvider, canUseDb);
        if (dbTests.ran)
        {
            LoginLogger.Status($"Self-test: login-flow {(dbTests.ok ? "ok" : "failed")}");
            ok &= dbTests.ok;
        }
        else
        {
            LoginLogger.Info("Self-test: login-flow skipped (db unavailable or missing tables).");
        }

        cts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(500));

        LoginLogger.Status(ok ? "Self-test: OK" : "Self-test: FAILED");
        return ok ? 0 : 2;
    }

    private static LoginConfig CreateTestConfig(LoginConfig config)
    {
        return new LoginConfig
        {
            BindIp = IPAddress.Loopback,
            LoginPort = 0,
            LogLogin = false,
            UseMd5Passwords = config.UseMd5Passwords,
            DateFormat = config.DateFormat,
            NewAccountFlag = config.NewAccountFlag,
            AccountNameMinLength = config.AccountNameMinLength,
            PasswordMinLength = config.PasswordMinLength,
            GroupIdToConnect = config.GroupIdToConnect,
            MinGroupIdToConnect = config.MinGroupIdToConnect,
            UseWebAuthToken = config.UseWebAuthToken,
            DisableWebTokenDelayMs = config.DisableWebTokenDelayMs,
            MaxChars = config.MaxChars,
            MaxCharVip = config.MaxCharVip,
            MaxCharBilling = config.MaxCharBilling,
            CharPerAccount = config.CharPerAccount,
            VipGroupId = config.VipGroupId,
            VipCharIncrease = config.VipCharIncrease,
            IpBanEnabled = false,
            DynamicPassFailureBan = false,
            DynamicPassFailureBanIntervalMinutes = config.DynamicPassFailureBanIntervalMinutes,
            DynamicPassFailureBanLimit = config.DynamicPassFailureBanLimit,
            DynamicPassFailureBanDurationMinutes = config.DynamicPassFailureBanDurationMinutes,
            UseDnsbl = false,
            DnsblServers = string.Empty,
            IpBanCleanupIntervalSeconds = config.IpBanCleanupIntervalSeconds,
            ConsoleEnabled = false,
            AllowedRegistrations = config.AllowedRegistrations,
            RegistrationWindowSeconds = config.RegistrationWindowSeconds,
            StartLimitedTimeSeconds = config.StartLimitedTimeSeconds,
            ClientHashCheck = config.ClientHashCheck,
            ClientHashRules = config.ClientHashRules,
            IpSyncIntervalMinutes = config.IpSyncIntervalMinutes,
            UsercountDisable = config.UsercountDisable,
            UsercountLow = config.UsercountLow,
            UsercountMedium = config.UsercountMedium,
            UsercountHigh = config.UsercountHigh,
        };
    }

    private static async Task<bool> ProbeHashAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var req = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(req.AsSpan(0, 2), PacketConstants.CaReqHash);
        await stream.WriteAsync(req);

        var header = await ReadExactAsync(stream, 4);
        if (header.Length < 4)
        {
            LoginLogger.Warning("Self-test: hash missing header.");
            return false;
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(0, 2));
        var length = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(2, 2));
        if (packetType != PacketConstants.AcAckHash || length < 4)
        {
            LoginLogger.Warning($"Self-test: hash unexpected response 0x{packetType:X4} len={length}.");
            return false;
        }

        var body = await ReadExactAsync(stream, length - 4);
        if (body.Length != length - 4)
        {
            LoginLogger.Warning($"Self-test: hash body size mismatch {body.Length} != {length - 4}.");
            return false;
        }

        return true;
    }

    private static async Task<bool> ProbeLoginRefuseAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var packet = new byte[2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 0);
        WriteFixedString(packet, 6, PacketConstants.NameLength, "test");
        WriteFixedString(packet, 6 + PacketConstants.NameLength, PacketConstants.NameLength, "test");
        packet[^1] = 1;

        await stream.WriteAsync(packet);

        var resp = await ReadExactAsync(stream, 2);
        if (resp.Length < 2)
        {
            return false;
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(resp);
        return packetType == PacketConstants.AcRefuseLogin;
    }

    private static async Task<(bool ran, bool ok)> ProbeDbLoginFlowAsync(
        int port,
        LoginConfigStore configStore,
        Func<LoginDbContext?> dbFactory,
        CharServerRegistry charServers,
        LoginState state,
        IServiceProvider serviceProvider,
        bool canUseDb)
    {
        if (!canUseDb)
        {
            return (false, true);
        }

        using var provisioningScope = serviceProvider.CreateScope();
        var provisioning = provisioningScope.ServiceProvider.GetService<IPlayerAccountProvisioningService>();
        if (provisioning == null)
        {
            LoginLogger.Info("Self-test: login-flow skipped (Identity DB unavailable).");
            return (false, true);
        }

        var username = $"selftest_{Guid.NewGuid():N}".Substring(0, 16);
        var password = "SelfTest1!";
        var provisioned = await provisioning.ProvisionAsync(username, $"{username}@example.com", password, 'M', CancellationToken.None);
        if (!provisioned.Success)
        {
            LoginLogger.Warning($"Self-test: unable to provision test account ({provisioned.ErrorMessage}).");
            return (true, false);
        }

        var accountId = provisioned.RagnarokAccountId;

        try
        {
            var charServer = new CharServerInfo
            {
                Name = "SelfTest",
                Ip = IPAddress.Loopback,
                Port = 6121,
                Type = 0,
                IsNew = 0,
                Users = 0,
                Connection = null,
            };
            charServers.Register(1, charServer);

            var accept = await ProbeLoginAcceptAsync(port, username, password, configStore.Current);
            var acceptOk = accept.ok;
            var userCount = accept.userCount;
            if (!acceptOk)
            {
                return (true, false);
            }

            var expected = MapUserCount(configStore.Current, 0);
            if (userCount != expected)
            {
                LoginLogger.Warning($"Self-test: usercount mismatch {userCount} != {expected}.");
                return (true, false);
            }

            state.RemoveOnlineUser(accountId);
            state.RemoveAuthNode(accountId);

            state.AddOnlineUser(1, accountId);
            var alreadyOnlineOk = await ProbeAlreadyOnlineAsync(port, username, password);
            if (!alreadyOnlineOk)
            {
                return (true, false);
            }

            return (true, true);
        }
        finally
        {
            state.RemoveOnlineUser(accountId);
            state.RemoveAuthNode(accountId);
            charServers.Unregister(1);

            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetService<Db.Identity.AthenaIdentityDbContext>();
            if (db != null)
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == provisioned.IdentityUserId);
                if (user != null)
                {
                    db.Users.Remove(user);
                }

                var gameAccount = await db.GameAccounts.FirstOrDefaultAsync(a => a.Id == provisioned.GameAccountId);
                if (gameAccount != null)
                {
                    db.GameAccounts.Remove(gameAccount);
                }

                await db.SaveChangesAsync();
            }
        }
    }

    private static async Task<(bool ok, ushort userCount)> ProbeLoginAcceptAsync(int port, string username, string password, LoginConfig config)
    {
        var userCount = (ushort)0;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var packet = new byte[2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 0);
        WriteFixedString(packet, 6, PacketConstants.NameLength, username);
        WriteFixedString(packet, 6 + PacketConstants.NameLength, PacketConstants.NameLength, password);
        packet[^1] = 1;
        await stream.WriteAsync(packet);

        var header = await ReadExactAsync(stream, 4);
        if (header.Length < 4)
        {
            return (false, userCount);
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(0, 2));
        var length = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(2, 2));
        if (packetType != PacketConstants.AcAcceptLogin || length < 64)
        {
            return (false, userCount);
        }

        var body = await ReadExactAsync(stream, length - 4);
        if (body.Length != length - 4)
        {
            return (false, userCount);
        }

        var response = new byte[length];
        Buffer.BlockCopy(header, 0, response, 0, 4);
        Buffer.BlockCopy(body, 0, response, 4, body.Length);

        var serverEntryOffset = 47 + PacketConstants.WebAuthTokenLength;
        if (response.Length >= serverEntryOffset + 28)
        {
            userCount = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(serverEntryOffset + 26, 2));
        }

        return (true, userCount);
    }

    private static async Task<bool> ProbeAlreadyOnlineAsync(int port, string username, string password)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var packet = new byte[2 + 4 + PacketConstants.NameLength + PacketConstants.NameLength + 1];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.CaLogin);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 0);
        WriteFixedString(packet, 6, PacketConstants.NameLength, username);
        WriteFixedString(packet, 6 + PacketConstants.NameLength, PacketConstants.NameLength, password);
        packet[^1] = 1;
        await stream.WriteAsync(packet);

        var resp = await ReadExactAsync(stream, 3);
        if (resp.Length < 3)
        {
            return false;
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(resp.AsSpan(0, 2));
        return packetType == PacketConstants.ScNotifyBan && resp[2] == 8;
    }

    private static async Task<bool> WaitForPortAsync(LoginTcpServer server, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (server.BoundPort > 0)
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var bytes = await stream.ReadAsync(buffer.AsMemory(read, length - read));
            if (bytes == 0)
            {
                return Array.Empty<byte>();
            }

            read += bytes;
        }

        return buffer;
    }

    private static void WriteFixedString(byte[] buffer, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, buffer, offset, Math.Min(length, bytes.Length));
    }

    private static ushort MapUserCount(LoginConfig config, int users)
    {
        if (config.UsercountDisable)
        {
            return 4;
        }

        if (users <= config.UsercountLow)
        {
            return 0;
        }

        if (users <= config.UsercountMedium)
        {
            return 1;
        }

        if (users <= config.UsercountHigh)
        {
            return 2;
        }

        return 3;
    }

    private static async Task<bool> CanUseLoginDbAsync(Func<LoginDbContext?> dbFactory)
    {
        var db = dbFactory();
        if (db == null)
        {
            return false;
        }

        await using (db)
        {
            try
            {
                if (!await db.Database.CanConnectAsync())
                {
                    return false;
                }

                await db.Accounts.AnyAsync();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
