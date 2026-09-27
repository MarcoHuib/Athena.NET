using Microsoft.EntityFrameworkCore;
using Athena.Net.LoginServer.Db.Entities;

namespace Athena.Net.LoginServer.Db;

public sealed class LoginDbContext : DbContext
{
    private readonly LoginDbTableNames _tableNames;

    public LoginDbContext(DbContextOptions<LoginDbContext> options, LoginDbTableNames? tableNames = null) : base(options)
    {
        _tableNames = tableNames ?? LoginDbTableNames.Default;
    }

    public DbSet<IpBanEntry> IpBanList => Set<IpBanEntry>();
    public DbSet<LoginLogEntry> LoginLogs => Set<LoginLogEntry>();
    public DbSet<AccountRegNum> AccountRegNums => Set<AccountRegNum>();
    public DbSet<AccountRegStr> AccountRegStrs => Set<AccountRegStr>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IpBanEntry>(entity =>
        {
            entity.ToTable(_tableNames.IpBanTable);
            entity.HasKey(e => new { e.List, e.BanTime });

            entity.Property(e => e.List)
                .HasColumnName("list")
                .HasMaxLength(15)
                .IsRequired()
                .HasDefaultValue(string.Empty);

            entity.Property(e => e.BanTime)
                .HasColumnName("btime");

            entity.Property(e => e.ReleaseTime)
                .HasColumnName("rtime");

            entity.Property(e => e.Reason)
                .HasColumnName("reason")
                .HasMaxLength(255)
                .IsRequired()
                .HasDefaultValue(string.Empty);
        });

        modelBuilder.Entity<LoginLogEntry>(entity =>
        {
            entity.ToTable(_tableNames.LoginLogTable);
            entity.HasNoKey();

            entity.Property(e => e.Time)
                .HasColumnName("time");

            entity.Property(e => e.Ip)
                .HasColumnName("ip")
                .HasMaxLength(15)
                .IsRequired();

            entity.HasIndex(e => e.Ip)
                .HasDatabaseName("ip");

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

            entity.HasIndex(e => e.AccountId)
                .HasDatabaseName("account_id");

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

            entity.HasIndex(e => e.AccountId)
                .HasDatabaseName("account_id");

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
