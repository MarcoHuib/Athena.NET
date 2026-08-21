using System.Buffers.Binary;
using System.Net;
using System.Text;
using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class IroCharProtocolTests
{
    [Fact]
    public void InitialHandshake_UsesCapturedSlotsAndSyncCountWithoutLegacyPackets()
    {
        var config = new CharConfig
        {
            IroRenewalCompatibility = true,
            MinChars = 9,
            MaxChars = 15,
        };

        var slotInfo = ClientSession.BuildAcceptEnter2Packet(config, 0, 0, 9);
        var pageSync = ClientSession.BuildCharListNotifyPacket(PacketConstants.IroCharSyncCount);
        var packetIds = new[]
        {
            BinaryPrimitives.ReadInt16LittleEndian(slotInfo),
            BinaryPrimitives.ReadInt16LittleEndian(pageSync),
        };

        Assert.Equal(new short[] { 0x082d, 0x09a0 }, packetIds);
        Assert.DoesNotContain((short)0x006b, packetIds);
        Assert.DoesNotContain((short)0x020d, packetIds);
        Assert.Equal(new byte[] { 9, 9, 0, 9, 9 }, slotInfo.AsSpan(4, 5).ToArray());
        Assert.Equal((uint)12, BinaryPrimitives.ReadUInt32LittleEndian(pageSync.AsSpan(2, 4)));
    }

    [Fact]
    public void ParseCharacterCreate_ReadsEvery0a39FieldAndStopsNameAtFirstNull()
    {
        var packet = new byte[36];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x0a39);
        Encoding.ASCII.GetBytes("kaaskaaskaas\0ignoreddata").CopyTo(packet.AsSpan(2, 24));
        packet[26] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(27, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(29, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(31, 4), 0);
        packet[35] = 1;

        var parsed = ClientSession.ParseIroCharacterCreate(packet);

        Assert.Equal("kaaskaaskaas", parsed.Name);
        Assert.Equal((byte)0, parsed.Slot);
        Assert.Equal((ushort)0, parsed.HairColor);
        Assert.Equal((ushort)1, parsed.HairStyle);
        Assert.Equal((uint)0, parsed.Job);
        Assert.Equal((byte)1, parsed.Sex);
    }

    [Fact]
    public void CharacterCreateResponses_Use0b6fAnd006e()
    {
        var success = ClientSession.BuildAcceptMakeCharPacket(new byte[ClientSession.CharacterInfoSize]);
        var failure = ClientSession.BuildRefuseMakeCharPacket(0xff);

        Assert.Equal((short)0x0b6f, BinaryPrimitives.ReadInt16LittleEndian(success));
        Assert.Equal(177, success.Length);
        Assert.Equal((short)0x006e, BinaryPrimitives.ReadInt16LittleEndian(failure));
        Assert.Equal(3, failure.Length);
        Assert.Equal(0xff, failure[2]);
    }

    [Fact]
    public void CharacterCreate_UniqueNameAndFreeSlot_IsAccepted()
    {
        var existing = new[] { new CharCharacter { CharId = 123, Name = "Existing", CharNum = 0 } };

        var failure = ClientSession.DetermineCharacterCreateFailure(
            ClientSession.NameValidationResult.Ok, existing, slot: 1, availableSlots: 9);

        Assert.Null(failure);
        var success = ClientSession.BuildAcceptMakeCharPacket(new byte[ClientSession.CharacterInfoSize]);
        Assert.Equal((short)0x0b6f, BinaryPrimitives.ReadInt16LittleEndian(success));
        Assert.Equal(177, success.Length);
    }

    [Fact]
    public void CharacterCreate_UniqueNameAndFreeSlotEight_IsAccepted()
    {
        var existing = new[] { new CharCharacter { CharId = 1, Name = "Test", CharNum = 0 } };

        var failure = ClientSession.DetermineCharacterCreateFailure(
            ClientSession.NameValidationResult.Ok, existing, slot: 8, availableSlots: 9);

        Assert.Null(failure);
        var success = ClientSession.BuildAcceptMakeCharPacket(new byte[ClientSession.CharacterInfoSize]);
        Assert.Equal((short)0x0b6f, BinaryPrimitives.ReadInt16LittleEndian(success));
        Assert.Equal(177, success.Length);
    }

    [Fact]
    public void CharacterCreate_UniqueNameAndOccupiedSlot_UsesDeniedRatherThanNameTaken()
    {
        var existing = new[] { new CharCharacter { CharId = 123, Name = "Existing", CharNum = 0 } };

        var failure = ClientSession.DetermineCharacterCreateFailure(
            ClientSession.NameValidationResult.Ok, existing, slot: 0, availableSlots: 9);

        Assert.Equal(ClientSession.CharacterCreateFailure.SlotOccupied, failure);
        var reason = ClientSession.GetCharacterCreateFailureWireReason(failure!.Value);
        Assert.Equal(0xff, reason);
        Assert.NotEqual(0x00, reason);
    }

    [Fact]
    public void CharacterCreate_ExistingNameAndFreeSlot_UsesNameTaken()
    {
        var existing = new[] { new CharCharacter { CharId = 123, Name = "Existing", CharNum = 0 } };

        var failure = ClientSession.DetermineCharacterCreateFailure(
            ClientSession.NameValidationResult.Exists, existing, slot: 1, availableSlots: 9);

        Assert.Equal(ClientSession.CharacterCreateFailure.NameTaken, failure);
        Assert.Equal(0x00, ClientSession.GetCharacterCreateFailureWireReason(failure!.Value));
    }

    [Theory]
    [InlineData("Marco")]
    [InlineData("kaas")]
    public void CharacterCreate_UniqueCapturedNames_AreNotDuplicates(string requestedName)
    {
        var existingNames = new[] { "Existing" };

        Assert.False(ClientSession.IsCharacterNameTaken(
            existingNames, requestedName, nameIgnoringCase: false));
    }

    [Fact]
    public void CharacterCreate_NameComparisonHonorsNameIgnoringCaseConfiguration()
    {
        var existingNames = new[] { "Marco" };

        Assert.True(ClientSession.IsCharacterNameTaken(
            existingNames, "marco", nameIgnoringCase: false));
        Assert.False(ClientSession.IsCharacterNameTaken(
            existingNames, "marco", nameIgnoringCase: true));
    }

    [Fact]
    public void CharacterCreate_InvalidSlot_UsesNotEligibleReason()
    {
        Assert.Equal(
            0x03,
            ClientSession.GetCharacterCreateFailureWireReason(
                ClientSession.CharacterCreateFailure.InvalidSlot));
    }

    [Fact]
    public void AccountCheck_Parses0187AccountId()
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x0187);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), 2_000_123);

        Assert.Equal((uint)2_000_123, ClientSession.ParseAccountCheck(packet));

        Assert.Equal(packet, ClientSession.BuildAccountCheckEcho(2_000_123));
    }

    [Fact]
    public void CharacterPages_Use0b72WithCapturedLengths()
    {
        var empty = ClientSession.BuildCharacterPagePacket(ReadOnlySpan<byte>.Empty);
        var oneCharacter = ClientSession.BuildCharacterPagePacket(new byte[ClientSession.CharacterInfoSize]);

        Assert.Equal(new byte[] { 0x72, 0x0b, 0x04, 0x00 }, empty);
        Assert.Equal((short)0x0b72, BinaryPrimitives.ReadInt16LittleEndian(oneCharacter));
        Assert.Equal(179, oneCharacter.Length);
        Assert.Equal((short)179, BinaryPrimitives.ReadInt16LittleEndian(oneCharacter.AsSpan(2, 2)));
        Assert.Equal(175, oneCharacter.AsSpan(4).Length);
    }

    [Fact]
    public void CharacterListSync_ZeroCharacters_SendsOneEmptyResponseThenIgnoresLaterRequests()
    {
        var sync = new IroCharacterListSyncState(Array.Empty<CharCharacter>());

        var firstResponses = sync.HandleRequest();

        var response = Assert.Single(firstResponses);
        Assert.Equal(new byte[] { 0x72, 0x0b, 0x04, 0x00 }, response);
        for (var request = 2; request <= PacketConstants.IroCharSyncCount; request++)
        {
            Assert.Empty(sync.HandleRequest());
        }
        Assert.True(sync.IsComplete);
        Assert.Equal(PacketConstants.IroCharSyncCount, sync.RequestsReceived);
    }

    [Fact]
    public void CharacterListSync_OneCharacter_Sends179ByteResponseWithoutEmptyTerminator()
    {
        var sync = new IroCharacterListSyncState(
            new[] { CreateCharacter(charId: 1, slot: 0, name: "Test") });

        var firstResponses = sync.HandleRequest();

        var response = Assert.Single(firstResponses);
        Assert.Equal(179, response.Length);
        Assert.Equal((short)0x0b72, BinaryPrimitives.ReadInt16LittleEndian(response));
        Assert.Equal((byte)0, response[4 + ClientSession.CharacterInfoSlotOffset]);
        for (var request = 2; request <= PacketConstants.IroCharSyncCount; request++)
        {
            Assert.Empty(sync.HandleRequest());
        }
    }

    [Fact]
    public void CharacterListSync_TwoCharacters_SortsSlotsAndWritesBoth175ByteBlocks()
    {
        var sync = new IroCharacterListSyncState(
            new[]
            {
                CreateCharacter(charId: 2, slot: 1, name: "Kaas"),
                CreateCharacter(charId: 1, slot: 0, name: "Test"),
            });

        var firstResponses = sync.HandleRequest();

        var response = Assert.Single(firstResponses);
        Assert.Equal(4 + (2 * ClientSession.CharacterInfoSize), response.Length);
        Assert.Equal(354, response.Length);
        Assert.Equal((short)354, BinaryPrimitives.ReadInt16LittleEndian(response.AsSpan(2, 2)));

        var firstSlotOffset = 4 + ClientSession.CharacterInfoSlotOffset;
        var secondSlotOffset = 4 + ClientSession.CharacterInfoSize + ClientSession.CharacterInfoSlotOffset;
        Assert.Equal(142, firstSlotOffset);
        Assert.Equal(317, secondSlotOffset);
        Assert.Equal((byte)0, response[firstSlotOffset]);
        Assert.Equal((byte)1, response[secondSlotOffset]);
        for (var request = 2; request <= PacketConstants.IroCharSyncCount; request++)
        {
            Assert.Empty(sync.HandleRequest());
        }
    }

    [Fact]
    public void CharacterListSync_ExactlyThreeCharacters_AppendsImmediateEmptyTerminator()
    {
        var sync = new IroCharacterListSyncState(
            Enumerable.Range(0, 3)
                .Select(index => CreateCharacter((uint)(index + 1), (byte)index, $"Character{index}"))
                .ToArray());

        var firstResponses = sync.HandleRequest();
        var secondResponses = sync.HandleRequest();

        Assert.Equal(2, firstResponses.Count);
        Assert.Equal(529, firstResponses[0].Length);
        Assert.Equal(new byte[] { 0x72, 0x0b, 0x04, 0x00 }, firstResponses[1]);
        Assert.Empty(secondResponses);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void CharacterListSync_MoreThanThreeCharacters_SendsOnlyPopulatedResponse(int characterCount)
    {
        var characters = Enumerable.Range(0, characterCount)
            .Select(index => CreateCharacter((uint)(index + 1), (byte)index, $"Character{index}"))
            .ToArray();
        var sync = new IroCharacterListSyncState(characters);

        var responses = sync.HandleRequest();

        var response = Assert.Single(responses);
        Assert.Equal(4 + (characterCount * ClientSession.CharacterInfoSize), response.Length);
        Assert.Empty(sync.HandleRequest());
    }

    [Fact]
    public void CharacterSelectAndIroZoneHandoff_UseCapturedLayouts()
    {
        var select = new byte[] { 0x66, 0x00, 0x00 };
        Assert.Equal((byte)0, ClientSession.ParseCharacterSelect(select));

        var handoff = ClientSession.BuildIroZoneServerPacket(
            0x04030201, "iz_int01.gat", IPAddress.Parse("128.241.92.42"), 4501);

        Assert.Equal(28, handoff.Length);
        Assert.Equal((short)0x0071, BinaryPrimitives.ReadInt16LittleEndian(handoff));
        Assert.Equal((uint)0x04030201, BinaryPrimitives.ReadUInt32LittleEndian(handoff.AsSpan(2, 4)));
        Assert.Equal("iz_int01.gat", ReadFixedString(handoff.AsSpan(6, 16)));
        Assert.Equal(new byte[] { 128, 241, 92, 42 }, handoff.AsSpan(22, 4).ToArray());
        Assert.Equal((ushort)4501, BinaryPrimitives.ReadUInt16LittleEndian(handoff.AsSpan(26, 2)));
    }

    [Fact]
    public void PageSync_WithIroPinDisabled_ProducesNo08b9Packet()
    {
        var config = new CharConfig
        {
            IroRenewalCompatibility = true,
            PincodeEnabled = false,
            PincodeForce = true,
        };

        var state = ClientSession.DeterminePincodeStartState(config, string.Empty, 0, false, false, 0);
        var outputPackets = state.HasValue
            ? new[] { ClientSession.BuildPincodeStatePacket(state.Value, 1, 2_000_000) }
            : Array.Empty<byte[]>();

        Assert.Null(state);
        Assert.DoesNotContain(outputPackets, packet =>
            BinaryPrimitives.ReadInt16LittleEndian(packet) == 0x08b9);
    }

    [Fact]
    public void PageSync_WithPinEnabled_CanStartNewPinFlow()
    {
        var config = new CharConfig
        {
            IroRenewalCompatibility = true,
            PincodeEnabled = true,
            PincodeForce = true,
        };

        var state = ClientSession.DeterminePincodeStartState(config, string.Empty, 0, false, false, 0);
        Assert.Equal(ClientSession.PincodeState.New, state);

        var packet = ClientSession.BuildPincodeStatePacket(state!.Value, 1, 2_000_000);
        Assert.Equal((short)0x08b9, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10, 2)));
    }

    private static string ReadFixedString(ReadOnlySpan<byte> value)
    {
        var terminator = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(terminator >= 0 ? value[..terminator] : value);
    }

    private static CharCharacter CreateCharacter(uint charId, byte slot, string name)
    {
        return new CharCharacter
        {
            CharId = charId,
            CharNum = slot,
            Name = name,
            LastMap = "prontera",
            Sex = "M",
        };
    }
}
