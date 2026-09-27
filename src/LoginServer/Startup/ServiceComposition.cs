using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Runtime;

namespace Athena.Net.LoginServer.Startup;

/// <summary>
/// Composition root for LoginServer's application-level services. Registers the
/// LoginDb EF Core context factory (scoped, short-lived contexts - never tied to a
/// TCP connection's lifetime) alongside the authentication/session abstractions
/// ClientSession depends on, so a future ASP.NET Core Identity implementation can
/// be swapped in by changing only this registration.
/// </summary>
public static class ServiceComposition
{
    public static (ServiceProvider Provider, bool DbAvailable) Build(
        InterConfig interConfig,
        SecretConfig secrets,
        LoginDbTableNames tableNames)
    {
        var services = new ServiceCollection();

        var dbAvailable = DbSetup.TryAddLoginDbContext(services, interConfig, secrets, tableNames);

        // Legacy (pre-Identity) implementation. Stateless, so a single shared
        // instance is safe across every connection.
        services.AddSingleton<IPlayerAuthenticationService, LegacyPlayerAuthenticationService>();

        // Each TCP connection needs its own "has this socket authenticated as a
        // service?" state, so this is transient rather than a shared singleton.
        services.AddTransient<IServiceAuthenticationService, ServiceAuthenticationService>();

        return (services.BuildServiceProvider(), dbAvailable);
    }
}
