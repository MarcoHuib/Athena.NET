using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class CharacterPositionPersistenceTests
{
    [Fact]
    public void ExistingCharacter_UsesPersistedLastPositionInsteadOfStartOrSavePoint()
    {
        var character = new CharCharacter
        {
            LastMap = "iz_int03",
            LastX = 55,
            LastY = 22,
            SaveMap = "iz_int",
            SaveX = 18,
            SaveY = 26,
        };

        var location = ClientSession.ResolveCharacterLocation(character);

        Assert.Equal(("iz_int03", (ushort)55, (ushort)22), location);
    }

    [Fact]
    public void CharacterWithoutLastPosition_FallsBackToSavePointCoordinates()
    {
        var character = new CharCharacter
        {
            SaveMap = "iz_int02",
            SaveX = 18,
            SaveY = 26,
        };

        Assert.Equal(
            ("iz_int02", (ushort)18, (ushort)26),
            ClientSession.ResolveCharacterLocation(character));
    }

    [Theory]
    [InlineData(false, 10, 20, false)]
    [InlineData(true, 11, 20, false)]
    [InlineData(true, 10, 21, false)]
    [InlineData(true, 10, 20, true)]
    public void SaveAuthorization_RequiresAuthenticatedOwningMapServerSession(
        bool authenticated,
        uint accountId,
        uint charId,
        bool expected)
    {
        IReadOnlySet<(uint AccountId, uint CharId)> owned =
            new HashSet<(uint AccountId, uint CharId)> { (10, 20) };

        Assert.Equal(
            expected,
            MapServerSession.IsPositionSaveAuthorized(authenticated, owned, accountId, charId));
    }

    [Theory]
    [InlineData(false, 10, 20, 21001, 0, false)]
    [InlineData(true, 11, 20, 21001, 0, false)]
    [InlineData(true, 10, 21, 21001, 0, false)]
    [InlineData(true, 10, 20, 0, 0, false)]
    [InlineData(true, 10, 20, 21001, 3, false)]
    [InlineData(true, 10, 20, 21001, 0, true)]
    [InlineData(true, 10, 20, 21001, 2, true)]
    public void QuestAuthorization_RequiresAuthenticatedOwningMapServerSessionAndValidRequest(
        bool authenticated,
        uint accountId,
        uint charId,
        uint questId,
        byte operation,
        bool expected)
    {
        IReadOnlySet<(uint AccountId, uint CharId)> owned =
            new HashSet<(uint AccountId, uint CharId)> { (10, 20) };

        Assert.Equal(
            expected,
            MapServerSession.IsQuestStateRequestAuthorized(
                authenticated, owned, accountId, charId, questId, operation));
    }

    [Theory]
    [InlineData(false, 10, 20, 6008, 1u, false)]
    [InlineData(true, 11, 20, 6008, 1u, false)]
    [InlineData(true, 10, 21, 6008, 1u, false)]
    [InlineData(true, 10, 20, 0, 1u, false)]
    [InlineData(true, 10, 20, 6008, 0u, false)]
    [InlineData(true, 10, 20, 6008, 1u, true)]
    public void InventoryAddAuthorization_RequiresAuthenticatedOwningMapServerSessionAndValidRequest(
        bool authenticated,
        uint accountId,
        uint charId,
        int itemId,
        uint amount,
        bool expected)
    {
        IReadOnlySet<(uint AccountId, uint CharId)> owned =
            new HashSet<(uint AccountId, uint CharId)> { (10, 20) };

        Assert.Equal(
            expected,
            MapServerSession.IsInventoryAddRequestAuthorized(
                authenticated, owned, accountId, charId, itemId, amount));
    }
}
