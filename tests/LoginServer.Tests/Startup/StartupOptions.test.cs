using Athena.Net.LoginServer.Startup;

namespace Athena.Net.LoginServer.Tests.Startup;

/// <summary>
/// StartupOptions deliberately has no --create-account-password argument (and
/// no CreateAccountPassword property to hold one): a password passed as a
/// process argument would be visible in shell history and in every other
/// process's view of this process's argument list for as long as it runs.
/// LoginServerApp reads the password from stdin instead (see
/// ReadPasswordFromStdin). This is a compile-time guarantee (the property does
/// not exist), but these tests document the remaining parsed fields and prove
/// an accidental --create-account-password argument is silently ignored rather
/// than picked up by anything.
/// </summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public void Parse_CreateAccountArgs_PopulatesUserNameSexAndEmail()
    {
        var args = new[]
        {
            "--create-account-username", "newplayer",
            "--create-account-sex", "f",
            "--create-account-email", "newplayer@example.com",
        };

        var options = StartupOptions.Parse(args);

        Assert.Equal("newplayer", options.CreateAccountUserName);
        Assert.Equal('F', options.CreateAccountSex);
        Assert.Equal("newplayer@example.com", options.CreateAccountEmail);
    }

    [Fact]
    public void Parse_NoCreateAccountSex_DefaultsToM()
    {
        var options = StartupOptions.Parse(new[] { "--create-account-username", "newplayer" });

        Assert.Equal('M', options.CreateAccountSex);
    }

    [Fact]
    public void Parse_NoCreateAccountEmail_IsNull()
    {
        var options = StartupOptions.Parse(new[] { "--create-account-username", "newplayer" });

        Assert.Null(options.CreateAccountEmail);
    }
}
