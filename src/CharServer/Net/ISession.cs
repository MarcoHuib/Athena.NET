namespace Athena.Net.CharServer.Net;

public interface ISession : IDisposable
{
    Task RunAsync(CancellationToken cancellationToken);
}
