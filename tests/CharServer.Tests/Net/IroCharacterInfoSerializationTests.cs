using System.Buffers.Binary;
using System.Text;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class IroCharacterInfoSerializationTests
{
    [Fact]
    public void WriteCharacterInfo_WritesCapturedIro175ByteLayoutAtExpectedOffsets()
    {
        var character = new CharCharacter
        {
            CharId = 0x04030201,
            BaseExp = 0x0807060504030201,
            Zeny = 0x44332211,
            JobExp = 0x1817161514131211,
            JobLevel = 42,
            Option = 0x00000004,
            Karma = 7,
            Manner = -8,
            StatusPoint = 513,
            Hp = 0x11223344,
            MaxHp = 0x55667788,
            Sp = 0x1234,
            MaxSp = 0x5678,
            Class = 0x2345,
            Hair = 0x67,
            Body = 7,
            Weapon = 0x3456,
            BaseLevel = 200,
            SkillPoint = 321,
            HeadBottom = 11,
            Shield = 12,
            HeadTop = 13,
            HeadMid = 14,
            HairColor = 15,
            ClothesColor = 16,
            Name = "IroTester",
            Str = 21,
            Agi = 22,
            Vit = 23,
            Int = 24,
            Dex = 25,
            Luk = 26,
            CharNum = 2,
            Rename = 1,
            LastMap = "prontera",
            DeleteDate = 0,
            Robe = 17,
            Moves = 3,
            Sex = "F",
        };
        var result = new byte[ClientSession.CharacterInfoSize];

        ClientSession.WriteCharacterInfo(result, character);

        Assert.Equal(175, result.Length);
        Assert.Equal(character.CharId, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(0, 4)));
        Assert.Equal(character.BaseExp, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(4, 8)));
        Assert.Equal(character.Zeny, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(12, 4)));
        Assert.Equal(character.JobExp, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(16, 8)));
        Assert.Equal((ulong)character.Hp, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(50, 8)));
        Assert.Equal((ulong)character.MaxHp, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(58, 8)));
        Assert.Equal((ulong)character.Sp, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(66, 8)));
        Assert.Equal((ulong)character.MaxSp, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(74, 8)));
        Assert.Equal((ushort)150, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(82, 2)));
        Assert.Equal(character.Class, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(84, 2)));
        Assert.Equal(character.Hair, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(86, 2)));
        Assert.Equal(character.Body, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(88, 2)));
        Assert.Equal(character.Weapon, BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(90, 2)));
        Assert.Equal("IroTester", ReadFixedString(result.AsSpan(108, 24)));
        Assert.Equal(character.CharNum, result[138]);
        Assert.Equal("prontera", ReadFixedString(result.AsSpan(142, 16)));
        Assert.Equal(character.Robe, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(162, 4)));
        Assert.Equal(character.Moves, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(166, 4)));
        Assert.Equal((byte)0, result[174]);
    }

    [Fact]
    public void WriteCharacterInfo_RejectsAnyOtherBlockSize()
    {
        Assert.Throws<ArgumentException>(() =>
            ClientSession.WriteCharacterInfo(new byte[155], new CharCharacter()));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void WriteCharacterInfo_WritesCharacterSlotAtOffset138(byte slot)
    {
        var result = new byte[ClientSession.CharacterInfoSize];

        ClientSession.WriteCharacterInfo(result, new CharCharacter { CharNum = slot });

        Assert.Equal(slot, result[138]);
    }

    private static string ReadFixedString(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        return Encoding.UTF8.GetString(terminator >= 0 ? value[..terminator] : value);
    }
}
