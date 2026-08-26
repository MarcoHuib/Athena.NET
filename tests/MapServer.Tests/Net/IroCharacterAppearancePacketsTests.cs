using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Verified against stock-iRO capture kill-poring-heal-jobup-all-gravity-traffic.txt, frame 210
// (post-0x007D burst): D7 01 <AID> 02 B1 04 00 00 00 00 00 00 - LOOK_WEAPON val=0x000004B1=1201
// for the equipped Knife (item id, NOT its weapon_type enum value W_DAGGER=1). Struct layout
// also traced from pinned PACKET_ZC_SPRITE_CHANGE (packets_struct.hpp:2591).
public sealed class IroCharacterAppearancePacketsTests
{
    [Fact]
    public void BuildSpriteChangeWeapon_Knife_MatchesCapturedLayout()
    {
        var packet = IroCharacterAppearancePackets.BuildSpriteChangeWeapon(actorId: 9, weaponViewId: 1201);

        Assert.Equal(15, packet.Length);
        Assert.Equal((short)0x01d7, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(2, packet[6]); // LOOK_WEAPON
        Assert.Equal(1201u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7))); // captured 0x000004B1
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(11))); // no shield
    }

    [Fact]
    public void BuildSpriteChangeWeapon_Unarmed_WritesZero()
    {
        var packet = IroCharacterAppearancePackets.BuildSpriteChangeWeapon(actorId: 9, weaponViewId: 0);

        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7)));
    }

    [Fact]
    public void BuildSpriteChangeWeapon_ShieldViewId_WritesVal2()
    {
        var packet = IroCharacterAppearancePackets.BuildSpriteChangeWeapon(actorId: 9, weaponViewId: 1201, shieldViewId: 1234);

        Assert.Equal(1234u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(11)));
    }
}
