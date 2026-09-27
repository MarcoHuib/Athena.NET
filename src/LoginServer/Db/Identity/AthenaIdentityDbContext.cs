using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.LoginServer.Db.Identity;

/// <summary>
/// Persistence boundary for human identity (ASP.NET Core Identity tables) and the
/// strictly-1:1 <see cref="AthenaGameAccount"/> table. Both live in one DbContext
/// so account provisioning can create the Identity user and the game account in a
/// single transaction, per the strict 1:1 relationship and the schema constraints
/// documented on <see cref="AthenaGameAccount"/>.
/// <para>
/// This context is separate from <see cref="LoginDbContext"/>, which continues to
/// own the legacy service-account rows (CharServer authentication), IP bans,
/// login audit logs, and the account registry tables - none of those are human
/// identity concerns.
/// </para>
/// </summary>
public sealed class AthenaIdentityDbContext : IdentityDbContext<AthenaIdentityUser, IdentityRole<Guid>, Guid>
{
    public AthenaIdentityDbContext(DbContextOptions<AthenaIdentityDbContext> options) : base(options)
    {
    }

    public DbSet<AthenaGameAccount> GameAccounts => Set<AthenaGameAccount>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AthenaIdentityUser>(entity =>
        {
            entity.ToTable("AthenaIdentityUsers");

            // Email uniqueness is enforced at the database level in addition to
            // Identity's own RequireUniqueEmail application-level check, since a
            // future website login path will authenticate by email.
            entity.HasIndex(u => u.NormalizedEmail)
                .IsUnique()
                .HasDatabaseName("EmailIndex");
        });

        builder.Entity<IdentityRole<Guid>>(entity => entity.ToTable("AthenaIdentityRoles"));
        builder.Entity<IdentityUserRole<Guid>>(entity => entity.ToTable("AthenaIdentityUserRoles"));
        builder.Entity<IdentityUserClaim<Guid>>(entity => entity.ToTable("AthenaIdentityUserClaims"));
        builder.Entity<IdentityUserLogin<Guid>>(entity => entity.ToTable("AthenaIdentityUserLogins"));
        builder.Entity<IdentityRoleClaim<Guid>>(entity => entity.ToTable("AthenaIdentityRoleClaims"));
        builder.Entity<IdentityUserToken<Guid>>(entity => entity.ToTable("AthenaIdentityUserTokens"));

        builder.Entity<AthenaGameAccount>(entity =>
        {
            entity.ToTable("AthenaGameAccounts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Sex)
                .HasMaxLength(1)
                .IsRequired()
                .HasDefaultValue("M");

            entity.Property(e => e.LastIp)
                .HasMaxLength(100)
                .IsRequired()
                .HasDefaultValue(string.Empty);

            entity.Property(e => e.Pincode)
                .HasMaxLength(4)
                .IsRequired()
                .HasDefaultValue(string.Empty);

            entity.Property(e => e.WebAuthToken)
                .HasMaxLength(17);

            // IdentityPlayerAuthenticationService.UpdateWebAuthTokenWithRetryAsync
            // regenerates the token and retries on DbUpdateException, which only
            // does anything useful if the database actually rejects duplicates.
            // Filtered (not a plain unique index) because most accounts have a
            // null token (never logged in, or UseWebAuthToken disabled) and SQL
            // Server's plain unique index only allows a single NULL row.
            entity.HasIndex(e => e.WebAuthToken)
                .IsUnique()
                .HasFilter("[WebAuthToken] IS NOT NULL")
                .HasDatabaseName("IX_AthenaGameAccounts_WebAuthToken");

            entity.Property(e => e.Birthdate)
                .HasColumnType("date");

            // Strict 1:1 with the owning Identity user. Restrict (not cascade)
            // delete: deleting an Identity user must never silently delete
            // durable game-account data.
            entity.HasIndex(e => e.IdentityUserId).IsUnique();
            entity.HasOne<AthenaIdentityUser>()
                .WithOne()
                .HasForeignKey<AthenaGameAccount>(e => e.IdentityUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Legacy uint32 compatibility identifier for the stock iRO wire
            // protocol; must remain unique across all game accounts.
            entity.HasIndex(e => e.RagnarokAccountId).IsUnique();
        });

        // Backs SqlServerSequenceRagnarokAccountIdAllocator: a SQL Server SEQUENCE
        // is atomic under concurrent provisioning without an in-process counter or
        // holding a transaction lock across the allocation. Starts above the
        // legacy login table's reserved/service-account range and its own
        // IDENTITY(2000000,1) starting point, matching the previous MAX()+1
        // allocation's starting range.
        //
        // Deliberately NOT declared via builder.HasSequence(...): that would put
        // it in the EF model, and Database.EnsureCreated() (used by this
        // project's SQLite-backed tests, since SQLite has no migrations
        // provider) creates schema straight from the model and throws
        // NotSupportedException the moment a sequence appears in it - SQLite has
        // no equivalent. The sequence exists only via the
        // AddRagnarokAccountIdSequence migration, which is fine: nothing else in
        // the model references it (SqlServerSequenceRagnarokAccountIdAllocator
        // queries it with a raw "NEXT VALUE FOR" statement, not a column default).
    }
}
