using System.Buffers.Binary;
using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Regression test for the exact production 0x0B32 pipeline (MapClientSession.
// BuildIroSkillInfoListPacket): GeneratedSkillTreeRegistry -> CharacterSkillService.
// CalculateEffectiveState -> filter ClientVisible -> GeneratedSkillRegistry.GetById ->
// IroSkillInfoEntry.From -> IroSkillInfoListPackets.Build. This is the SAME sequence
// MapClientSession runs - it does NOT manually construct an IroSkillInfoEntry, unlike
// IroSkillInfoListPacketsTests's pure serializer tests.
//
// This is the regression that would have caught the historical production bug where generated
// NV_BASIC.Range was 0 (pinned source has no Range field for NV_BASIC, which pinned
// SkillDatabase::parseBodyNode zero-fills) while the official stock-iRO capture proves range=1 -
// a manually-constructed IroSkillInfoEntry test can never observe that divergence because it never
// reads GeneratedSkillRegistry at all.
public sealed class IroSkillInfoProductionProjectionTests
{
    private static CharacterGameplayState FreshNoviceJobLevel2SkillPoint1() => new(
        CharacterId: 9, Version: 0, JobClass: (ushort)JobClass.Novice, BaseLevel: 1, JobLevel: 2,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 11, MaxHp: 40, MaxSp: 11,
        StatPoints: 0, SkillPoints: 1, Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);

    // Mirrors MapClientSession.BuildIroSkillInfoListPacket's exact production sequence.
    private static byte[] BuildProductionSkillListPacket(CharacterGameplayState gameplay, CharacterSkillSnapshot skills)
    {
        var tree = GeneratedSkillTreeRegistry.Get(gameplay.JobClass);
        var effective = CharacterSkillService.CalculateEffectiveState(gameplay, skills, tree, out _);
        var entries = new List<IroSkillInfoEntry>();
        foreach (var state in effective)
        {
            if (!state.ClientVisible) continue;
            var canonical = GeneratedSkillRegistry.GetById(state.SkillId);
            entries.Add(IroSkillInfoEntry.From(state, canonical, skills));
        }
        return IroSkillInfoListPackets.Build(entries);
    }

    // The mandatory end-to-end regression (task correction section 10): a fresh Novice's FIRST
    // 0x0B32 entry (NV_BASIC) must match the official stock-iRO capture byte-for-byte, built
    // through the real production projection - not a manually-provided Range: 1 fixture value.
    [Fact]
    public void FreshNovice_FirstEntry_MatchesOfficialCaptureByteForByte()
    {
        var packet = BuildProductionSkillListPacket(FreshNoviceJobLevel2SkillPoint1(), CharacterSkillSnapshot.Empty);

        Assert.True(packet.Length >= IroSkillInfoListPackets.HeaderLength + IroSkillInfoListPackets.EntryLength);
        Assert.Equal(PacketConstants.ZcSkillInfoList, BinaryPrimitives.ReadInt16LittleEndian(packet));

        var firstEntry = packet.AsSpan(IroSkillInfoListPackets.HeaderLength, IroSkillInfoListPackets.EntryLength);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry));        // SkillId = NV_BASIC
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(firstEntry[2..]));             // inf = 0
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry[6..]));    // currentLevel = 0
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry[8..]));    // spCost = 0
        // THE historical regression: this line fails against unfixed production code, which would
        // emit 0 (the pinned generated value) instead of the verified stock-iRO capture's 1.
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry[10..]));   // range = 1 (verified capture, see IroWireCompatibility)
        Assert.Equal((byte)1, firstEntry[12]);                                                 // upgradable = 1
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry[13..]));   // secondaryLevel = 0
    }

    // Same production pipeline, but with SkillPoints=0 - learned/eligible skills must still appear
    // (task section 45), proven through the real projection, not a hand-built entry.
    [Fact]
    public void FreshNovice_ZeroSkillPoints_StillEmitsNvBasicEntry()
    {
        var gameplay = FreshNoviceJobLevel2SkillPoint1() with { SkillPoints = 0 };
        var packet = BuildProductionSkillListPacket(gameplay, CharacterSkillSnapshot.Empty);
        var firstEntry = packet.AsSpan(IroSkillInfoListPackets.HeaderLength, IroSkillInfoListPackets.EntryLength);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry));
        Assert.Equal((byte)1, firstEntry[12]); // still upgradable=1 - pinned upFlag is not gated on SkillPoints
    }

    // A learned NV_BASIC (level 3) must reflect its actual persisted level/cost/secondaryLevel
    // through the same real production pipeline - not just the unlearned level-0 capture case.
    [Fact]
    public void LearnedNvBasic_ReflectsPersistedLevelThroughProductionPipeline()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 3, CharSkillFlag.Permanent)]);
        var packet = BuildProductionSkillListPacket(FreshNoviceJobLevel2SkillPoint1(), skills);
        var firstEntry = packet.AsSpan(IroSkillInfoListPackets.HeaderLength, IroSkillInfoListPackets.EntryLength);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry[6..]));  // currentLevel
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(firstEntry[13..])); // secondaryLevel mirrors it
    }

    private static ReadOnlySpan<byte> FindEntry(byte[] packet, ushort skillId)
    {
        var count = (packet.Length - IroSkillInfoListPackets.HeaderLength) / IroSkillInfoListPackets.EntryLength;
        for (var i = 0; i < count; i++)
        {
            var entry = packet.AsSpan(IroSkillInfoListPackets.HeaderLength + i * IroSkillInfoListPackets.EntryLength, IroSkillInfoListPackets.EntryLength);
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) == skillId) return entry;
        }
        throw new InvalidOperationException($"SkillId {skillId} not found in packet.");
    }

    // Real generated Knight tree, real per-level KN_SPEARBOOMERANG (59) Range data
    // ([3,5,7,9,11]) through the full production pipeline - the exact regression that would have
    // caught the historical "per-level Range silently generated as 0" compiler bug (task
    // correction section 1). KN_SPEARBOOMERANG requires SM_BASH(56) >= 3 to become normally
    // learnable from scratch; persisting it directly at level 2 proves the "already learned
    // survives the gate" rule while exercising its real per-level range at that level.
    [Fact]
    public void Knight_SpearBoomerang_ResolvesRealPerLevelRangeThroughProductionPipeline()
    {
        var gameplay = FreshNoviceJobLevel2SkillPoint1() with { JobClass = (ushort)JobClass.Knight, BaseLevel = 99, JobLevel = 50 };
        var skills = CharacterSkillSnapshot.FromLogin([(59, 2, CharSkillFlag.Permanent)]); // KN_SPEARBOOMERANG level 2
        var packet = BuildProductionSkillListPacket(gameplay, skills);
        var entry = FindEntry(packet, 59);

        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..])); // currentLevel
        // Real pinned per-level Range for KN_SPEARBOOMERANG: [3,5,7,9,11] - level 2 -> 5, never
        // the pre-fix generated zero.
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]));
    }

    // Real generated Archer tree: AC_DOUBLE (46, Flags.AlterRangeVulture, Range: -9) resolved
    // through the full production pipeline at two different AC_VULTURE (44) learned levels,
    // proving the companion-skill-level range modifier is generic (driven by
    // GeneratedSkillDefinition.RangeFlags, not a hardcoded AC_DOUBLE/AC_VULTURE runtime case) and
    // actually wired end-to-end from CharacterSkillSnapshot through IroSkillRangeResolver.
    [Theory]
    [InlineData((byte)0, (ushort)9)]  // abs(-9) + 0 (no AC_VULTURE learned)
    [InlineData((byte)5, (ushort)14)] // abs(-9) + 5
    public void Archer_Double_RangeIncludesAcVultureLearnedLevel(byte vultureLevel, ushort expectedRange)
    {
        var gameplay = FreshNoviceJobLevel2SkillPoint1() with { JobClass = (ushort)JobClass.Archer, BaseLevel = 99, JobLevel = 50 };
        var rows = new List<(ushort SkillId, byte Level, CharSkillFlag Flag)> { (46, 1, CharSkillFlag.Permanent) }; // AC_DOUBLE level 1
        if (vultureLevel > 0) rows.Add((44, vultureLevel, CharSkillFlag.Permanent)); // AC_VULTURE
        var skills = CharacterSkillSnapshot.FromLogin(rows);
        var packet = BuildProductionSkillListPacket(gameplay, skills);
        var entry = FindEntry(packet, 46);

        Assert.Equal(expectedRange, BinaryPrimitives.ReadUInt16LittleEndian(entry[10..]));
    }
}
