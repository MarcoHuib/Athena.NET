using Microsoft.Extensions.DependencyInjection;
using Athena.Net.LoginServer.Application;
using Athena.Net.LoginServer.Config;
using Athena.Net.LoginServer.Db;
using Athena.Net.LoginServer.Startup;

namespace Athena.Net.LoginServer.Tests.Startup;

/// <summary>
/// Regression coverage for the production composition root. LoginServer's own
/// unit tests build their own hand-wired fakes/fixtures almost everywhere, which
/// is exactly how a missing registration in <see cref="ServiceComposition"/>
/// (LoginConfigStore was never registered, so resolving
/// IPlayerAuthenticationService crashed the first real player login) went
/// unnoticed: nothing ever built the real container. These tests build the real
/// composition root with a valid-looking (but unreachable) SQL Server
/// configuration and resolve every service ClientSession depends on.
/// </summary>
public sealed class ServiceCompositionTests
{
    private static (InterConfig InterConfig, SecretConfig Secrets, LoginDbTableNames TableNames, LoginConfigStore ConfigStore) CreateValidConfig()
    {
        var interConfig = new InterConfig
        {
            LoginDbProvider = "sqlserver",
            LoginDbConnectionString = "Server=localhost;Database=AthenaLoginDb;User Id=sa;Password=not-a-real-password;TrustServerCertificate=True;",
        };
        var secrets = new SecretConfig();
        var tableNames = new LoginDbTableNames
        {
            IpBanTable = interConfig.IpBanTable,
            LoginLogTable = interConfig.LoginLogTable,
            GlobalAccRegNumTable = interConfig.GlobalAccRegNumTable,
            GlobalAccRegStrTable = interConfig.GlobalAccRegStrTable,
        };
        var configStore = new LoginConfigStore(new LoginConfig());

        return (interConfig, secrets, tableNames, configStore);
    }

    [Fact]
    public void Build_WithValidConfiguration_DoesNotThrowDuringValidation()
    {
        var (interConfig, secrets, tableNames, configStore) = CreateValidConfig();

        using var provider = ServiceComposition.Build(interConfig, secrets, tableNames, configStore).Provider;

        Assert.NotNull(provider);
    }

    [Fact]
    public void Build_WithValidConfiguration_ResolvesIPlayerAuthenticationService()
    {
        var (interConfig, secrets, tableNames, configStore) = CreateValidConfig();
        using var provider = ServiceComposition.Build(interConfig, secrets, tableNames, configStore).Provider;

        // Regression for the LoginConfigStore-not-registered bug: resolving this
        // singleton constructs an IdentityPlayerAuthenticationService, whose
        // constructor requires LoginConfigStore from the container.
        var playerAuth = provider.GetRequiredService<IPlayerAuthenticationService>();

        Assert.IsType<IdentityPlayerAuthenticationService>(playerAuth);
    }

    [Fact]
    public void Build_WithValidConfiguration_ResolvesIServiceAuthenticationService()
    {
        var (interConfig, secrets, tableNames, configStore) = CreateValidConfig();
        using var provider = ServiceComposition.Build(interConfig, secrets, tableNames, configStore).Provider;

        var serviceAuth = provider.GetRequiredService<IServiceAuthenticationService>();

        Assert.NotNull(serviceAuth);
    }

    [Fact]
    public void Build_WithValidConfiguration_ResolvesIPlayerAccountProvisioningServiceFromAScope()
    {
        var (interConfig, secrets, tableNames, configStore) = CreateValidConfig();
        using var provider = ServiceComposition.Build(interConfig, secrets, tableNames, configStore).Provider;

        using var scope = provider.CreateScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IPlayerAccountProvisioningService>();

        Assert.NotNull(provisioning);
    }

    [Fact]
    public void Build_WithUnsupportedDbProvider_StillResolvesEveryClientSessionDependency()
    {
        // Mirrors a sandbox/dev machine with no DB configured at all: DbAvailable
        // and IdentityDbAvailable are both false, but the container must still be
        // buildable and every ClientSession dependency must still resolve to a
        // safe "unavailable" fallback rather than throwing.
        var interConfig = new InterConfig { LoginDbProvider = "mysql", LoginDbConnectionString = string.Empty };
        var secrets = new SecretConfig();
        var tableNames = new LoginDbTableNames();
        var configStore = new LoginConfigStore(new LoginConfig());

        var composition = ServiceComposition.Build(interConfig, secrets, tableNames, configStore);
        using var provider = composition.Provider;

        Assert.False(composition.DbAvailable);
        Assert.False(composition.IdentityDbAvailable);

        var playerAuth = provider.GetRequiredService<IPlayerAuthenticationService>();
        Assert.IsType<UnavailablePlayerAuthenticationService>(playerAuth);

        var serviceAuth = provider.GetRequiredService<IServiceAuthenticationService>();
        Assert.NotNull(serviceAuth);
    }
}
