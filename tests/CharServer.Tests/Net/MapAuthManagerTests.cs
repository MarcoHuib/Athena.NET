using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapAuthManagerTests
{
    private static readonly MapAuthNode Node = new(
        100,
        200,
        300,
        400,
        1,
        "iz_int01.gat",
        18,
        27,
        0,
        0,
        0,
        0,
        false);

    [Fact]
    public void TryConsume_MatchingIroFieldsSucceedsAndConsumesNode()
    {
        var manager = CreateManager();

        Assert.True(manager.TryConsume(Node.AccountId, Node.CharId, Node.LoginId1, null, out var consumed));
        Assert.Equal(Node, consumed);
        Assert.False(manager.TryGet(Node.AccountId, out _));
        Assert.False(manager.TryConsume(Node.AccountId, Node.CharId, Node.LoginId1, null, out _));
    }

    [Theory]
    [InlineData(101u, 200u, 300u)]
    [InlineData(100u, 201u, 300u)]
    [InlineData(100u, 200u, 301u)]
    public void TryConsume_WrongProvenFieldFailsWithoutConsuming(uint accountId, uint charId, uint loginId1)
    {
        var manager = CreateManager();

        Assert.False(manager.TryConsume(accountId, charId, loginId1, null, out _));
        Assert.True(manager.TryGet(Node.AccountId, out _));
    }

    [Fact]
    public void TryConsume_MissingNodeFails()
    {
        var manager = new MapAuthManager();

        Assert.False(manager.TryConsume(Node.AccountId, Node.CharId, Node.LoginId1, null, out _));
    }

    [Fact]
    public void TryConsume_LegacySexMismatchFailsWithoutConsuming()
    {
        var manager = CreateManager();

        Assert.False(manager.TryConsume(Node.AccountId, Node.CharId, Node.LoginId1, 0, out _));
        Assert.True(manager.TryGet(Node.AccountId, out _));
    }

    private static MapAuthManager CreateManager()
    {
        var manager = new MapAuthManager();
        manager.Add(Node);
        return manager;
    }
}
