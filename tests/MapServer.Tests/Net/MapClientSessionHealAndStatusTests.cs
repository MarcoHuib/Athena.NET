using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionHealAndStatusTests
{
    [Fact]
    public async Task ScriptContextHealPersistsBeforeSendingHpParameterPacket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 20, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).HealAsync(9999, 0, default);

        Assert.Equal(1, persistence.Updates);
        Assert.Equal(40U, session.GameplayState!.State.CurrentHp);
        var packet = await ReadExact(client.GetStream(), 8);
        Assert.Equal(PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(40U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
    }

    [Fact]
    public async Task ScriptContextHealSendsSpPacketWhenOnlySpChanges()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).HealAsync(0, 6, default);

        var packet = await ReadExact(client.GetStream(), 8);
        Assert.Equal(PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(11U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
    }

    [Fact]
    public async Task ScriptContextHealSendsBothPacketsWhenHpAndSpChange()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 20, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).HealAsync(9999, 9999, default);

        var hpPacket = await ReadExact(client.GetStream(), 8);
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(hpPacket.AsSpan(2)));
        var spPacket = await ReadExact(client.GetStream(), 8);
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(spPacket.AsSpan(2)));
    }

    [Fact]
    public async Task ScriptContextHealSendsNoPacketWhenAlreadyFull()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).HealAsync(9999, 9999, default);

        Assert.Equal(0, persistence.Updates);
        Assert.Equal(0, client.Client.Available);
    }

    [Fact]
    public async Task ScriptContextHealDoesNotAdvanceLocalStateWhenPersistenceFails()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 20, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1)) { FailUpdates = true };
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ScriptContext(session, "npc:test", 1, "Test", null).HealAsync(10, 0, default));

        Assert.Equal(20U, session.GameplayState!.State.CurrentHp);
    }

    [Fact]
    public async Task ScriptContextStartStatusAppliesToSessionStatusEffectState()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        var context = new ScriptContext(session, "npc:test", 1, "Test", null);
        await context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10, default);
        await context.StartStatusAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, 240000, 10, default);

        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var blessing));
        Assert.Equal(10, blessing.Val1);
        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out var increaseAgi));
        Assert.Equal(10, increaseAgi.Val1);
        var effective = session.StatusEffects.Recalculate(session.GameplayState!.State);
        Assert.Equal((ushort)11, effective.Strength);
        Assert.Equal(25, effective.MoveSpeedHaste);
    }

    [Fact]
    public async Task ScriptContextSpecialEffectSkillEffectAndSpecialEffectSendNoClientPacket()
    {
        // The npc-interaction-heal-action capture proves Captain Carocc's own dialogue
        // turn produced zero client-visible bytes for specialeffect2/skilleffect/sc_start
        // (see ai/iro-2026-wire.md). Pending independent wire proof of their packet
        // layout, these commands apply only their required server-side semantics.
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        var context = new ScriptContext(session, "npc:test", 1, "Test", null);
        await context.SpecialEffectAsync(RathenaConstants.EF_HEAL2, default);
        await context.SkillEffectAsync(34, 0, default);
        await context.StartStatusAsync(RathenaConstants.SC_BLESSING, 240000, 10, default);

        // Only StartStatusAsync mutates observable state; the others are no-ops.
        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.Equal(0, client.Client.Available);
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length) { var data = new byte[length]; await stream.ReadExactlyAsync(data); return data; }

    private sealed class RecordingPersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state = state;
        public bool FailUpdates { get; init; }
        public int Updates { get; private set; }
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(_state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            Updates++;
            if (FailUpdates || expected.Version != _state.Version) return Task.FromResult<CharacterGameplayState?>(null);
            _state = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(_state);
        }
    }
}
