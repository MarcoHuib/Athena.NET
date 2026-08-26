using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapEquipmentProtocolTests
{
    [Fact]
    public void BuildGetRequest_WritesOpcodeAccountAndCharId()
    {
        var packet = MapEquipmentProtocol.BuildGetRequest(7, 100);

        Assert.Equal(MapEquipmentProtocol.GetRequestLength, packet.Length);
        Assert.Equal(PacketConstants.MapEquipmentGetRequest, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(7U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(100U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)));
    }

    [Fact]
    public void TryParseResponse_Success_WithRightHandEquipped_ReturnsSnapshot()
    {
        var packet = new byte[MapEquipmentProtocol.ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapEquipmentGetResponse);
        packet[2] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), 100);
        packet[7] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 1201);
        packet[12] = 2;

        Assert.True(MapEquipmentProtocol.TryParseResponse(packet, out var result, out var charId, out var equipment));
        Assert.Equal((byte)0, result);
        Assert.Equal(100U, charId);
        Assert.True(equipment.Succeeded);
        Assert.Equal(1201, equipment.Snapshot!.RightHandItemId);
        Assert.Equal((byte)2, equipment.Snapshot.RightHandRefine);
    }

    [Fact]
    public void TryParseResponse_Success_NoRightHandEquipped_ReturnsNullItemId()
    {
        var packet = new byte[MapEquipmentProtocol.ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapEquipmentGetResponse);
        packet[2] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), 100);
        packet[7] = 0;

        Assert.True(MapEquipmentProtocol.TryParseResponse(packet, out _, out _, out var equipment));
        Assert.True(equipment.Succeeded);
        Assert.Null(equipment.Snapshot!.RightHandItemId);
    }

    [Fact]
    public void TryParseResponse_FailureResult_ReturnsFailedRead_NotConfusedWithUnarmed()
    {
        var packet = new byte[MapEquipmentProtocol.ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapEquipmentGetResponse);
        packet[2] = 1;

        Assert.True(MapEquipmentProtocol.TryParseResponse(packet, out var result, out _, out var equipment));
        Assert.Equal((byte)1, result);
        Assert.False(equipment.Succeeded);
        Assert.Null(equipment.Snapshot);
    }

    [Fact]
    public void TryParseResponse_WrongLength_ReturnsFalse()
    {
        Assert.False(MapEquipmentProtocol.TryParseResponse(new byte[MapEquipmentProtocol.ResponseLength - 1], out _, out _, out _));
    }
}
