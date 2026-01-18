using System.Security.Cryptography;

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
    .WithEnvironment("ACCEPT_EULA", "Y");

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

    var repoRoot = Directory.GetCurrentDirectory();
    var aspireDir = Path.Combine(repoRoot, ".aspire");
    var secretsPath = Path.Combine(aspireDir, "parameters.local.json");

    if (TryReadPassword(secretsPath, out var existingPassword))
    {
        Environment.SetEnvironmentVariable("Parameters__sql-edge-password", existingPassword);
        return;
    }

    Directory.CreateDirectory(aspireDir);

    var generatedPassword = GenerateSqlPassword();
    var payload = new
    {
        Parameters = new Dictionary<string, string>
        {
            ["sql-edge-password"] = generatedPassword
        }
    };

    File.WriteAllText(secretsPath, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
    {
        WriteIndented = true
    }));

    Environment.SetEnvironmentVariable("Parameters__sql-edge-password", generatedPassword);
}

static bool TryReadPassword(string secretsPath, out string password)
{
    password = string.Empty;
    if (!File.Exists(secretsPath))
    {
        return false;
    }

    using var stream = File.OpenRead(secretsPath);
    using var document = System.Text.Json.JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty("Parameters", out var parameters))
    {
        return false;
    }

    if (!parameters.TryGetProperty("sql-edge-password", out var passwordElement))
    {
        return false;
    }

    password = passwordElement.GetString() ?? string.Empty;
    return !string.IsNullOrWhiteSpace(password);
}

static string GenerateSqlPassword()
{
    const string alphabet = "abcdefghijklmnopqrstuvwxyz";
    const string upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    const string digits = "0123456789";
    const string symbols = "!@#$%^&*_-+=?";
    const string all = alphabet + upper + digits + symbols;

    Span<char> result = stackalloc char[16];
    result[0] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    result[1] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
    result[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
    result[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];

    for (var i = 4; i < result.Length; i++)
    {
        result[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
    }

    for (var i = result.Length - 1; i > 0; i--)
    {
        var swapIndex = RandomNumberGenerator.GetInt32(i + 1);
        (result[i], result[swapIndex]) = (result[swapIndex], result[i]);
    }

    return new string(result);
}
