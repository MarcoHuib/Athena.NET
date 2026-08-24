using System.Net; using System.Net.Sockets;
using Athena.Net.MapServer.Config; using Athena.Net.MapServer.Net; using Athena.Net.MapServer.World;
namespace Athena.Net.MapServer.Tests.Net;
public sealed class MapClientSessionGameplayStateTests
{
    [Fact]
    public async Task SuccessfulAuthenticationLoadsStateBeforeBootstrap()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); var endpoint=(IPEndPoint)listener.LocalEndpoint;
        using var client=new TcpClient(); var connecting=client.ConnectAsync(endpoint.Address,endpoint.Port); using var server=await listener.AcceptTcpClientAsync(); await connecting;
        var state=new CharacterGameplayState(9,3,2,4,123,456,30,8,45,11,51,3,2,3,4,5,6,7); var persistence=new StubPersistence(state);
        using var session=new MapClientSession(1,server,new CharServerConnector(new MapConfigStore(new MapConfig(),"unused")),false,gameplayStatePersistence:persistence);
        var auth=new MapAuthOkData(7,9,1,2,0,0,false,"iz_int01",18,26,0,0,0);
        await session.CompleteIroAuthenticationAsync(auth);
        Assert.Equal(state,session.GameplayState!.State); Assert.Equal((short)0x0b18,await ReadInt16Async(client.GetStream()));
    }
    private static async Task<short> ReadInt16Async(NetworkStream stream){var b=new byte[2];await stream.ReadExactlyAsync(b);return System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b);}
    private sealed class StubPersistence(CharacterGameplayState state):ICharacterGameplayStatePersistence
    { public Task<CharacterGameplayState?> GetAsync(uint a,uint c,CancellationToken t)=>Task.FromResult<CharacterGameplayState?>(a==7&&c==9?state:null); public Task<CharacterGameplayState?> UpdateAsync(uint a,CharacterGameplayState e,CharacterGameplayState u,CancellationToken t)=>Task.FromResult<CharacterGameplayState?>(null); }
}
