using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Capture-proven client synchronization for the small set of temporary statuses/effects
// Athena models (see CharacterStatusEffectState). Verified against
// npc-interaction-heal-action.pcapng frame 3496 (reassembled TCP stream, Captain Carocc's
// "All done now? / Hunt 2 Porings.../Good luck." completion burst) - see ai/iro-2026-wire.md
// for the full byte segmentation this was derived from. Field layouts/packet IDs additionally
// cross-checked against legacy/rathena/src/map/clif.cpp (clif_status_change_sub,
// clif_skill_nodamage, clif_couplestatus).
internal static class IroStatusEffectPackets
{
    // ZC_MSG_STATE_CHANGE3 (0x0983), PACKETVER >= 20120618, 29 bytes:
    // id.W type.W actorId.L state.B totalMsec.L remainMsec.L val1.L val2.L val3.L
    // (clif.cpp:6486-6509). Capture proves activation sends the real server val1 (not a
    // hardcoded 1) despite pinned db/re/status.yml lacking SendVal1 for Blessing/Increaseagi -
    // the capture's operator config differs from the pinned snapshot for this one field; the
    // packet ID/layout/EFST mapping themselves are unaffected and fully pinned-source-derived.
    internal static byte[] BuildStatusChange3(uint actorId, ushort efstType, bool active, int totalMilliseconds, int remainMilliseconds, int val1, int val2 = 0, int val3 = 0)
    {
        var packet = new byte[29];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcMsgStateChange3);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), efstType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), actorId);
        packet[8] = (byte)(active ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(9), totalMilliseconds);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(13), remainMilliseconds);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(17), val1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(21), val2);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(25), val3);
        return packet;
    }

    // ZC_MSG_STATE_CHANGE (0x0196), 9 bytes: id.W type.W actorId.L flag.B
    // (clif_packetdb.hpp:182; clif.cpp:6488-6498 else-branch). Pinned status_change_end
    // (status.cpp:14086) sends this with flag=0 unconditionally when a status expires/ends -
    // status_change_start_post_delay's flag=1 activation always uses 0x0983 instead (this
    // PACKETVER branch), so the two packet families are asymmetric by design, not an
    // inconsistency. Not independently capture-proven (Captain's dialogue does not run long
    // enough to observe a 240s expiry in this capture); derived directly from the same
    // clif_status_change_sub builder already proven for activation.
    internal static byte[] BuildStatusChangeEnd(uint actorId, ushort efstType)
    {
        var packet = new byte[9];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcMsgStateChange);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), efstType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), actorId);
        packet[8] = 0;
        return packet;
    }

    // ZC_USE_SKILL (0x09CB), PACKETVER_RE >= 20130724, 17 bytes:
    // id.W skillId.W level.L(i32) targetActorId.L srcActorId.L result.B
    // (packets_struct.hpp:4674-4683). Built by clif_skill_nodamage(src, dst, skillId, heal);
    // for a non-heal skill the 4th argument is repurposed as the displayed skill level
    // (legacy/rathena/src/map/skills/acolyte/incagi.cpp:18 passes skill_lv there). The capture
    // proves src = the casting NPC's actor ID (Captain) and target = the player for all three
    // observed skill visuals (AL_INCAGI=29, AL_BLESSING=34, AL_HEAL=28); the exact pinned
    // call site attributing src=Captain rather than src=player for the skilleffect-driven
    // packets was not conclusively located in static source (see ai/iro-2026-wire.md), but
    // the wire layout, field values, and src/target semantics are unambiguous and used as-is
    // per this project's capture-over-source evidence priority.
    internal static byte[] BuildUseSkillVisual(ushort skillId, int level, uint targetActorId, uint srcActorId)
    {
        var packet = new byte[17];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcUseSkill);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), skillId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), level);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), targetActorId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), srcActorId);
        packet[16] = 1; // result: success
        return packet;
    }

    // ZC_COUPLESTATUS (0x0141), 14 bytes: id.W statusType.L(u32) baseStatus.L(i32) plusStatus.L(i32)
    // (clif.cpp:3608-3618). Sent by clif_updatestatus(SP_STR/SP_AGI/SP_INT/SP_DEX/...) whenever
    // a base stat's effective (battle_status) value differs from its persisted (status) value -
    // "plusStatus" is exactly that delta, i.e. the temporary-status bonus. statusType uses the
    // _sp enum (map.hpp:500-501): SP_STR=13, SP_AGI=14, SP_VIT=15, SP_INT=16, SP_DEX=17, SP_LUK=18.
    internal static byte[] BuildCoupleStatus(ushort statusType, int baseValue, int plusValue)
    {
        var packet = new byte[14];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcCoupleStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), statusType);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6), baseValue);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(10), plusValue);
        return packet;
    }

    // _sp enum values (legacy/rathena/src/map/map.hpp:500-501) used by BuildCoupleStatus. All
    // six are independently confirmed by the base-stat-allocation capture (statsonly.pcapng,
    // ai/iro-2026-wire.md) as the StatusId field of both 0x00BB (client request) and 0x00BC
    // (server ack) - SpVit/SpLuk were added alongside that capture; SpStr/SpAgi/SpInt/SpDex
    // pre-date it (added for CharacterStatusEffectState's temporary-status resync).
    internal const ushort SpStr = 13;
    internal const ushort SpAgi = 14;
    internal const ushort SpVit = 15;
    internal const ushort SpInt = 16;
    internal const ushort SpDex = 17;
    internal const ushort SpLuk = 18;

    // Pinned legacy/rathena/src/map/status.hpp enum efst_type (EFST_BLANK = -1 origin).
    internal const ushort EfstBlessing = 10;
    internal const ushort EfstIncAgi = 12;

    // Pinned legacy/rathena/src/map/skill.hpp enum e_skill (NV_BASIC = 1 origin).
    internal const ushort AlHeal = 28;
}
