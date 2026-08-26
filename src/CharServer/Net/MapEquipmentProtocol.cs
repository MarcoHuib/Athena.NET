using System.Buffers.Binary;
namespace Athena.Net.CharServer.Net;
internal static class MapEquipmentProtocol
{
    internal const int GetRequestLength=10,ResponseLength=13;
    internal static bool TryParseGet(ReadOnlySpan<byte> p,out uint a,out uint c){a=0;c=0;if(p.Length!=GetRequestLength||BinaryPrimitives.ReadInt16LittleEndian(p)!=PacketConstants.MapEquipmentGetRequest)return false;a=BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);c=BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);return true;}
    internal static byte[] BuildResponse(byte result,uint charId,CharacterEquipmentDto? equipment){var p=new byte[ResponseLength];BinaryPrimitives.WriteInt16LittleEndian(p,PacketConstants.MapEquipmentGetResponse);p[2]=result;BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(3),charId);if(equipment is not null){p[7]=equipment.HasRightHand?(byte)1:(byte)0;BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8),equipment.RightHandItemId);p[12]=equipment.RightHandRefine;}return p;}
}
