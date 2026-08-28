using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class GeneratedCaptainCaroccIntegrationTests
{
    private const string EntityId = "npc:int_land03:captain carocc#intro_npc03_03";

    [Fact]
    public async Task VisibleRealNpc_ClicksGeneratedOnClickCompletesQuest21001HealsAndAppliesStatuses()
    {
        var entity = Assert.Single(GeneratedScriptRegistry.Entities, item => item.Id == EntityId);
        Assert.Equal(new WorldActorComponent("Captain Carocc#intro_npc03_03", "int_land03", 78, 103, 5, 873, 0), entity.Actor);
        var registry = new WorldMapRegistry([], [entity]);
        var actor = Assert.Single(registry.GetVisibleWarpActors("int_land03", 78, 103));
        Assert.True(registry.TryGetInteraction(actor.ActorId, "int_land03", out var bound, out _));
        Assert.Same(entity, bound);

        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var serverClient = await listener.AcceptTcpClientAsync(); await connect;
        await using var stream = client.GetStream();
        var questPersistence = new RecordingQuestPersistence(21001, CharacterQuestStatus.Active); // quest 21001 already active, matching the capture's own quest state; 21008 defaults to Absent (case 0).
        var gameplayPersistence = new RecordingGameplayStatePersistence(new(9, 0, 0, 1, 1, 0, 0, 20, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1));
        await using var session = new MapClientSession(1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true, "int_land03", 78, 103, registry,
            questPersistence: questPersistence, gameplayStatePersistence: gameplayPersistence, accountId: 7, charId: 9);
        var run = session.RunAsync(CancellationToken.None);
        // Unlike the gameplay-state-free Wounded Swordsman fixture, Captain's script needs
        // CharacterGameplayState loaded (heal/getexp), which only CompleteIroAuthenticationAsync does.
        await session.CompleteIroAuthenticationAsync(new(7, 9, 1, 2, 0, 0, false, "int_land03", 78, 103, 0, 0, 0));
        var bootstrap = new byte[29]; await stream.ReadExactlyAsync(bootstrap);

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        var selfWeaponLook = await ReadExact(stream, 15); // 0x01D7, sent before the spawn broadcast (clif_parse_LoadEndAck ordering)
        Assert.Equal((short)0x01d7, BinaryPrimitives.ReadInt16LittleEndian(selfWeaponLook));
        await ReadExact(stream, 6); // 0x0B08 inventoryStart (empty test-default inventory, no 0x0B09/0x0B39 items)
        await ReadExact(stream, 4); // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        Assert.Equal(actor.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5)));
        Assert.Equal((ushort)873, BinaryPrimitives.ReadUInt16LittleEndian(spawn.AsSpan(23)));

        await stream.WriteAsync(ActorPacket(0x0090, actor.ActorId, 8));
        Assert.Equal("[Captain Carocc]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("There are still people in the cabins?!\0", Message(await ReadDynamic(stream)));
        Assert.Equal("At least you are safe.\0", Message(await ReadDynamic(stream)));
        Assert.Equal("Are you alright?\0", Message(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));

        await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
        var menu = await ReadDynamic(stream);
        Assert.Equal((short)0x00b7, BinaryPrimitives.ReadInt16LittleEndian(menu));
        Assert.Contains("I'm alright, but others need help.:I think I am the last?:\0", Message(menu));

        await stream.WriteAsync(SelectionPacket(actor.ActorId, 1));
        Assert.Equal("[Captain Carocc]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("There are more people left?\0", Message(await ReadDynamic(stream)));
        Assert.Equal("I will send a rescue team to them.\0", Message(await ReadDynamic(stream)));
        Assert.Equal("Thank you for your report.\0", Message(await ReadDynamic(stream)));
        Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));

        // Walk through the remaining pinned "next" boundaries up to the heal/status/quest
        // burst (academy.txt:39-53): 2, 3, 2, 2 messages per boundary, then the final
        // boundary's 2 messages ("[Captain Carocc]" / "It is a hard task...") precede the burst.
        foreach (var messageCount in new[] { 2, 3, 2, 2 })
        {
            await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
            for (var i = 0; i < messageCount; i++) await ReadDynamic(stream);
            Assert.Equal((short)0x00b5, BinaryPrimitives.ReadInt16LittleEndian(await ReadExact(stream, 6)));
        }

        await stream.WriteAsync(ActorPacket(0x00b9, actor.ActorId, 7));
        Assert.Equal("[Captain Carocc]\0", Message(await ReadDynamic(stream)));
        Assert.Equal("It is a hard task, but you look tough enough.\0", Message(await ReadDynamic(stream)));

        // Frame-3496 proven burst, in the generated script's exact call order (academy.txt:165-172):
        // specialeffect2(no-op) -> heal(9999,0) -> skilleffect(34,0) -> sc_start(BLESSING) ->
        // skilleffect(29,0) -> sc_start(INCREASEAGI). See ai/iro-2026-wire.md for the full
        // byte segmentation this was derived from.

        // heal(9999,0): HP 20 -> 40 (clamped to MaxHp), sent via the generic 0x00B0 parameter path.
        // The packet is only sent after HealAsync's mutation is persisted (see
        // CharacterHealService/CharacterGameplayStateSession.MutateAsync), so observing it here is
        // sufficient proof; session.GameplayState.State itself is not re-checked at this point
        // because the generated script task keeps running concurrently with this read (getexp,
        // right after, can itself recalculate CurrentHp again on a level-up) - asserting live
        // session state here would race against that continuation instead of proving anything.
        var healPacket = await ReadExact(stream, 8);
        Assert.Equal(PacketConstants.ZcParameterChange, BinaryPrimitives.ReadInt16LittleEndian(healPacket));
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(healPacket.AsSpan(2)));
        Assert.Equal(40U, BinaryPrimitives.ReadUInt32LittleEndian(healPacket.AsSpan(4)));

        // heal(9999,0) also sends the capture-proven 0x09CB AL_HEAL visual (target=player, src=Captain).
        var healVisual = await ReadExact(stream, 17);
        Assert.Equal(PacketConstants.ZcUseSkill, BinaryPrimitives.ReadInt16LittleEndian(healVisual));
        Assert.Equal(IroStatusEffectPackets.AlHeal, BinaryPrimitives.ReadUInt16LittleEndian(healVisual.AsSpan(2)));
        Assert.Equal(9999, BinaryPrimitives.ReadInt32LittleEndian(healVisual.AsSpan(4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(healVisual.AsSpan(8)));
        Assert.Equal(actor.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(healVisual.AsSpan(12)));

        // skilleffect(34,0) sends the AL_BLESSING 0x09CB cast visual (level=0, the script's own arg).
        var blessingVisual = await ReadExact(stream, 17);
        Assert.Equal(PacketConstants.ZcUseSkill, BinaryPrimitives.ReadInt16LittleEndian(blessingVisual));
        Assert.Equal((ushort)34, BinaryPrimitives.ReadUInt16LittleEndian(blessingVisual.AsSpan(2)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(blessingVisual.AsSpan(4)));

        // sc_start(SC_BLESSING,240000,10) sends 0x0983 activation (EFST_BLESSING=10, val1=10)
        // then 0x0141 STR/INT/DEX (base=1, plus=val1=10).
        var blessingActivation = await ReadExact(stream, 29);
        Assert.Equal(PacketConstants.ZcMsgStateChange3, BinaryPrimitives.ReadInt16LittleEndian(blessingActivation));
        Assert.Equal(IroStatusEffectPackets.EfstBlessing, BinaryPrimitives.ReadUInt16LittleEndian(blessingActivation.AsSpan(2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(blessingActivation.AsSpan(17)));
        foreach (var expectedType in new ushort[] { IroStatusEffectPackets.SpStr, IroStatusEffectPackets.SpInt, IroStatusEffectPackets.SpDex })
        {
            var stat = await ReadExact(stream, 14);
            Assert.Equal(PacketConstants.ZcCoupleStatus, BinaryPrimitives.ReadInt16LittleEndian(stat));
            Assert.Equal(expectedType, BinaryPrimitives.ReadUInt32LittleEndian(stat.AsSpan(2)));
            Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(stat.AsSpan(10)));
        }

        // skilleffect(29,0) sends the AL_INCAGI 0x09CB cast visual.
        var incAgiVisual = await ReadExact(stream, 17);
        Assert.Equal(PacketConstants.ZcUseSkill, BinaryPrimitives.ReadInt16LittleEndian(incAgiVisual));
        Assert.Equal((ushort)29, BinaryPrimitives.ReadUInt16LittleEndian(incAgiVisual.AsSpan(2)));

        // sc_start(SC_INCREASEAGI,240000,10) sends 0x0983 activation (EFST_INC_AGI=12, val1=10)
        // then 0x0141 AGI (base=1, plus=2+val1=12).
        var incAgiActivation = await ReadExact(stream, 29);
        Assert.Equal(PacketConstants.ZcMsgStateChange3, BinaryPrimitives.ReadInt16LittleEndian(incAgiActivation));
        Assert.Equal(IroStatusEffectPackets.EfstIncAgi, BinaryPrimitives.ReadUInt16LittleEndian(incAgiActivation.AsSpan(2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(incAgiActivation.AsSpan(17)));
        var agiStat = await ReadExact(stream, 14);
        Assert.Equal(PacketConstants.ZcCoupleStatus, BinaryPrimitives.ReadInt16LittleEndian(agiStat));
        Assert.Equal(IroStatusEffectPackets.SpAgi, BinaryPrimitives.ReadUInt32LittleEndian(agiStat.AsSpan(2)));
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(agiStat.AsSpan(10)));

        // completequest 21001 sends 0x02B4 (quest removed from client log) before getexp's
        // progression packets - matching ai/iro-2026-wire.md's documented completequest
        // wire behavior (removal from the client log, not deletion of server-side state).
        var removeQuest = await ReadExact(stream, 6);
        Assert.Equal((short)0x02b4, BinaryPrimitives.ReadInt16LittleEndian(removeQuest));
        Assert.Equal(21001u, BinaryPrimitives.ReadUInt32LittleEndian(removeQuest.AsSpan(2)));
        Assert.Equal(CharacterQuestStatus.Completed, questPersistence.State);

        // getexp 600,600 through the existing, separately-tested CharacterProgressionService/
        // IroCharacterProgressionPackets path (exact packet sequencing for a given award is
        // covered by CharacterProgressionServiceTests). 600/600 crosses a level threshold from
        // the level-1 fixture, so several parameter packets precede setquest 21008's 0x0B0C -
        // drain until that expected packet ID, requiring at least one progression packet.
        var progressionPacketCount = 0;
        var progressionPacketIds = new List<short>();
        byte[] addQuest;
        while (true)
        {
            var header = await ReadExact(stream, 2);
            var packetId = BinaryPrimitives.ReadInt16LittleEndian(header);
            if (packetId == 0x0b0c) { addQuest = [.. header, .. await ReadExact(stream, IroQuestPackets.AddQuestLength - 2)]; break; }
            var length = packetId switch
            {
                PacketConstants.ZcLongLongParameterChange => 12,
                PacketConstants.ZcNotifyExperience => PacketConstants.ZcNotifyExperienceLength,
                PacketConstants.ZcNotifyEffect => PacketConstants.ZcNotifyEffectLength,
                _ => 8,
            };
            await ReadExact(stream, length - 2);
            progressionPacketCount++;
            progressionPacketIds.Add(packetId);
        }
        Assert.True(progressionPacketCount > 0);
        Assert.Contains(PacketConstants.ZcNotifyExperience, progressionPacketIds);
        Assert.Contains(PacketConstants.ZcNotifyEffect, progressionPacketIds);
        Assert.True(gameplayPersistence.Updates >= 2); // heal, then the one atomic getexp mutation
        Assert.Equal(21008u, BinaryPrimitives.ReadUInt32LittleEndian(addQuest.AsSpan(2)));

        // sc_start SC_BLESSING/SC_INCREASEAGI additionally applied to session-local status state
        // (server-side authority; already proven client-synced above).
        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out var blessing));
        Assert.Equal(10, blessing.Val1);
        Assert.True(session.StatusEffects.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out var increaseAgi));
        Assert.Equal(10, increaseAgi.Val1);

        client.Close(); await run.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
    }

    private static byte[] ActorPacket(short type, uint id, int length) { var packet = new byte[length]; BinaryPrimitives.WriteInt16LittleEndian(packet, type); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), id); packet[^1] = 0xaa; return packet; }
    private static byte[] SelectionPacket(uint actorId, byte wireIndex) { var packet = new byte[8]; BinaryPrimitives.WriteInt16LittleEndian(packet, 0x00b8); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId); packet[6] = wireIndex; packet[7] = 0xaa; return packet; }
    private static string Message(byte[] packet) => System.Text.Encoding.ASCII.GetString(packet.AsSpan(8));
    private static async Task<byte[]> ReadDynamic(Stream stream) { var header = await ReadExact(stream, 4); var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)); return [.. header, .. await ReadExact(stream, length - 4)]; }
    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var data = new byte[length];
        await stream.ReadExactlyAsync(data).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return data;
    }

    // Captain Carocc's script checks two independent quests (21008 for its own switch, 21001
    // for its conditional completequest/getexp), so quest state must be tracked per quest ID.
    private sealed class RecordingQuestPersistence(uint questId, CharacterQuestStatus initialState) : ICharacterQuestPersistence
    {
        private readonly Dictionary<uint, CharacterQuestStatus> _states = new() { [questId] = initialState };
        public CharacterQuestStatus State => _states[questId];
        public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterQuestStatus?>(_states.GetValueOrDefault(requestedQuestId, CharacterQuestStatus.Absent));
        public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CharacterQuestStatus state, CancellationToken cancellationToken)
        {
            _states[requestedQuestId] = state;
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state = state;
        public int Updates { get; private set; }
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(_state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            Updates++;
            if (expected.Version != _state.Version) return Task.FromResult<CharacterGameplayState?>(null);
            _state = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(_state);
        }
    }
}
