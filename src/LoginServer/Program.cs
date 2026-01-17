namespace Athena.Net.LoginServer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await Startup.LoginServerApp.RunAsync(args);
    }
}
