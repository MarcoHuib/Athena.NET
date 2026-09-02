using System.Text.Json;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    Environment.SetEnvironmentVariable(
        "ASPNETCORE_URLS",
        "http://127.0.0.1:18888");
}

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
{
    Environment.SetEnvironmentVariable(
        "ASPNETCORE_ENVIRONMENT",
        "Development");
}

EnsureSqlServerPassword();

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT")))
{
    Environment.SetEnvironmentVariable(
        "ASPIRE_ALLOW_UNSECURED_TRANSPORT",
        "true");
}

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_ENDPOINT_URL")) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL")))
{
    Environment.SetEnvironmentVariable(
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL",
        "http://localhost:4317");

    Environment.SetEnvironmentVariable(
        "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL",
        "http://localhost:4318");
}

var builder = DistributedApplication.CreateBuilder(args);

var repoRoot = FindRepoRoot() ?? Directory.GetCurrentDirectory();

var loginConfigPath = Path.Combine(repoRoot, "conf", "login_athena.conf");
var charConfigPath = Path.Combine(repoRoot, "conf", "char_athena.conf");
var mapConfigPath = Path.Combine(repoRoot, "conf", "map_athena.conf");
var interConfigPath = Path.Combine(repoRoot, "conf", "inter_athena.conf");
var subnetConfigPath = Path.Combine(repoRoot, "conf", "subnet_athena.conf");
var loginMsgPath = Path.Combine(repoRoot, "conf", "msg_conf", "login_msg.conf");
var secretsPath = Path.Combine(repoRoot, "solutionfiles", "secrets", "secret.json");
var worldPartitionsPath = Path.Combine(repoRoot, "conf", "world_partitions.json");

// MapServer's configured map_cache_path is CWD-relative.
// Aspire does not guarantee that the MapServer child process starts
// with the repository root as its working directory, so pass the
// already discovered absolute path explicitly.
var mapCachePath = Path.Combine(
    repoRoot,
    "legacy",
    "rathena",
    "db",
    "map_cache.dat");

var sqlPassword = builder.AddParameter(
    "sql-server-password",
    secret: true);

var sql = builder
    .AddSqlServer(
        "sql",
        sqlPassword)
    .WithImage("mssql/server")
    .WithImageTag("2025-CU8-ubuntu-24.04")
    .WithDataVolume("athena-sql-server-2025")
    .WithEnvironment("ACCEPT_EULA", "Y")
    .WithEndpoint("tcp", endpoint =>
    {
        endpoint.Port = 58043;
        endpoint.TargetPort = 1433;
    });

var loginDb = sql.AddDatabase("LoginDb");
var charDb = sql.AddDatabase("CharDb");

//
// Orleans development cluster
//
// For the current single-silo development setup we deliberately
// use predictable direct Orleans ports.
//
// Aspire.Hosting.Orleans models its Orleans endpoints as proxied
// endpoints internally as a DCP workaround, but Orleans traffic
// itself must not use that proxy.
//
// Therefore:
// - the Aspire proxy may still get its own dynamic Port;
// - Athena.World listens directly on TargetPort 11111/30000;
// - MapServer connects directly to 127.0.0.1:30000.
//
const int worldSiloPort = 11111;
const int worldGatewayPort = 30000;
var worldGatewayAddress = $"127.0.0.1:{worldGatewayPort}";

var worldCluster = builder
    .AddOrleans("athena-world-cluster")
    .WithDevelopmentClustering();

var world = builder
    .AddProject<Projects.Athena_World>("athena-world")

    // Adds the Orleans silo configuration and creates the
    // "orleans-silo" and "orleans-gateway" endpoints.
    .WithReference(worldCluster)

    // Update the already-created Orleans silo endpoint.
    // Keep Aspire's proxy model intact, but make the actual
    // Orleans listener port deterministic.
    .WithEndpoint(
        "orleans-silo",
        endpoint =>
        {
            endpoint.TargetPort = worldSiloPort;
        })

    // Same for the client-facing Orleans gateway.
    //
    // IMPORTANT:
    // TargetPort = actual Athena.World Orleans listener.
    // Port       = Aspire/DCP proxy port, which we intentionally ignore.
    .WithEndpoint(
        "orleans-gateway",
        endpoint =>
        {
            endpoint.TargetPort = worldGatewayPort;
        })

    // Development cluster runs on this host, so advertise loopback.
    .WithEnvironment(
        "Orleans__Endpoints__AdvertisedIPAddress",
        "127.0.0.1")

    .WithEnvironment(
        "DOTNET_ENVIRONMENT",
        "Development")

    .WithEnvironment(
        "ATHENA_WORLD_PARTITIONS_PATH",
        worldPartitionsPath)

    .WithEnvironment(
        "ATHENA_WORLD_MAP_CACHE_PATH",
        mapCachePath)

    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "http://localhost:4317")

    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "grpc")

    .WithEnvironment(
        "OTEL_SERVICE_NAME",
        "athena-world");

builder
    .AddProject<Projects.LoginServer>("login-server")
    .WithEndpoint("tcp", endpoint =>
    {
        endpoint.Port = 6900;
        endpoint.IsProxied = false;
    })
    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "http://localhost:4317")
    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "grpc")
    .WithEnvironment(
        "OTEL_SERVICE_NAME",
        "login-server")
    .WithEnvironment(
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL",
        "http://localhost:4317")
    .WithEnvironment(
        "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL",
        "http://localhost:4318")
    .WithEnvironment(
        "OTEL_LOGS_EXPORTER",
        "otlp")
    .WithEnvironment(
        "OTEL_METRICS_EXPORTER",
        "otlp")
    .WithEnvironment(
        "OTEL_TRACES_EXPORTER",
        "otlp")
    .WithReference(loginDb)
    .WithEnvironment(
        "ATHENA_NET_LOGIN_DB_PROVIDER",
        "sqlserver")
    .WithEnvironment(
        "ATHENA_NET_LOGIN_DB_AUTOMIGRATE",
        "true")
    .WithArgs(
        "--login-config", loginConfigPath,
        "--inter-config", interConfigPath,
        "--subnet-config", subnetConfigPath,
        "--login-msg-config", loginMsgPath,
        "--secrets", secretsPath,
        "--auto-migrate");

