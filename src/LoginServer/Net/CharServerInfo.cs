using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Athena.Net.LoginServer.Net;

public sealed class CharServerInfo
{
    public string Name { get; set; } = string.Empty;
    public IPAddress Ip { get; set; } = IPAddress.Loopback;
    public ushort Port { get; set; }
    public ushort Users { get; set; }
    public ushort Type { get; set; }
    public ushort IsNew { get; set; }
    public CharServerConnection? Connection { get; set; }
}

public sealed class CharServerConnection
{
    public CharServerConnection(NetworkStream stream)
    {
        Stream = stream;
    }

    public NetworkStream Stream { get; }
    public SemaphoreSlim WriteLock { get; } = new(1, 1);
}
