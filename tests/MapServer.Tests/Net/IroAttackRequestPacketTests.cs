using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Sanitized fixture derived from verified stock iRO capture
// kill-poring-heal-jobup.pcapng, frame 614: real captured attack request
// bytes against actor 0x00001E9D (G_PORING). Actor ID is not secret and is
// retained for traceability; no authentication/session material present.
public sealed class IroAttackRequestPacketTests
{
    private static readonly byte[] CapturedBytes = [0x37, 0x04, 0x9D, 0x1E, 0x00, 0x00, 0x07, 0x7F];

    [Fact]
    public void TryParse_CapturedBytes_ParsesExactFields()
    {
        Assert.True(IroAttackRequestPacket.TryParse(CapturedBytes, out var request));
        Assert.Equal(0x00001E9Du, request.TargetActorId);
        Assert.Equal((byte)7, request.ActionType); // DMG_REPEAT
        Assert.Equal((byte)0x7F, request.OpaqueTrailingByte);
    }

    [Fact]
    public void TryParse_WrongLength_Rejected()
    {
        Assert.False(IroAttackRequestPacket.TryParse(CapturedBytes[..7], out _));
        Assert.False(IroAttackRequestPacket.TryParse([.. CapturedBytes, 0x00], out _));
    }

    [Fact]
    public void TryParse_WrongOpcode_Rejected()
    {
        var wrongOpcode = (byte[])CapturedBytes.Clone();
        wrongOpcode[0] = 0xFF;
        Assert.False(IroAttackRequestPacket.TryParse(wrongOpcode, out _));
    }
}
