using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Pure current-iRO PC actor serializers. The shared header/public-appearance
// projection follows pinned packet_*_unit at the project's PACKETVER and the
// Prontera extraction's capture-verified dynamic-name boundaries.
internal static class IroPlayerActorPackets
{
    private const int StandFixedLength = 84;
    private const int SpawnFixedLength = 83;
    private const int WalkFixedLength = 90;

    internal static byte[] BuildStandEntry(PlayerPresence player) => BuildIdleLike(player, PacketConstants.ZcNotifyStandEntry, StandFixedLength, hasState: true);
    internal static byte[] BuildSpawnEntry(PlayerPresence player) => BuildIdleLike(player, PacketConstants.ZcNotifyNewEntry, SpawnFixedLength, hasState: false);

    internal static byte[] BuildWalkEntry(PlayerPresence player)
    {
        var movement = player.Movement ?? throw new ArgumentException("A walking entry requires authoritative movement state.", nameof(player));
        var name = EncodeName(player.CharacterName);
        var packet = new byte[WalkFixedLength + name.Length];
        WriteCommonHeader(packet, PacketConstants.ZcNotifyMoveEntry, player);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(37), movement.StartTick);
        WriteWalkAppearance(packet, player);
        IroCoordinatePacking.WriteMovement(packet.AsSpan(67, 6), player.X, player.Y, movement.DestinationX, movement.DestinationY);
        packet[73] = 5;
        packet[74] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(75), player.BaseLevel);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(77), player.Font);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(79), -1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(83), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(88), player.BodyStyle);
        name.CopyTo(packet.AsSpan(WalkFixedLength));
        return packet;
    }

    internal static byte[] BuildVanish(uint actorId) => IroMonsterCombatPackets.BuildNotifyVanish(actorId, PacketConstants.ZcNotifyVanishReasonOutOfSight);

    internal static byte[] BuildDirection(uint actorId, byte headDirection, byte bodyDirection)
    {
        var packet = new byte[PacketConstants.ZcChangeDirectionLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcChangeDirection);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), headDirection);
        packet[8] = bodyDirection;
        return packet;
    }

    internal static byte[] BuildPlayerInfo(PlayerPresence player)
    {
        var packet = new byte[PacketConstants.ZcPlayerInfoLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcPlayerInfo);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), player.ActorId);
        WriteFixedString(packet.AsSpan(6, 24), player.CharacterName);
        WriteFixedString(packet.AsSpan(30, 24), player.PartyName);
        WriteFixedString(packet.AsSpan(54, 24), player.GuildName);
        WriteFixedString(packet.AsSpan(78, 24), player.GuildPositionName);
        // title_id at 102 remains zero: Athena has no authoritative title subsystem.
        return packet;
    }

    private static byte[] BuildIdleLike(PlayerPresence player, short packetId, int fixedLength, bool hasState)
    {
        var name = EncodeName(player.CharacterName);
        var packet = new byte[fixedLength + name.Length];
        WriteCommonHeader(packet, packetId, player);
        WriteIdleAppearance(packet, player);
        IroCoordinatePacking.WritePosition(packet.AsSpan(63, 3), player.X, player.Y, player.Direction);
        packet[66] = 5;
        packet[67] = 5;
        var levelOffset = hasState ? 69 : 68;
        if (hasState) packet[68] = 0; // authoritative standing/alive state; death/sit is out of scope.
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(levelOffset), player.BaseLevel);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(levelOffset + 2), player.Font);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(levelOffset + 4), -1);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(levelOffset + 8), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(levelOffset + 13), player.BodyStyle);
        name.CopyTo(packet.AsSpan(fixedLength));
        return packet;
    }

    private static void WriteCommonHeader(Span<byte> packet, short packetId, PlayerPresence player)
    {
        BinaryPrimitives.WriteInt16LittleEndian(packet, packetId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[2..], (ushort)packet.Length);
        packet[4] = 0; // PC_TYPE
        BinaryPrimitives.WriteUInt32LittleEndian(packet[5..], player.ActorId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[9..], player.CharacterId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[13..], player.WalkSpeed);
        // bodyState/healthState are zero; persisted Option is server-owned public effect state.
        BinaryPrimitives.WriteUInt32LittleEndian(packet[19..], player.Option);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[23..], player.JobClass);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[25..], player.HairStyle);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[27..], player.WeaponAppearance);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[31..], player.ShieldAppearance);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[35..], player.HeadBottomAppearance);
    }

    private static void WriteIdleAppearance(Span<byte> packet, PlayerPresence player)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(packet[37..], player.HeadTopAppearance);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[39..], player.HeadMidAppearance);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[41..], player.HairColor);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[43..], player.ClothesColor);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[45..], player.HeadDirection);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[47..], player.RobeAppearance);
        // guild id/emblem remain zero: no Athena guild association exists.
        BinaryPrimitives.WriteInt16LittleEndian(packet[55..], player.Manner);
        packet[61] = player.Karma == 0 ? (byte)0 : (byte)1;
        packet[62] = player.Sex;
    }

    private static void WriteWalkAppearance(Span<byte> packet, PlayerPresence player)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(packet[41..], player.HeadTopAppearance);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[43..], player.HeadMidAppearance);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[45..], player.HairColor);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[47..], player.ClothesColor);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[49..], player.HeadDirection);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[51..], player.RobeAppearance);
        BinaryPrimitives.WriteInt16LittleEndian(packet[59..], player.Manner);
        packet[65] = player.Karma == 0 ? (byte)0 : (byte)1;
        packet[66] = player.Sex;
    }

    private static byte[] EncodeName(string value)
    {
        var encoded = Encoding.ASCII.GetBytes(value);
        if (encoded.Length is 0 or > PacketConstants.NameLength) throw new ArgumentException("Player name must be 1-24 ASCII bytes.", nameof(value));
        return encoded;
    }

    private static void WriteFixedString(Span<byte> destination, string value)
    {
        destination.Clear();
        var encoded = Encoding.ASCII.GetBytes(value);
        encoded.AsSpan(0, Math.Min(encoded.Length, destination.Length - 1)).CopyTo(destination);
    }
}
