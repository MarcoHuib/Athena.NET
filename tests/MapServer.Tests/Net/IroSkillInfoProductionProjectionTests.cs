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
            entries.Add(IroSkillInfoEntry.From(state, canonical));
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
}
