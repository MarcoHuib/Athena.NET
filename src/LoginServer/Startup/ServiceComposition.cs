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
        LoginDbTableNames tableNames,
        LoginConfigStore configStore)
    {
        var services = new ServiceCollection();

        // IdentityPlayerAuthenticationService (and any other application service
        // that needs live config, e.g. UseWebAuthToken) resolves LoginConfigStore
        // from this container. Registering the SAME instance the rest of the app
        // uses (rather than letting DI construct its own) keeps config reload
        // (LoginConfigStore.Reload()) visible everywhere.
        services.AddSingleton(configStore);

        // Resolved once at startup and shared: the ServiceToken itself never
        // changes at runtime, so there is no reload concern like LoginConfigStore's.
        services.AddSingleton(new CharServerServiceTokenProvider(secrets));

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

            services.AddScoped<IRagnarokAccountIdAllocator, SqlServerSequenceRagnarokAccountIdAllocator>();
            services.AddScoped<IPlayerAccountProvisioningService, PlayerAccountProvisioningService>();

            // Singleton is safe: IdentityPlayerAuthenticationService holds no
            // per-call state itself, it creates a fresh DI scope (and therefore a
            // fresh UserManager/AthenaIdentityDbContext) per authentication call.
            services.AddSingleton<IPlayerAuthenticationService, IdentityPlayerAuthenticationService>();

            // Same pattern as IdentityPlayerAuthenticationService: no per-call
            // state, a fresh scope (and UserManager/AthenaIdentityDbContext) per
            // call, safe as a singleton on CharServer's long-lived connection.
            services.AddSingleton<IPlayerIdentityAccountService, PlayerIdentityAccountService>();
        }
        else
        {
            // Mirrors LoginDb's own Func<LoginDbContext?> returning null instead
            // of crashing: every player login safely fails rather than the server
            // bypassing authentication.
            services.AddSingleton<IPlayerAuthenticationService, UnavailablePlayerAuthenticationService>();
            services.AddSingleton<IPlayerIdentityAccountService, UnavailablePlayerIdentityAccountService>();
        }

        // Each TCP connection needs its own "has this socket authenticated as a
        // service?" state, so this is transient rather than a shared singleton.
        services.AddTransient<IServiceAuthenticationService, ServiceAuthenticationService>();

        // ValidateOnBuild + ValidateScopes catch missing/misscoped registrations
        // (like the LoginConfigStore omission this composition root once had) at
        // startup instead of at the first real login attempt. ValidateScopes also
        // enforces that scoped services (UserManager, the DbContexts, etc.) are
        // only ever resolved from a created IServiceScope, never straight from
        // this root provider.
        var providerOptions = new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true };
        return (services.BuildServiceProvider(providerOptions), dbAvailable, identityDbAvailable);
    }
}
