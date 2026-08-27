using System.Buffers.Binary;
using System.Text;

namespace Athena.Net.MapServer.Net;

// Client-facing monster actor-appearance serializer. Distinct from
// IroWorldActorPackets.BuildWorldActor (NPC/warp, object type 6, always
// 0xFFFFFFFF HP sentinels): pinned rAthena sends object type 5
// (NPC_MOB_TYPE, clif_bl_type clif.cpp:345-384) for a real monster, and its
// HP fields carry the unit's real current/max HP once damaged - see
// ai/iro-2026-wire.md's "Monster combat and quest drops" evidence.
//
// Verified against kill-poring-heal-jobup.pcapng frame 566 (G_PORING, actor
// 0x00001E9D, class 2401, position (75,51)). The captured 0x09FF instance
// is a clean, self-consistent 90-byte packet (declared length 0x005A = 90,
// immediately followed in the same TCP segment by a 96-byte 0x09FD for the
// same actor; 90+96=186 matches the captured TCP payload exactly). An
// earlier draft of this file mis-transcribed the capture (one 16-byte
// all-zero hex row dropped), producing a false apparent length-field
// anomaly; that was a transcription error, not a real capture artifact, and
// has been corrected.
//
// Proven field layout - IDENTICAL header shape to the existing
// IroWorldActorPackets.BuildWorldActor (position@63, radii@66/67,
// HP@73/77, name@84), confirming this capture instance used
// PACKETVER-generation family ZC_NOTIFY_STANDENTRY11 exactly like the
// already-proven WARPNPC packets, differing only in objecttype (5 vs 6) and
// carrying a real HP sentinel/value pair instead of an always-fixed one:
//   0   id 0x09FF
//   2   packet length (u16) - always fixedLength(84) + name.Length
//   4   objecttype = 5 (NPC_MOB_TYPE)
//   5   actorId (u32)
//   9   4 bytes zero (opaque)
//   13  speed (u16) - matches MobDefinition.WalkSpeed (400 for G_PORING)
//   15  8 bytes zero (opaque: bodyState/healthState/effectState region)
//   23  class/job (u16) - matches MobDefinition.Id (2401 for G_PORING)
//   25  38 bytes zero (opaque: appearance fields, a monster has none)
//   63  packed position (3 bytes: 10-bit x, 10-bit y, 4-bit direction)
//   66  radiusX, radiusY (2 bytes) then 5 bytes zero/flags (opaque)
//   73  8-byte HP sentinel/value (maxHP.L, HP.L) - matches pinned
//       clif_set_unit_idle (clif.cpp:1165-1181): 0xFFFFFFFF/0xFFFFFFFF
//       (-1/-1) when the unit is at full HP or HP-bar display is disabled;
//       real current/max values otherwise.
//   81  3 bytes zero (opaque)
//   84  name (ASCII, no NUL, no padding - ends the packet)
internal static class IroMonsterActorPackets
{
    private const int FixedLength = 84;

