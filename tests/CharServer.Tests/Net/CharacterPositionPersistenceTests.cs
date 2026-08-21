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
}
