using System.Buffers.Binary;
using System.Text;

namespace Athena.Net.MapServer.Net;

public static class IroNpcDialoguePackets
{
    public static bool TryParseInteraction(ReadOnlySpan<byte> packet, out uint actorId)
        => TryParseActorPacket(packet, PacketConstants.IroCzNpcInteraction, PacketConstants.IroCzNpcInteractionLength, out actorId);
    public static bool TryParseNext(ReadOnlySpan<byte> packet, out uint actorId)
        => TryParseActorPacket(packet, PacketConstants.IroCzNpcNext, PacketConstants.IroCzNpcNextLength, out actorId);
    public static bool TryParseClose(ReadOnlySpan<byte> packet, out uint actorId)
        => TryParseActorPacket(packet, PacketConstants.IroCzNpcClose, PacketConstants.IroCzNpcCloseLength, out actorId);
    public static bool TryParseSelection(ReadOnlySpan<byte> packet, out uint actorId, out byte wireIndex, out byte opaqueTrailingByte)
    {
        wireIndex = 0; opaqueTrailingByte = 0;
        if (!TryParseActorPacket(packet, PacketConstants.IroCzNpcSelection, PacketConstants.IroCzNpcSelectionLength, out actorId)) return false;
        wireIndex = packet[6]; opaqueTrailingByte = packet[7]; return true;
    }

    public static byte[] BuildMessage(uint actorId, string text)
    {
        if (text.Any(character => character > 0x7f)) throw new ArgumentException("Captured dialogue encoding is ASCII-only.", nameof(text));
        var encoded = Encoding.ASCII.GetBytes(text);
        var packet = new byte[9 + encoded.Length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNpcMessage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), actorId);
        encoded.CopyTo(packet.AsSpan(8));
        return packet;
    }

    public static byte[] BuildNext(uint actorId) => BuildServerActorPacket(PacketConstants.ZcNpcNext, actorId);
    public static byte[] BuildClose(uint actorId) => BuildServerActorPacket(PacketConstants.ZcNpcClose, actorId);
    public static byte[] BuildCutin(string image, byte position)
    {
        if (image.Any(character => character > 0x7f)) throw new ArgumentException("Captured cutin encoding is ASCII-only.", nameof(image));
        var wireName = image.Length == 0 || image.EndsWith(".BMP", StringComparison.OrdinalIgnoreCase) ? image : image + ".BMP";
        var encoded = Encoding.ASCII.GetBytes(wireName);
        if (encoded.Length >= 64) throw new ArgumentException("Cutin image exceeds the 63-byte iRO field.", nameof(image));
        var packet = new byte[67];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcShowImage);
        encoded.CopyTo(packet.AsSpan(2, 64));
        packet[66] = position;
        return packet;
    }
    public static byte[] BuildMenu(uint actorId, IReadOnlyList<string> options)
    {
        if (options.Count == 0 || options.Any(option => option.Length == 0 || option.Contains(':') || option.Any(character => character > 0x7f))) throw new ArgumentException("Captured menus require non-empty ASCII options without colons.", nameof(options));
        var encoded = Encoding.ASCII.GetBytes(string.Join(':', options) + ":");
        var packet = new byte[9 + encoded.Length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNpcMenu);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), actorId);
        encoded.CopyTo(packet.AsSpan(8));
        return packet;
    }

    private static bool TryParseActorPacket(ReadOnlySpan<byte> packet, short expectedType, int expectedLength, out uint actorId)
    {
        actorId = 0;
        if (packet.Length != expectedLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != expectedType) return false;
        actorId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]);
        return true;
    }

    private static byte[] BuildServerActorPacket(short type, uint actorId)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, type);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        return packet;
    }
}