    internal static byte[] BuildStandEntry(uint actorId, ushort mobClassId, ushort mobWalkSpeed, string name, ushort x, ushort y, byte direction, uint currentHp, uint maxHp)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var packet = new byte[FixedLength + nameBytes.Length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyStandEntry);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[4] = 5; // objecttype = NPC_MOB_TYPE
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), actorId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(13), mobWalkSpeed);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(23), mobClassId);
        WritePosition(packet.AsSpan(63, 3), x, y, direction);

        // maxHP=-1,HP=-1 sentinel when at full HP (matches pinned clif_set_unit_idle's
        // "no HP bar" branch); real values when damaged, matching the same source's
        // battle_config.monster_hp_bars_info branch for a damaged monster.
        if (currentHp < maxHp)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(73), (int)maxHp);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(77), (int)currentHp);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(73), -1);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(77), -1);
        }

        nameBytes.CopyTo(packet.AsSpan(FixedLength));
        return packet;
    }

    private static void WritePosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }

    // Verified against the SAME kill-poring-heal-jobup.pcapng frame 566 as BuildStandEntry above -
    // the capture's 90-byte fixed portion (96 total - "Poring".Length=6) for 0x09FD immediately
    // follows the 90-byte 0x09FF in the same TCP segment (90+90+6+6=... see BuildStandEntry's own
    // doc comment for the exact 90+96=186 reconciliation). ai/iro-2026-wire.md decoded this
    // instance's packed movement field as (75,51)->(75,57) subcell 8/8 - a Poring walking one cell
    // north.
    //
    // Field layout independently re-derived here from pinned packet_unit_walking
    // (packets_struct.hpp:758-830) at PACKETVER 20220406 (matches packet_idle_unit's shared header
    // exactly through `accessory`, confirming this is the same ZC_NOTIFY_*ENTRY11 packet family as
    // BuildStandEntry, not a coincidentally similar shape):
    //   0   id 0x09FD
    //   2   packet length (u16) - fixedLength(90) + name.Length
    //   4   objecttype = 5 (NPC_MOB_TYPE)
    //   5   actorId (u32)
    //   9   4 bytes zero (opaque)
    //   13  speed (u16) - matches MobDefinition.WalkSpeed
    //   15  8 bytes zero (opaque: bodyState/healthState/effectState region)
    //   23  class/job (u16) - matches MobDefinition.Id
    //   25  12 bytes zero (opaque: head/weapon/shield/accessory - a monster has none)
    //   37  moveStartTime (u32) - client_tick(gettick()) per pinned clif_set_unit_walking;
    //       server-tick-based, NOT itself independently capture-verified as to which exact tick
    //       base Athena must reproduce bit-for-bit - see this method's own parameter doc comment.
    //   41  26 bytes zero (opaque: remaining appearance/guild/palette fields - a monster has none)
    //   67  packed src/dst/subcell movement (6 bytes) - WBUFPOS2 layout (clif.cpp:182-190):
    //       byte0 = srcX>>2; byte1 = (srcX<<6)|((srcY>>4)&0x3f); byte2 = (srcY<<4)|((dstX>>6)&0xf);
    //       byte3 = (dstX<<2)|((dstY>>8)&0x3); byte4 = dstY; byte5 = (subX<<4)|(subY&0xf).
    //       subX/subY are always 8/8 in the captured instance (pinned clif_set_unit_walking always
    //       passes ud.sx/ud.sy, which pinned unit_walktoxy_sub initializes to 8/8 for a fresh
    //       walk - matching the captured value, not independently re-derived from first
    //       principles here).
    //   73  6 bytes zero (opaque: xSize/ySize/clevel/font - a monster has none)
    //   79  8-byte HP sentinel/value (maxHP.L, HP.L) - identical semantics to BuildStandEntry.
    //   87  3 bytes zero (opaque: isBoss/body)
    //   90  name (ASCII, no NUL, no padding - ends the packet, same convention as BuildStandEntry)
    internal static byte[] BuildWalkEntry(uint actorId, ushort mobClassId, ushort mobWalkSpeed, string name, ushort srcX, ushort srcY, ushort dstX, ushort dstY, uint moveStartTime, uint currentHp, uint maxHp)
    {
        const int walkFixedLength = 90;
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var packet = new byte[walkFixedLength + nameBytes.Length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyMoveEntry);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[4] = 5; // objecttype = NPC_MOB_TYPE
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), actorId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(13), mobWalkSpeed);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(23), mobClassId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(37), moveStartTime);
        WriteMoveData(packet.AsSpan(67, 6), srcX, srcY, dstX, dstY);

        if (currentHp < maxHp)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(79), (int)maxHp);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(83), (int)currentHp);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(79), -1);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(83), -1);
        }

        nameBytes.CopyTo(packet.AsSpan(walkFixedLength));
        return packet;
    }

    // Pinned WBUFPOS2 (clif.cpp:182-190). subX/subY are hardcoded to 8 (see BuildWalkEntry's own
    // doc comment on why: the only capture-verified value, matching pinned unit_walktoxy_sub's own
    // fresh-walk-start default).
    private static void WriteMoveData(Span<byte> buffer, ushort srcX, ushort srcY, ushort dstX, ushort dstY)
    {
        const byte subX = 8;
        const byte subY = 8;
        buffer[0] = (byte)(srcX >> 2);
        buffer[1] = (byte)((srcX << 6) | ((srcY >> 4) & 0x3f));
        buffer[2] = (byte)((srcY << 4) | ((dstX >> 6) & 0x0f));
        buffer[3] = (byte)((dstX << 2) | ((dstY >> 8) & 0x03));
        buffer[4] = (byte)dstY;
        buffer[5] = (byte)((subX << 4) | (subY & 0x0f));
    }

    // Capture-verified (kill-poring-heal-jobup.pcapng frame 674, ai/iro-2026-wire.md): ZC_STOPMOVE
    // (0x0088, clif.cpp:2204), fixed 10 bytes: id.L x.W y.W. Captured stopping the SAME Poring the
    // 0x09FD in frame 566 was walking, landing at its walk's own destination (75,57) - used here to
    // notify a client that an already-visible, already-walking monster's walk just finished
    // (MonsterMovementChangeKind.WalkFinished), so the client's own local walk animation/prediction
    // is corrected to the authoritative final cell rather than drifting from missed/late updates.
    internal static byte[] BuildStopMove(uint actorId, ushort x, ushort y)
    {
        var packet = new byte[PacketConstants.ZcStopMoveLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcStopMove);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), x);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), y);
        return packet;
    }
}
