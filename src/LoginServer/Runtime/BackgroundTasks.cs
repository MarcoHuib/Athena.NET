using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Logging;
using Athena.Net.LoginServer.Net;

namespace Athena.Net.LoginServer.Runtime;

public static class BackgroundTasks
{
    public static Task StartIpBanCleanupAsync(LoginConfigStore configStore, Func<LoginDbContext?> dbFactory, CancellationToken cancellationToken)
    {
        if (!configStore.Current.IpBanEnabled || configStore.Current.IpBanCleanupIntervalSeconds <= 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var interval = configStore.Current.IpBanCleanupIntervalSeconds;
                    if (interval <= 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
                    var db = dbFactory();
                    if (db == null)
                    {
                        continue;
                    }

                    await using (db)
                    {
                        var now = DateTime.UtcNow;
                        var affected = await db.IpBanList
                            .Where(entry => entry.ReleaseTime <= now)
                            .ExecuteDeleteAsync(cancellationToken);
                        if (affected > 0)
                        {
                            LoginLogger.Info($"IPBan cleanup removed {affected} rows.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LoginLogger.Warning($"IPBan cleanup error: {ex.Message}");
                }
            }
        }, cancellationToken);
    }

    public static Task StartIpSyncAsync(LoginConfigStore configStore, CharServerRegistry charServers, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var interval = configStore.Current.IpSyncIntervalMinutes;
                if (interval <= 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    continue;
                }

                await Task.Delay(TimeSpan.FromMinutes(interval), cancellationToken);

                var payload = new byte[2];
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(0, 2), PacketConstants.LcIpSyncRequest);
                await charServers.SendToAllAsync(payload);
            }
        }, cancellationToken);
    }

    public static Task StartOnlineCleanupAsync(LoginState state, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(600);
        return Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, cancellationToken);
                    state.CleanupUnknownUsers(interval);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cancellationToken);
    }

    public static async Task DisableWebAuthTokenAsync(uint accountId, LoginConfigStore configStore, LoginState state, Func<LoginDbContext?> dbFactory, CancellationToken cancellationToken)
    {
        if (!configStore.Current.UseWebAuthToken)
        {
            return;
        }

        var delay = configStore.Current.DisableWebTokenDelayMs;
        if (delay > 0)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (state.TryGetOnlineUser(accountId, out _))
        {
            return;
        }

        var db = dbFactory();
        if (db == null)
        {
            return;
        }

        await using (db)
        {
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.AccountId == accountId, cancellationToken);
            if (account == null)
            {
                return;
            }

            account.WebAuthTokenEnabled = false;
            db.Entry(account).Property(a => a.WebAuthTokenEnabled).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
