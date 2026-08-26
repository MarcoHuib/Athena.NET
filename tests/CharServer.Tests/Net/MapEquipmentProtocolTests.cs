using System.Buffers.Binary;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapEquipmentProtocolTests
{
    [Fact]
    public void TryParseGet_ValidPacket_ReadsAccountAndCharId()
    {
        var packet = new byte[MapEquipmentProtocol.GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapEquipmentGetRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), 100);

        Assert.True(MapEquipmentProtocol.TryParseGet(packet, out var accountId, out var charId));
        Assert.Equal(7U, accountId);
        Assert.Equal(100U, charId);
    }

    [Fact]
    public void TryParseGet_WrongOpcodeOrLength_ReturnsFalse()
    {
        var wrongOpcode = new byte[MapEquipmentProtocol.GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(wrongOpcode, PacketConstants.MapGameplayStateGetRequest);
        Assert.False(MapEquipmentProtocol.TryParseGet(wrongOpcode, out _, out _));

        var wrongLength = new byte[MapEquipmentProtocol.GetRequestLength - 1];
        Assert.False(MapEquipmentProtocol.TryParseGet(wrongLength, out _, out _));
    }

    [Fact]
    public void BuildResponse_WithEquipment_RoundTripsRightHandFields()
    {
        var equipment = new CharacterEquipmentDto(HasRightHand: true, RightHandItemId: 1201, RightHandRefine: 3);
        var response = MapEquipmentProtocol.BuildResponse(0, 100, equipment);

        Assert.Equal(MapEquipmentProtocol.ResponseLength, response.Length);
        Assert.Equal(PacketConstants.MapEquipmentGetResponse, BinaryPrimitives.ReadInt16LittleEndian(response));
        Assert.Equal((byte)0, response[2]);
        Assert.Equal(100U, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(3)));
        Assert.Equal((byte)1, response[7]);
        Assert.Equal(1201U, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)));
        Assert.Equal((byte)3, response[12]);
    }

    [Fact]
    public void BuildResponse_NoRightHandEquipped_HasRightHandFalse()
    {
        var equipment = new CharacterEquipmentDto(HasRightHand: false, RightHandItemId: 0, RightHandRefine: 0);
        var response = MapEquipmentProtocol.BuildResponse(0, 100, equipment);

        Assert.Equal((byte)0, response[7]);
    }

    [Fact]
    public void BuildResponse_FailureResult_LeavesEquipmentFieldsZeroed()
    {
        var response = MapEquipmentProtocol.BuildResponse(1, 100, null);

        Assert.Equal((byte)1, response[2]);
        Assert.Equal((byte)0, response[7]);
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)));
    }
}
