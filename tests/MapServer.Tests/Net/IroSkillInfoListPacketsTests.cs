using System.Buffers.Binary;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// 0x0B32 (ZC_SKILLINFO_LIST3) byte-level tests, anchored to the official stock-iRO map-entry
// capture documented in ai/map-server.md: a 4-byte header plus one 15-byte NV_BASIC entry at
// learned level 0 (SkillId=1 flags=0 currentLevel=0 spCost=0 range=1 upgradable=1 secondaryLevel=0).
public sealed class IroSkillInfoListPacketsTests
{
    [Fact]
    public void Build_SingleCapturedNvBasicEntry_MatchesOfficialCaptureBytesExactly()
    {
        var entry = new IroSkillInfoEntry(SkillId: 1, Flags: 0, CurrentLevel: 0, SpCost: 0, Range: 1, Upgradable: true, SecondaryLevel: 0);
        var packet = IroSkillInfoListPackets.Build([entry]);

        Assert.Equal(19, packet.Length); // 4-byte header + one 15-byte entry, matching the capture exactly.
        Assert.Equal(PacketConstants.ZcSkillInfoList, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)19, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));

        var body = packet.AsSpan(IroSkillInfoListPackets.HeaderLength, IroSkillInfoListPackets.EntryLength);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(body));         // SkillId, offset 0
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body[2..]));             // flags, offset 2
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(body[6..]));    // currentLevel, offset 6
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(body[8..]));    // spCost, offset 8
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(body[10..]));   // range, offset 10
        Assert.Equal((byte)1, body[12]);                                                // upgradable, offset 12
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(body[13..]));   // secondaryLevel, offset 13
    }

    [Fact]
    public void Build_EmptyList_EmitsHeaderOnly()
    {
        var packet = IroSkillInfoListPackets.Build([]);
        Assert.Equal(4, packet.Length);
        Assert.Equal(PacketConstants.ZcSkillInfoList, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
    }

    // Proves genericity: multiple entries with distinct fields serialize independently and in
    // order, not merely the single captured NV_BASIC case.
    [Fact]
    public void Build_MultipleEntries_SerializesEachIndependentlyInOrder()
    {
        var first = new IroSkillInfoEntry(SkillId: 1, Flags: 0, CurrentLevel: 0, SpCost: 0, Range: 1, Upgradable: true, SecondaryLevel: 0);
        var second = new IroSkillInfoEntry(SkillId: 5, Flags: 0, CurrentLevel: 3, SpCost: 8, Range: -1, Upgradable: true, SecondaryLevel: 0);
        var packet = IroSkillInfoListPackets.Build([first, second]);

        Assert.Equal(4 + 15 * 2, packet.Length);

        var firstBody = packet.AsSpan(4, 15);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstBody));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(firstBody[6..]));

        var secondBody = packet.AsSpan(19, 15);
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(secondBody));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(secondBody[6..]));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(secondBody[8..]));
        // Range=-1 is a real pinned source value (e.g. SM_BASH) - the wire field is the raw 16-bit
        // reinterpretation, matching what a packed C int16/uint16 struct field would hold.
        Assert.Equal(unchecked((ushort)-1), BinaryPrimitives.ReadUInt16LittleEndian(secondBody[10..]));
        Assert.Equal((byte)1, secondBody[12]);
    }

    [Fact]
    public void From_ResolvesSpCostAndRangeFromCurrentLevel_NotMaxOrNextLevel()
    {
        // SM_BASH (id 5): SpCostByLevel = [8,8,8,8,8,15,15,15,15,15], Range = -1 (real generated data).
        var canonical = Athena.Net.MapServer.Generated.Skills.GeneratedSkillRegistry.GetById(5);
        var stateAtLevel3 = new CharacterSkillState(SkillId: 5, CurrentLevel: 3, MaxLevel: 10, EffectiveTreeMembership: true, NormallyLearnable: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        var entry = IroSkillInfoEntry.From(stateAtLevel3, canonical);
        Assert.Equal((ushort)8, entry.SpCost); // level-3 cost, not level-1 or MaxLevel(10)'s cost of 15.
        Assert.Equal((short)-1, entry.Range);

        var stateAtLevel6 = stateAtLevel3 with { CurrentLevel = 6 };
        var entryAtLevel6 = IroSkillInfoEntry.From(stateAtLevel6, canonical);
        Assert.Equal((ushort)15, entryAtLevel6.SpCost);
    }

    [Fact]
    public void From_UnlearnedSkill_ReportsZeroSpCost_MatchingCapturedNvBasicSemantics()
    {
        var canonical = Athena.Net.MapServer.Generated.Skills.GeneratedSkillRegistry.GetById(1); // NV_BASIC, no Requires block at all.
        var state = new CharacterSkillState(SkillId: 1, CurrentLevel: 0, MaxLevel: 9, EffectiveTreeMembership: true, NormallyLearnable: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        var entry = IroSkillInfoEntry.From(state, canonical);
        Assert.Equal((ushort)0, entry.SpCost);
    }
}
