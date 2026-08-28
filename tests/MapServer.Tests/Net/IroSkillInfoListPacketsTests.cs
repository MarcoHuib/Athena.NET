using System.Buffers.Binary;
using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// 0x0B32 (ZC_SKILLINFO_LIST3) byte-level tests, anchored to the official stock-iRO map-entry
// capture documented in ai/map-server.md: a 4-byte header plus one 15-byte NV_BASIC entry at
// learned level 0 (SkillId=1 inf=0 currentLevel=0 spCost=0 range=1 upgradable=1 secondaryLevel=0).
//
// These are PURE SERIALIZER tests only - they manually construct IroSkillInfoEntry and prove
// IroSkillInfoListPackets.Build emits the correct bytes for it. They do NOT exercise the
// production projection path (GeneratedSkillRegistry -> CharacterSkillService ->
// IroSkillInfoEntry.From -> IroSkillInfoListPackets.Build) - see
// IroSkillInfoProductionProjectionTests for that regression, which is the one that would have
// caught the historical Range=0 production bug this manually-constructed style could not.
public sealed class IroSkillInfoListPacketsTests
{
    [Fact]
    public void Build_SingleCapturedNvBasicEntry_MatchesOfficialCaptureBytesExactly()
    {
        var entry = new IroSkillInfoEntry(SkillId: 1, Inf: 0, CurrentLevel: 0, SpCost: 0, Range: 1, Upgradable: true, SecondaryLevel: 0);
        var packet = IroSkillInfoListPackets.Build([entry]);

        Assert.Equal(19, packet.Length); // 4-byte header + one 15-byte entry, matching the capture exactly.
        Assert.Equal(PacketConstants.ZcSkillInfoList, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)19, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));

        var body = packet.AsSpan(IroSkillInfoListPackets.HeaderLength, IroSkillInfoListPackets.EntryLength);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(body));         // SkillId, offset 0
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body[2..]));             // inf, offset 2
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
        var first = new IroSkillInfoEntry(SkillId: 1, Inf: 0, CurrentLevel: 0, SpCost: 0, Range: 1, Upgradable: true, SecondaryLevel: 0);
        var second = new IroSkillInfoEntry(SkillId: 5, Inf: 1, CurrentLevel: 3, SpCost: 8, Range: 1, Upgradable: true, SecondaryLevel: 3);
        var packet = IroSkillInfoListPackets.Build([first, second]);

        Assert.Equal(4 + 15 * 2, packet.Length);

        var firstBody = packet.AsSpan(4, 15);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstBody));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(firstBody[6..]));

        var secondBody = packet.AsSpan(19, 15);
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(secondBody));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(secondBody[2..]));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(secondBody[6..]));
        Assert.Equal((ushort)8, BinaryPrimitives.ReadUInt16LittleEndian(secondBody[8..]));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(secondBody[10..]));
        Assert.Equal((byte)1, secondBody[12]);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(secondBody[13..])); // secondaryLevel mirrors currentLevel
    }

    [Fact]
    public void From_ResolvesSpCostFromCurrentLevel_NotMaxOrNextLevel()
    {
        // SM_BASH (id 5): SpCostByLevel = [8,8,8,8,8,15,15,15,15,15] (real generated data).
        var canonical = GeneratedSkillRegistry.GetById(5);
        var stateAtLevel3 = new CharacterSkillState(SkillId: 5, CurrentLevel: 3, MaxLevel: 10, EffectiveTreeMembership: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        var entry = IroSkillInfoEntry.From(stateAtLevel3, canonical);
        Assert.Equal((ushort)8, entry.SpCost); // level-3 cost, not level-1 or MaxLevel(10)'s cost of 15.

        var stateAtLevel6 = stateAtLevel3 with { CurrentLevel = 6 };
        var entryAtLevel6 = IroSkillInfoEntry.From(stateAtLevel6, canonical);
        Assert.Equal((ushort)15, entryAtLevel6.SpCost);
    }

    [Fact]
    public void From_UnlearnedSkill_ReportsZeroSpCost_MatchingCapturedNvBasicSemantics()
    {
        var canonical = GeneratedSkillRegistry.GetById(1); // NV_BASIC, no Requires block at all.
        var state = new CharacterSkillState(SkillId: 1, CurrentLevel: 0, MaxLevel: 9, EffectiveTreeMembership: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        var entry = IroSkillInfoEntry.From(state, canonical);
        Assert.Equal((ushort)0, entry.SpCost);
    }

    [Fact]
    public void From_NegativeGeneratedRange_ResolvesToAbsoluteValue()
    {
        // SM_BASH's real generated Range is -1; pinned skill_get_range2's default (no
        // skillrange_from_weapon config) is an absolute-value fallback, not a raw pass-through.
        var canonical = GeneratedSkillRegistry.GetById(5);
        Assert.Equal((short)-1, canonical.Range); // confirms the generated source data itself is unmodified/negative
        var state = new CharacterSkillState(SkillId: 5, CurrentLevel: 1, MaxLevel: 10, EffectiveTreeMembership: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        var entry = IroSkillInfoEntry.From(state, canonical);
        Assert.Equal((short)1, entry.Range);
    }

    [Fact]
    public void From_CopiesInfDirectlyFromGeneratedData()
    {
        var nvBasic = GeneratedSkillRegistry.GetById(1); // TargetType absent -> Inf 0 (passive)
        var smBash = GeneratedSkillRegistry.GetById(5);  // TargetType: Attack -> Inf 1
        var nvBasicState = new CharacterSkillState(SkillId: 1, CurrentLevel: 0, MaxLevel: 9, EffectiveTreeMembership: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        var smBashState = new CharacterSkillState(SkillId: 5, CurrentLevel: 1, MaxLevel: 10, EffectiveTreeMembership: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        Assert.Equal((ushort)0, IroSkillInfoEntry.From(nvBasicState, nvBasic).Inf);
        Assert.Equal((ushort)1, IroSkillInfoEntry.From(smBashState, smBash).Inf);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)3)]
    [InlineData((byte)9)]
    public void From_SecondaryLevelAlwaysMirrorsCurrentLevel(byte currentLevel)
    {
        var canonical = GeneratedSkillRegistry.GetById(1);
        var state = new CharacterSkillState(SkillId: 1, CurrentLevel: currentLevel, MaxLevel: 9, EffectiveTreeMembership: true, RequirementsSatisfied: true, ClientVisible: true, Upgradeable: true);
        var entry = IroSkillInfoEntry.From(state, canonical);
        Assert.Equal(currentLevel, entry.SecondaryLevel);
        Assert.Equal(entry.CurrentLevel, entry.SecondaryLevel);
    }
}
