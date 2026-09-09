using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// PINNED-SOURCE-BACKED, NOT capture-verified: no stock-iRO capture of ZC_ATTACK_FAILURE_FOR_
// DISTANCE has been independently obtained for this project yet. The byte layout below is derived
// directly from pinned struct PACKET_ZC_ATTACK_FAILURE_FOR_DISTANCE (packets_struct.hpp:5419-5426)
// and pinned clif_movetoattack (clif.cpp:8172-8185), which is the ONLY place this project's
// pinned-source tree constructs and sends this packet:
//
//   void clif_movetoattack( const map_session_data& sd, block_list& bl ){
//       PACKET_ZC_ATTACK_FAILURE_FOR_DISTANCE packet{};
//       packet.PacketType = HEADER_ZC_ATTACK_FAILURE_FOR_DISTANCE;
//       packet.targetAID = bl.id;
//       packet.targetXPos = bl.x;
//       packet.targetYPos = bl.y;
//       packet.xPos = sd.x;
//       packet.yPos = sd.y;
//       packet.currentAttRange = sd.battle_status.rhw.range;
//       clif_send( &packet, sizeof( packet ), &sd, SELF );
//   }
//
// Sent by pinned unit_attack_timer_sub (unit.cpp:3251-3256) when a PC's own attack request target
// fails check_distance_client_bl - i.e. exactly the case this project's own combat-range slice
// exists to fix (a client-initiated attack against a target beyond the resolved attack range must
// never deal damage, and must instead let the stock client perform its own move-to-attack
// behavior in response to this packet).
internal static class IroCombatDistancePackets
{
    // Fields are all CURRENT authoritative state at the moment of the rejected attack attempt -
    // targetX/targetY is the target's CURRENT resolved position (MobInstance.GetPosition(), not a
    // stale/cached one), playerX/playerY is the attacker's CURRENT authoritative position (after
    // SyncPositionToNow()), and currentAttRange is the attacker's CURRENT resolved BASE basic-attack
    // range (BasicAttackRangeResolver.Resolve against the CURRENT equipped weapon - this project's
    // own `resolvedRange`) - never a fixed/hardcoded value for any of the three, and, per pinned
    // clif_movetoattack's own `sd.battle_status.rhw.range` (the raw base value, see this file's own
    // doc comment above), NEVER the temporary +1 "chasing" bonus a caller's own distance-CHECK may
    // separately apply on top of this same base value for legality purposes only - callers must pass
    // the pre-bonus resolvedRange here even when the actual accept/reject decision used a larger
    // bonused value.
    internal static byte[] BuildAttackFailureForDistance(uint targetActorId, ushort targetX, ushort targetY, ushort playerX, ushort playerY, ushort currentAttackRange)
    {
        var packet = new byte[PacketConstants.ZcAttackFailureForDistanceLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcAttackFailureForDistance);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), targetActorId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), targetX);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), targetY);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(10), playerX);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12), playerY);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), currentAttackRange);
        return packet;
    }
}
