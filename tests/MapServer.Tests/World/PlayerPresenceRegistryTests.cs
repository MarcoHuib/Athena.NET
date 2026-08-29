using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class PlayerPresenceRegistryTests
{
    [Theory]
    [InlineData(156, 34, 159, 48, true)]
    [InlineData(156, 34, 143, 43, true)]
    [InlineData(156, 34, 170, 34, true)]
    [InlineData(156, 34, 171, 34, false)]
    public void IsVisible_UsesCaptureBackedSquareBoundary(ushort x1, ushort y1, ushort x2, ushort y2, bool expected)
    {
        Assert.Equal(expected, WorldVisibilityOptions.Default.IsVisible("prontera", x1, y1, "PRONTERA", x2, y2));
        Assert.False(WorldVisibilityOptions.Default.IsVisible("prontera", x1, y1, "izlude", x2, y2));
    }

    [Fact]
    public void Registry_RegisterMoveMapLookupAndUnregister_AreConsistent()
    {
        var registry = new PlayerPresenceRegistry();
        var original = Make(10, 100, "prontera", 15, 15);
        Assert.True(registry.TryRegister(original));
        Assert.False(registry.TryRegister(original));
        Assert.Equal(original, AssertPresence(registry, 10, 100));

        var sameBucket = original with { X = 20, Y = 20 };
        Assert.True(registry.TryReplace(sameBucket, out var previous));
        Assert.Equal(original, previous);
        Assert.Single(registry.QueryNearby("prontera", 20, 20));

        var otherMap = sameBucket with { MapName = "izlude", X = 200, Y = 200 };
        Assert.True(registry.TryReplace(otherMap, out _));
        Assert.Empty(registry.QueryNearby("prontera", 20, 20));
        Assert.Single(registry.QueryNearby("izlude", 200, 200));

        Assert.True(registry.TryUnregister(10, out var removed));
        Assert.Equal(otherMap, removed);
        Assert.False(registry.TryGetByActorId(10, out _));
        Assert.Empty(registry.QueryNearby("izlude", 200, 200));
    }

    [Fact]
    public void LocalQuery_UsesOnlyNearbyMapBuckets_NotGlobalPopulation()
    {
        var registry = new PlayerPresenceRegistry(new WorldVisibilityOptions(14, 16));
        for (uint i = 1; i <= 1_000; i++)
        {
            var map = i % 2 == 0 ? "prontera" : "izlude";
            Assert.True(registry.TryRegister(Make(i, 10_000 + i, map, (ushort)(i * 29 % 900), (ushort)(i * 47 % 900))));
        }
        Assert.True(registry.TryRegister(Make(5_001, 20_001, "prontera", 100, 100)));

        var candidates = registry.QueryCandidateActorIds("prontera", 100, 100);
        Assert.Contains(5_001u, candidates);
        Assert.True(candidates.Count < 100, $"Local bucket query returned {candidates.Count} of {registry.Count} global players.");
        Assert.All(registry.QueryNearby("prontera", 100, 100), player =>
            Assert.True(WorldVisibilityOptions.Default.IsVisible("prontera", 100, 100, player.MapName, player.X, player.Y)));
    }

    [Fact]
    public async Task ConcurrentRegisterMoveUnregister_DoesNotDuplicateOrCorruptIndex()
    {
        var registry = new PlayerPresenceRegistry();
        var registrations = Enumerable.Range(1, 250).Select(async value =>
        {
            var id = (uint)value;
            var presence = Make(id, id + 10_000, "prontera", (ushort)(value % 100), (ushort)(value / 3));
            Assert.True(registry.TryRegister(presence));
            Assert.True(registry.TryReplace(presence with { X = (ushort)(presence.X + 1) }, out _));
            if (value % 2 == 0) Assert.True(registry.TryUnregister(id, out _));
            await Task.Yield();
        });
        await Task.WhenAll(registrations);
        Assert.Equal(125, registry.Count);
        var nearby = registry.QueryNearby("prontera", 50, 50);
        Assert.Equal(nearby.Count, nearby.Select(p => p.ActorId).Distinct().Count());
    }

    private static PlayerPresence AssertPresence(PlayerPresenceRegistry registry, uint actorId, uint charId)
    {
        Assert.True(registry.TryGetByActorId(actorId, out var byActor));
        Assert.True(registry.TryGetByCharacterId(charId, out var byCharacter));
        Assert.Same(byActor, byCharacter);
        return byActor;
    }

    internal static PlayerPresence Make(uint actorId, uint charId, string map, ushort x, ushort y, string? name = null) =>
        new(actorId, charId, name ?? $"P{actorId}", map, x, y, 0, 0, null, 0, 1, 1, 150,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
