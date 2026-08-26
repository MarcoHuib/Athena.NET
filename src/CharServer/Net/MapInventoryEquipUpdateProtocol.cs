using System.Buffers.Binary;
namespace Athena.Net.CharServer.Net;
internal static class MapInventoryEquipUpdateProtocol
{
    internal const int RequestLength=18,ResponseLength=11;
    internal static bool TryParseRequest(ReadOnlySpan<byte> p,out uint a,out uint c,out uint durableId,out uint equip)
    {
        a=0;c=0;durableId=0;equip=0;
        if(p.Length!=RequestLength||BinaryPrimitives.ReadInt16LittleEndian(p)!=PacketConstants.MapInventoryEquipUpdateRequest)return false;
        a=BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
        c=BinaryPrimitives.ReadUInt32LittleEndian(p[6..]);
        durableId=BinaryPrimitives.ReadUInt32LittleEndian(p[10..]);
        equip=BinaryPrimitives.ReadUInt32LittleEndian(p[14..]);
        return true;
    }
    internal static byte[] BuildResponse(bool success,uint charId,uint durableId)
    {
        var p=new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(p,PacketConstants.MapInventoryEquipUpdateResponse);
        p[2]=success?(byte)0:(byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(3),charId);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(7),durableId);
        return p;
    }
}
