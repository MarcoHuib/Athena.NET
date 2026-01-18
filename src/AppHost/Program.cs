using System.Text.Json;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:18888");
}

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
}

EnsureSqlEdgePassword();

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT")))
{
    Environment.SetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");
}

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_ENDPOINT_URL")) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL")))
{
    Environment.SetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_ENDPOINT_URL", "http://localhost:4317");
    Environment.SetEnvironmentVariable("DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", "http://localhost:4318");
}

var builder = DistributedApplication.CreateBuilder(args);

var sqlPassword = builder.AddParameter("sql-edge-password", secret: true);
var sql = builder.AddSqlServer("sql", sqlPassword)
    .WithImage("azure-sql-edge")
    .WithImageTag("latest")
    .WithEnvironment("ACCEPT_EULA", "Y")
    .WithEndpoint("tcp", endpoint =>
    {
        endpoint.Port = 58043;
        endpoint.TargetPort = 1433;
    });

var loginDb = sql.AddDatabase("LoginDb");

builder.AddProject("login-server", "../LoginServer/LoginServer.csproj")
    .WithReference(loginDb)
    .WithEnvironment("ATHENA_NET_LOGIN_DB_PROVIDER", "sqlserver")
    .WithEnvironment("ATHENA_NET_LOGIN_DB_AUTOMIGRATE", "true");

builder.Build().Run();

static void EnsureSqlEdgePassword()
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Parameters__sql-edge-password")))
    {
        return;
    }

    var repoRoot = FindRepoRoot() ?? Directory.GetCurrentDirectory();
    if (TryReadSqlPasswordFromSecrets(repoRoot, out var secretsPassword))
    {
        Environment.SetEnvironmentVariable("Parameters__sql-edge-password", secretsPassword);
        return;
    }
    throw new InvalidOperationException(
        "Missing SQL Edge SA password. Set Parameters__sql-edge-password or SqlServer.SaPassword in solutionfiles/secrets/secret.json.");
}

static bool TryReadSqlPasswordFromSecrets(string repoRoot, out string password)
{
    password = string.Empty;
    var secretsPath = Path.Combine(repoRoot, "solutionfiles", "secrets", "secret.json");
    if (!File.Exists(secretsPath))
    {
        return false;
    }

    using var stream = File.OpenRead(secretsPath);
    using var document = JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty("SqlServer", out var sqlServer))
    {
        return false;
    }

    if (!sqlServer.TryGetProperty("SaPassword", out var passwordElement))
    {
        return false;
    }

    password = passwordElement.GetString() ?? string.Empty;
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
            var secretsPath = Path.Combine(dir.FullName, "solutionfiles", "secrets", "secret.json");
            if (File.Exists(secretsPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }
    }

    return null;
}
