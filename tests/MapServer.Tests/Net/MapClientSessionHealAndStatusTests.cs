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
        // hp==0 -> no heal visual (BuildUseSkillVisual is only sent when hp > 0).
        Assert.Equal(0, client.Client.Available);
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

    // Frame 3496 of npc-interaction-heal-action.pcapng proves a positive heal amount is
    // followed by a ZC_USE_SKILL (0x09CB) visual: SKID=AL_HEAL(28), level=the heal amount
    // (not the resulting HP), target=player, src=the executing NPC's actor. See
    // ai/iro-2026-wire.md for the full byte segmentation.
    [Fact]
    public async Task ScriptContextHealSendsCaptureProvenHealVisualWhenHpChanges()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 20, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, accountId: 7, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).HealAsync(9999, 0, default);

        await ReadExact(client.GetStream(), 8); // 0x00B0 HP parameter packet
        var visual = await ReadExact(client.GetStream(), 17);
        Assert.Equal(PacketConstants.ZcUseSkill, BinaryPrimitives.ReadInt16LittleEndian(visual));
        Assert.Equal(IroStatusEffectPackets.AlHeal, BinaryPrimitives.ReadUInt16LittleEndian(visual.AsSpan(2)));
        Assert.Equal(9999, BinaryPrimitives.ReadInt32LittleEndian(visual.AsSpan(4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(8))); // target = player account id
        Assert.Equal(1, visual[16]);
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
        await DrainBlessingClientPackets(client.GetStream());
        await context.StartStatusAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, 240000, 10, default);
        await DrainIncreaseAgiClientPackets(client.GetStream());

        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var blessing));
        Assert.Equal(10, blessing.Val1);
        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out var increaseAgi));
        Assert.Equal(10, increaseAgi.Val1);
        var effective = session.StatusEffects.Recalculate(session.GameplayState!.State);
        Assert.Equal((ushort)11, effective.Strength);
        Assert.Equal((ushort)13, effective.Agility); // base 1 + (2 + val1=10) = 13, per status.cpp:10853/6844.
        Assert.Equal(25, effective.MoveSpeedHaste);
    }

    // Frame 3496 proves Blessing activation sends 0x0983 (EFST_BLESSING=10, val1=10, duration
    // total=remain=240000) followed by 0x0141 STR/INT/DEX (base=1, plus=10 each).
    [Fact]
    public async Task ScriptContextStartStatusSendsCaptureProvenBlessingActivationPackets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, accountId: 7, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10, default);

        var activation = await ReadExact(client.GetStream(), 29);
        Assert.Equal(PacketConstants.ZcMsgStateChange3, BinaryPrimitives.ReadInt16LittleEndian(activation));
        Assert.Equal(IroStatusEffectPackets.EfstBlessing, BinaryPrimitives.ReadUInt16LittleEndian(activation.AsSpan(2)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(activation.AsSpan(4)));
        Assert.Equal(1, activation[8]);
        Assert.Equal(240000, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(9)));
        Assert.Equal(240000, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(13)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(17)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(21)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(25)));

        foreach (var expectedType in new ushort[] { IroStatusEffectPackets.SpStr, IroStatusEffectPackets.SpInt, IroStatusEffectPackets.SpDex })
        {
            var stat = await ReadExact(client.GetStream(), 14);
            Assert.Equal(PacketConstants.ZcCoupleStatus, BinaryPrimitives.ReadInt16LittleEndian(stat));
            Assert.Equal(expectedType, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(2)));
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(6)));
            Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(10)));
        }
        Assert.Equal(0, client.Client.Available);
    }

    // Frame 3496 proves Increase AGI activation sends 0x0983 (EFST_INC_AGI=12, val1=10)
    // followed by 0x0141 AGI (base=1, plus=12 = 2+val1).
    [Fact]
    public async Task ScriptContextStartStatusSendsCaptureProvenIncreaseAgiActivationPackets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, accountId: 7, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).StartStatusAsync(CharacterStatusEffectState.StatusIds.IncreaseAgi, 240000, 10, default);

        var activation = await ReadExact(client.GetStream(), 29);
        Assert.Equal(PacketConstants.ZcMsgStateChange3, BinaryPrimitives.ReadInt16LittleEndian(activation));
        Assert.Equal(IroStatusEffectPackets.EfstIncAgi, BinaryPrimitives.ReadUInt16LittleEndian(activation.AsSpan(2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(17)));

        var stat = await ReadExact(client.GetStream(), 14);
        Assert.Equal(PacketConstants.ZcCoupleStatus, BinaryPrimitives.ReadInt16LittleEndian(stat));
        Assert.Equal(IroStatusEffectPackets.SpAgi, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(6)));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(10)));
        Assert.Equal(0, client.Client.Available);
    }

    // Re-applying an already-active status (pinned sc_start semantics) refreshes the client
    // the same way as first activation: another 0x0983 with the new values/duration.
    [Fact]
    public async Task ScriptContextStartStatusRefreshSendsAnotherActivationPacket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        var context = new ScriptContext(session, "npc:test", 1, "Test", null);
        await context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10, default);
        await DrainBlessingClientPackets(client.GetStream());

        await context.StartStatusAsync(CharacterStatusEffectState.StatusIds.Blessing, 100000, 3, default);

        var activation = await ReadExact(client.GetStream(), 29);
        Assert.Equal(PacketConstants.ZcMsgStateChange3, BinaryPrimitives.ReadInt16LittleEndian(activation));
        Assert.Equal(100000, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(9)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(activation.AsSpan(17)));
    }

    [Fact]
    public async Task ScriptContextSkillEffectSendsCaptureProvenUseSkillVisual()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, accountId: 7, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).SkillEffectAsync(34, 10, default);

        var visual = await ReadExact(client.GetStream(), 17);
        Assert.Equal(PacketConstants.ZcUseSkill, BinaryPrimitives.ReadInt16LittleEndian(visual));
        Assert.Equal((ushort)34, BinaryPrimitives.ReadUInt16LittleEndian(visual.AsSpan(2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(visual.AsSpan(4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(visual.AsSpan(8)));
    }

    // Pinned BUILDIN_FUNC(specialeffect2) -> clif_specialeffect -> 0x01F3 (ZC_NOTIFY_EFFECT2),
    // and frame 3496's 461-byte burst contains zero 0x01F3 bytes anywhere - this remains
    // unimplemented pending independent wire proof (ai/iro-2026-wire.md).
    [Fact]
    public async Task ScriptContextSpecialEffectSendsNoClientPacket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var persistence = new RecordingPersistence(new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false, gameplayStatePersistence: persistence);
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));
        var bootstrap = new byte[29]; await client.GetStream().ReadExactlyAsync(bootstrap);

        await new ScriptContext(session, "npc:test", 1, "Test", null).SpecialEffectAsync(RathenaConstants.EF_HEAL2, default);

        Assert.Equal(0, client.Client.Available);
    }

    // Temporary status values are never written to the CharacterGameplayState persistence
    // path - only heal/getexp go through ICharacterGameplayStatePersistence.UpdateAsync.
    [Fact]
    public async Task StartStatusDoesNotPersistTemporaryStatusValues()
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

        Assert.Equal(0, persistence.Updates);
        Assert.Equal((ushort)1, session.GameplayState!.State.Strength);
        Assert.Equal((ushort)1, session.GameplayState!.State.Agility);
    }

    private static async Task DrainBlessingClientPackets(Stream stream)
    {
        await ReadExact(stream, 29); // 0x0983
        await ReadExact(stream, 14); // STR 0x0141
        await ReadExact(stream, 14); // INT 0x0141
        await ReadExact(stream, 14); // DEX 0x0141
    }

    private static async Task DrainIncreaseAgiClientPackets(Stream stream)
    {
        await ReadExact(stream, 29); // 0x0983
        await ReadExact(stream, 14); // AGI 0x0141
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