builder
    .AddProject<Projects.CharServer>("char-server")
    .WithEndpoint("tcp", endpoint =>
    {
        endpoint.Port = 6121;
        endpoint.IsProxied = false;
    })
    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "http://localhost:4317")
    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "grpc")
    .WithEnvironment(
        "OTEL_SERVICE_NAME",
        "char-server")
    .WithEnvironment(
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL",
        "http://localhost:4317")
    .WithEnvironment(
        "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL",
        "http://localhost:4318")
    .WithEnvironment(
        "OTEL_LOGS_EXPORTER",
        "otlp")
    .WithEnvironment(
        "OTEL_METRICS_EXPORTER",
        "otlp")
    .WithEnvironment(
        "OTEL_TRACES_EXPORTER",
        "otlp")
    .WithReference(charDb)
    .WithEnvironment(
        "ATHENA_NET_CHAR_DB_PROVIDER",
        "sqlserver")
    .WithEnvironment(
        "ATHENA_NET_CHAR_DB_AUTOMIGRATE",
        "true")
    .WithArgs(
        "--char-config", charConfigPath,
        "--inter-config", interConfigPath,
        "--secrets", secretsPath,
        "--auto-migrate");

builder
    .AddProject<Projects.MapServer>("map-server")
    .WithEndpoint("tcp", endpoint =>
    {
        endpoint.Port = 5121;
        endpoint.IsProxied = false;
    })
    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "http://localhost:4317")
    .WithEnvironment(
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "grpc")
    .WithEnvironment(
        "OTEL_SERVICE_NAME",
        "map-server")
    .WithEnvironment(
        "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL",
        "http://localhost:4317")
    .WithEnvironment(
        "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL",
        "http://localhost:4318")
    .WithEnvironment(
        "OTEL_LOGS_EXPORTER",
        "otlp")
    .WithEnvironment(
        "OTEL_METRICS_EXPORTER",
        "otlp")
    .WithEnvironment(
        "OTEL_TRACES_EXPORTER",
        "otlp")
    .WithEnvironment(
        "ATHENA_WORLD_PARTITIONS_PATH", 
        worldPartitionsPath)

    // Gives MapServer the same ClusterId, ServiceId and
    // development clustering configuration as Athena.World.
    .WithReference(worldCluster.AsClient())

    // Development clustering uses StaticGatewayListProvider.
    //
    // Use a plain value here rather than a ReferenceExpression.
    // This deliberately bypasses Aspire's DCP proxy and avoids
    // cross-resource TargetPort substitution.
    .WithEnvironment(
        "Orleans__Clustering__Gateways__0",
        worldGatewayAddress)

    .WaitFor(world)

    .WithArgs(
        "--map-config", mapConfigPath,
        "--map-cache-path", mapCachePath,
        "--secrets", secretsPath);

builder.Build().Run();

static void EnsureSqlServerPassword()
{
    if (!string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(
                "Parameters__sql-server-password")))
    {
        return;
    }

    // Temporary compatibility alias for existing local developer environments.
    // New configuration should use the engine-neutral parameter name above.
    var legacySqlEdgePassword =
        Environment.GetEnvironmentVariable(
            "Parameters__sql-edge-password");

    if (!string.IsNullOrWhiteSpace(legacySqlEdgePassword))
    {
        Environment.SetEnvironmentVariable(
            "Parameters__sql-server-password",
            legacySqlEdgePassword);

        return;
    }

    var repoRoot =
        FindRepoRoot() ??
        Directory.GetCurrentDirectory();

    if (TryReadSqlPasswordFromSecrets(
            repoRoot,
            out var secretsPassword))
    {
        Environment.SetEnvironmentVariable(
            "Parameters__sql-server-password",
            secretsPassword);

        return;
    }

    throw new InvalidOperationException(
        "Missing SQL Edge SA password. " +
        "Set Parameters__sql-server-password or " +
        "SqlServer.SaPassword in solutionfiles/secrets/secret.json.");
}

static bool TryReadSqlPasswordFromSecrets(
    string repoRoot,
    out string password)
{
    password = string.Empty;

    var secretsPath = Path.Combine(
        repoRoot,
        "solutionfiles",
        "secrets",
        "secret.json");

    if (!File.Exists(secretsPath))
    {
        return false;
    }

    using var stream = File.OpenRead(secretsPath);
    using var document = JsonDocument.Parse(stream);

    if (!document.RootElement.TryGetProperty(
            "SqlServer",
            out var sqlServer))
    {
        return false;
    }

    if (!sqlServer.TryGetProperty(
            "SaPassword",
            out var passwordElement))
    {
        return false;
    }

    password =
        passwordElement.GetString() ??
        string.Empty;

    return !string.IsNullOrWhiteSpace(password);
}

static string? FindRepoRoot()
{
    var roots = new[]
    {
        Environment.CurrentDirectory,
        AppContext.BaseDirectory
    };

    foreach (var root in roots)
    {
        var dir = new DirectoryInfo(root);

        while (dir != null)
        {
            var secretsPath = Path.Combine(
                dir.FullName,
                "solutionfiles",
                "secrets",
                "secret.json");

            if (File.Exists(secretsPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }
    }

    return null;
}
