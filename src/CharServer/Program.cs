namespace Athena.Net.CharServer;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await Startup.CharServerApp.RunAsync(args);
    }
}
