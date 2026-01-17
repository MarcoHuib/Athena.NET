using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Rathena.LoginServer.Config;
using Rathena.LoginServer.Db;
using Rathena.LoginServer.Db.Entities;
using Microsoft.EntityFrameworkCore;

namespace Rathena.LoginServer.Net;

public static class SelfTest
{
    public static async Task<int> RunAsync(LoginConfig config, Func<LoginDbContext?> dbFactory)
    {
        var testConfig = CreateTestConfig(config);
        var configStore = new LoginConfigStore(testConfig);
        var charServers = new CharServerRegistry();
        var state = new LoginState();
        var subnetConfig = new SubnetConfig();

        using var cts = new CancellationTokenSource();
        var server = new LoginTcpServer(configStore, dbFactory, charServers, state, subnetConfig);
        var serverTask = server.RunAsync(cts.Token);

        var ready = await WaitForPortAsync(server, TimeSpan.FromSeconds(2));
        if (!ready)
        {
            cts.Cancel();
            return 1;
        }

        var ok = true;
        var hashOk = await ProbeHashAsync(server.BoundPort);
        Console.WriteLine($"Self-test: hash {(hashOk ? "ok" : "failed")}");
        ok &= hashOk;

        var canUseDb = await CanUseLoginDbAsync(dbFactory);
        if (canUseDb)
        {
            var refuseOk = await ProbeLoginRefuseAsync(server.BoundPort);
            Console.WriteLine($"Self-test: login-refuse {(refuseOk ? "ok" : "failed")}");
            ok &= refuseOk;
        }
        else
        {
            Console.WriteLine("Self-test: login-refuse skipped (db unavailable or missing tables).");
        }

        var keepAliveOk = await ProbeKeepAliveAsync(server.BoundPort);
        Console.WriteLine($"Self-test: keepalive {(keepAliveOk ? "ok" : "failed")}");
        ok &= keepAliveOk;

        var dbTests = await ProbeDbLoginFlowAsync(server.BoundPort, configStore, dbFactory, charServers, state, canUseDb);
        if (dbTests.ran)
        {
            Console.WriteLine($"Self-test: login-flow {(dbTests.ok ? "ok" : "failed")}");
            ok &= dbTests.ok;
        }
        else
        {
            Console.WriteLine("Self-test: login-flow skipped (db unavailable or missing tables).");
        }

        cts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(500));

        Console.WriteLine(ok ? "Self-test: OK" : "Self-test: FAILED");
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
            Console.WriteLine("Self-test: hash missing header.");
            return false;
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(0, 2));
        var length = BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(2, 2));
        if (packetType != PacketConstants.AcAckHash || length < 4)
        {
            Console.WriteLine($"Self-test: hash unexpected response 0x{packetType:X4} len={length}.");
            return false;
        }

        var body = await ReadExactAsync(stream, length - 4);
        if (body.Length != length - 4)
        {
            Console.WriteLine($"Self-test: hash body size mismatch {body.Length} != {length - 4}.");
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

    private static async Task<bool> ProbeKeepAliveAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var stream = client.GetStream();

        var packet = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(0, 2), PacketConstants.LcKeepAliveRequest);
        await stream.WriteAsync(packet);

        var resp = await ReadExactAsync(stream, 2);
        if (resp.Length < 2)
        {
            Console.WriteLine("Self-test: keepalive missing response.");
            return false;
        }

        var packetType = BinaryPrimitives.ReadInt16LittleEndian(resp);
        if (packetType != PacketConstants.LcKeepAliveResponse)
        {
            Console.WriteLine($"Self-test: keepalive unexpected response 0x{packetType:X4}.");
        }
        return packetType == PacketConstants.LcKeepAliveResponse;
    }

    private static async Task<(bool ran, bool ok)> ProbeDbLoginFlowAsync(
        int port,
        LoginConfigStore configStore,
        Func<LoginDbContext?> dbFactory,
        CharServerRegistry charServers,
        LoginState state,
        bool canUseDb)
    {
        if (!canUseDb)
        {
            return (false, true);
        }

        var db = dbFactory();
        if (db == null)
        {
            return (false, true);
        }

        await using (db)
        {
            var username = $"selftest_{Guid.NewGuid():N}".Substring(0, 16);
            var password = "SelfTest1!";
            var storedPass = configStore.Current.UseMd5Passwords ? Md5Hex(Encoding.ASCII.GetBytes(password)) : password;

            var account = new LoginAccount
            {
                UserId = username,
                UserPass = storedPass,
                Sex = "M",
                Email = "selftest@example.com",
                GroupId = 0,
                State = 0,
                UnbanTime = 0,
                ExpirationTime = 0,
                LoginCount = 0,
                LastLogin = null,
                LastIp = "127.0.0.1",
                Birthdate = null,
                CharacterSlots = (byte)configStore.Current.CharPerAccount,
                Pincode = string.Empty,
                PincodeChange = 0,
                VipTime = 0,
                OldGroup = 0,
                WebAuthToken = null,
                WebAuthTokenEnabled = false,
            };

            db.Accounts.Add(account);
            await db.SaveChangesAsync();

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
                    Console.WriteLine($"Self-test: usercount mismatch {userCount} != {expected}.");
                    return (true, false);
                }

                state.RemoveOnlineUser(account.AccountId);
                state.RemoveAuthNode(account.AccountId);

                state.AddOnlineUser(1, account.AccountId);
                var alreadyOnlineOk = await ProbeAlreadyOnlineAsync(port, username, password);
                if (!alreadyOnlineOk)
                {
                    return (true, false);
                }

                return (true, true);
            }
            finally
            {
                state.RemoveOnlineUser(account.AccountId);
                state.RemoveAuthNode(account.AccountId);
                charServers.Unregister(1);
                db.Accounts.Remove(account);
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

    private static string Md5Hex(byte[] data)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(data);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
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
