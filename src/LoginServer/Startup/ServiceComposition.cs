using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Db.Identity;
using Athena.Net.LoginServer.Runtime;

namespace Athena.Net.LoginServer.Startup;

/// <summary>
/// Composition root for LoginServer's application-level services. Registers the
/// LoginDb EF Core context factory (scoped, short-lived contexts - never tied to a
/// TCP connection's lifetime), ASP.NET Core Identity plus the AthenaGameAccount
/// schema, and the authentication/session abstractions ClientSession depends on.
/// </summary>
public static class ServiceComposition
{
    public static (ServiceProvider Provider, bool DbAvailable, bool IdentityDbAvailable) Build(
        InterConfig interConfig,
        SecretConfig secrets,
        LoginDbTableNames tableNames)
    {
        var services = new ServiceCollection();

        var dbAvailable = DbSetup.TryAddLoginDbContext(services, interConfig, secrets, tableNames);
        var identityDbAvailable = IdentityDbSetup.TryAddAthenaIdentityDbContext(services, interConfig, secrets);

        if (identityDbAvailable)
        {
            services.AddIdentityCore<AthenaIdentityUser>(options =>
            {
                // Identity.UserName is the Ragnarok login username entered in the
                // stock client's existing 0x0064 request; Email is a separate
                // identity intended for future website/account login. Password
                // complexity intentionally mirrors the legacy min-length-only
                // policy rather than Identity's default complexity rules, so
                // existing account-creation tooling (create-player-account.sh,
                // the console `create:` command) is not silently broken.
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 6;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
            })
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<AthenaIdentityDbContext>();

            services.AddScoped<IPlayerAccountProvisioningService, PlayerAccountProvisioningService>();

            // Singleton is safe: IdentityPlayerAuthenticationService holds no
            // per-call state itself, it creates a fresh DI scope (and therefore a
            // fresh UserManager/AthenaIdentityDbContext) per authentication call.
            services.AddSingleton<IPlayerAuthenticationService, IdentityPlayerAuthenticationService>();
        }
        else
        {
            // Mirrors LoginDb's own Func<LoginDbContext?> returning null instead
            // of crashing: every player login safely fails rather than the server
            // bypassing authentication.
            services.AddSingleton<IPlayerAuthenticationService, UnavailablePlayerAuthenticationService>();
        }

        // Each TCP connection needs its own "has this socket authenticated as a
        // service?" state, so this is transient rather than a shared singleton.
        services.AddTransient<IServiceAuthenticationService, ServiceAuthenticationService>();

        return (services.BuildServiceProvider(), dbAvailable, identityDbAvailable);
    }
}
