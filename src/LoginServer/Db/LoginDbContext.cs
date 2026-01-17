using Microsoft.EntityFrameworkCore;
using Rathena.LoginServer.Db.Entities;

namespace Rathena.LoginServer.Db;

public sealed class LoginDbContext : DbContext
{
    private readonly LoginDbTableNames _tableNames;

    public LoginDbContext(DbContextOptions<LoginDbContext> options, LoginDbTableNames? tableNames = null) : base(options)
    {
        _tableNames = tableNames ?? LoginDbTableNames.Default;
    }

    public DbSet<LoginAccount> Accounts => Set<LoginAccount>();
    public DbSet<IpBanEntry> IpBanList => Set<IpBanEntry>();
    public DbSet<LoginLogEntry> LoginLogs => Set<LoginLogEntry>();
    public DbSet<AccountRegNum> AccountRegNums => Set<AccountRegNum>();
    public DbSet<AccountRegStr> AccountRegStrs => Set<AccountRegStr>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var isMySql = Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true;

        modelBuilder.Entity<LoginAccount>(entity =>
        {
            entity.ToTable(_tableNames.AccountTable);
            entity.HasKey(e => e.AccountId);

            entity.Property(e => e.AccountId)
                .HasColumnName("account_id")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.UserId)
                .HasColumnName("userid")
                .HasMaxLength(23)
                .IsRequired();

            entity.Property(e => e.UserPass)
                .HasColumnName("user_pass")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.Sex)
                .HasColumnName("sex")
                .HasMaxLength(1)
                .IsRequired();

            if (isMySql)
            {
                entity.Property(e => e.Sex)
                    .HasColumnType("enum('M','F','S')");
            }

            entity.Property(e => e.Email)
                .HasColumnName("email")
                .HasMaxLength(39)
                .IsRequired();

            entity.Property(e => e.GroupId)
                .HasColumnName("group_id");

            entity.Property(e => e.State)
                .HasColumnName("state");

            entity.Property(e => e.UnbanTime)
                .HasColumnName("unban_time");

            entity.Property(e => e.ExpirationTime)
                .HasColumnName("expiration_time");

            entity.Property(e => e.LoginCount)
                .HasColumnName("logincount");

            entity.Property(e => e.LastLogin)
                .HasColumnName("lastlogin");

            entity.Property(e => e.LastIp)
                .HasColumnName("last_ip")
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.Birthdate)
                .HasColumnName("birthdate")
                .HasColumnType("date");

            entity.Property(e => e.CharacterSlots)
                .HasColumnName("character_slots");

            entity.Property(e => e.Pincode)
                .HasColumnName("pincode")
                .HasMaxLength(4)
                .IsRequired();

            entity.Property(e => e.PincodeChange)
                .HasColumnName("pincode_change");

            entity.Property(e => e.VipTime)
                .HasColumnName("vip_time");

            entity.Property(e => e.OldGroup)
                .HasColumnName("old_group");

            entity.Property(e => e.WebAuthToken)
                .HasColumnName("web_auth_token")
                .HasMaxLength(17);

            entity.Property(e => e.WebAuthTokenEnabled)
                .HasColumnName("web_auth_token_enabled");

            entity.HasIndex(e => e.UserId)
                .HasDatabaseName("name");

            entity.HasIndex(e => e.WebAuthToken)
                .IsUnique()
                .HasDatabaseName("web_auth_token_key");
        });

        modelBuilder.Entity<IpBanEntry>(entity =>
        {
            entity.ToTable(_tableNames.IpBanTable);
            entity.HasKey(e => new { e.List, e.BanTime });

            entity.Property(e => e.List)
                .HasColumnName("list")
                .HasMaxLength(15)
                .IsRequired();

            entity.Property(e => e.BanTime)
                .HasColumnName("btime");

            entity.Property(e => e.ReleaseTime)
                .HasColumnName("rtime");

            entity.Property(e => e.Reason)
                .HasColumnName("reason")
                .HasMaxLength(255)
                .IsRequired();
        });

        modelBuilder.Entity<LoginLogEntry>(entity =>
        {
            entity.ToTable(_tableNames.LoginLogTable);
            entity.HasKey(e => new { e.Time, e.Ip, e.User, e.ResultCode, e.Log });

            entity.Property(e => e.Time)
                .HasColumnName("time");

            entity.Property(e => e.Ip)
                .HasColumnName("ip")
                .HasMaxLength(15)
                .IsRequired();

            entity.Property(e => e.User)
                .HasColumnName("user")
                .HasMaxLength(23)
                .IsRequired();

            entity.Property(e => e.ResultCode)
                .HasColumnName("rcode");

            entity.Property(e => e.Log)
                .HasColumnName("log")
                .HasMaxLength(255)
                .IsRequired();
        });

        modelBuilder.Entity<AccountRegNum>(entity =>
        {
            entity.ToTable(_tableNames.GlobalAccRegNumTable);
            entity.HasKey(e => new { e.AccountId, e.Key, e.Index });

            entity.Property(e => e.AccountId)
                .HasColumnName("account_id");

            entity.Property(e => e.Key)
                .HasColumnName("key")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.Index)
                .HasColumnName("index");

            entity.Property(e => e.Value)
                .HasColumnName("value");
        });

        modelBuilder.Entity<AccountRegStr>(entity =>
        {
            entity.ToTable(_tableNames.GlobalAccRegStrTable);
            entity.HasKey(e => new { e.AccountId, e.Key, e.Index });

            entity.Property(e => e.AccountId)
                .HasColumnName("account_id");

            entity.Property(e => e.Key)
                .HasColumnName("key")
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(e => e.Index)
                .HasColumnName("index");

            entity.Property(e => e.Value)
                .HasColumnName("value")
                .HasMaxLength(254)
                .IsRequired();
        });
    }
}
