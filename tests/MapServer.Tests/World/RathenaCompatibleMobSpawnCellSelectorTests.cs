using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class RathenaCompatibleMobSpawnCellSelectorTests
{
    private static MobDefinition MakeMob() => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static MobSpawnDefinition MapWideSpawn(string map = "test_map", int count = 1) =>
        new(MakeMob(), map, count, 5000, new("rAthena", "abc", "x.txt", 1)); // X=Y=Xs=Ys default 0.

    private static MobSpawnDefinition RectangularSpawn() =>
        new(MakeMob(), "test_map", 1, 5000, new("rAthena", "abc", "x.txt", 1), X: 150, Y: 180, Xs: 10, Ys: 12);

    // All-walkable square map with the given side length, so every cell inside pinned traversal
    // bounds (0 <= x < side-1, 0 <= y < side-1) is a valid candidate.
    private static MapCollisionMap MakeAllWalkableMap(string name, int side) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    private static MapCollisionMap MakeMapWithBlockedCell(string name, int side, int blockedX, int blockedY)
    {
        var cells = Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray();
        cells[blockedX + blockedY * side] = MapCellFlags.None;
        return new MapCollisionMap(name, side, side, cells);
    }

    private static Func<int, int, int> SequentialRandom(params int[] values)
    {
        var index = 0;
        return (_, _) => values[index++ % values.Length];
    }

    [Fact]
    public void TrySelectCell_MapWideDeclaration_ReturnsWalkableCellWithinTraversalBounds()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, max) => min);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.True(position.X >= 0 && position.X < map.Width - 1);
        Assert.True(position.Y >= 0 && position.Y < map.Height - 1);
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }

    [Fact]
    public void TrySelectCell_NeverReturnsABlockedCell()
    {
        // Deterministic RNG sequence: first candidate lands exactly on the blocked cell, forcing
        // the selector to retry and prove it does not just accept the first candidate blindly.
        var map = MakeMapWithBlockedCell("test_map", 100, blockedX: 50, blockedY: 50);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(50, 51); // (x,y) pairs consumed as (low,high) calls alternate.
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.NotEqual((50, 50), (position.X, position.Y));
        Assert.True(map.IsWalkable(position.X, position.Y));
    }

    [Fact]
    public void TrySelectCell_NeverReturnsTheFinalRowOrColumn_EvenThoughRawArtifactBoundsIncludeThem()
    {
        // A 100x100 all-walkable map's raw artifact bounds include x/y == 99, but pinned
        // traversal semantics exclude the final row/column (map_getcellp, map.cpp:3329-3331) - the
        // random range itself (edge..width-edge-1) already keeps candidates well inside that
        // boundary for any reasonably sized map, so this proves the selector's candidate range is
        // never wide enough to reach the excluded edge even by chance.
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(15, 84, 15, 84); // Widest legal range: [edge, width-edge-1].
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var position));

        Assert.True(position.X < map.Width - 1);
        Assert.True(position.Y < map.Height - 1);
    }

    [Fact]
    public void TrySelectCell_HighestLegalCoordinate_CanBeSelected()
    {
        // rAthena's rnd_value(min,max) is INCLUSIVE of both endpoints (map.cpp:1835-1836); this
        // proves the selector's own translation preserves that - the highest legal candidate
        // (width - MapEdgeSize - 1) must be reachable, not silently excluded by an off-by-one when
        // adapting to .NET's exclusive-upper-bound Random.Next shape.
        const int side = 100;
        var highestLegalX = side - RathenaCompatibleMobSpawnCellSelector.MapEdgeSize - 1;
        var highestLegalY = side - RathenaCompatibleMobSpawnCellSelector.MapEdgeSize - 1;
        var map = MakeAllWalkableMap("test_map", side);
        var provider = new MapCollisionProvider([map]);
        // The random source always returns the caller-supplied `max` (the inclusive upper bound
        // itself) - if the selector ever passed an EXCLUSIVE bound one-too-high, or clamped away
        // from the true maximum, this would either throw (out of range for a real Random.Next) or
        // never select the true edge value. Here it simply proves the boundary value is what gets
        // requested and accepted.
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, max) => max);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)highestLegalX, position.X);
        Assert.Equal((ushort)highestLegalY, position.Y);
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }

    [Fact]
    public void TrySelectCell_LowestLegalCoordinate_CanBeSelected()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.True(success);
        Assert.Equal((ushort)RathenaCompatibleMobSpawnCellSelector.MapEdgeSize, position.X);
        Assert.Equal((ushort)RathenaCompatibleMobSpawnCellSelector.MapEdgeSize, position.Y);
    }

    [Fact]
    public void TrySelectCell_IsNotTheOldDeterministicFallbackRow()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min + 5);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var position));

        // The old UnverifiedFallbackMobSpawnCellSelector places instance 0 at exactly (50,50).
        Assert.NotEqual((ushort)50, position.X);
    }

    [Fact]
    public void TrySelectCell_RepeatedSelections_CanProduceDifferentValidCellsUnderAFakeRngSequence()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(20, 20, 40, 40, 60, 60);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var first));
        Assert.True(selector.TrySelectCell(MapWideSpawn(), 1, out var second));
        Assert.True(selector.TrySelectCell(MapWideSpawn(), 2, out var third));

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void TrySelectCell_MultipleInstances_UseIndependentSelections()
    {
        var map = MakeAllWalkableMap("test_map", 100);
        var provider = new MapCollisionProvider([map]);
        var random = SequentialRandom(10, 10, 90, 90); // Two distinct (x,y) pairs.
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, random);
        var spawn = MapWideSpawn(count: 2);

        Assert.True(selector.TrySelectCell(spawn, 0, out var first));
        Assert.True(selector.TrySelectCell(spawn, 1, out var second));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TrySelectCell_MissingMap_ThrowsRatherThanFallingBack()
    {
        // A missing map is a hard world/configuration error once collision-backed spawning is
        // active for this world - it must NEVER be silently recovered via
        // UnverifiedFallbackMobSpawnCellSelector's fabricated coordinates, which would make a
        // broken/incomplete collision-backed world look like it placed the monster correctly.
        var provider = new MapCollisionProvider([]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min);

        var exception = Assert.Throws<InvalidOperationException>(
            () => selector.TrySelectCell(MapWideSpawn("nonexistent_map"), 0, out _));
        Assert.Contains("nonexistent_map", exception.Message);
    }

    [Fact]
    public void TrySelectCell_MapTooSmallForEdgeMargin_ThrowsRatherThanFallingBack()
    {
        // Side 20 with MapEdgeSize=15 leaves low=15, high=20-15-1=4: low > high, an impossible
        // range under pinned semantics too - a DATA problem with this specific map, not something
        // more attempts or a fallback placeholder can paper over.
        var map = MakeAllWalkableMap("tiny_map", 20);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min);

        var exception = Assert.Throws<InvalidOperationException>(
            () => selector.TrySelectCell(MapWideSpawn("tiny_map"), 0, out _));
        Assert.Contains("tiny_map", exception.Message);
    }

    [Fact]
    public void TrySelectCell_AllCandidatesBlocked_ReturnsFalse_NotAPlaceholderPosition()
    {
        // Every candidate the fixed RNG can produce (always (50,50)) is blocked - the attempt
        // budget exhausts and the selector must report a genuine temporary failure (false),
        // never silently falling back to (0,0) or any other placeholder coordinate.
        var map = MakeMapWithBlockedCell("test_map", 100, blockedX: 50, blockedY: 50);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (_, _) => 50);

        var success = selector.TrySelectCell(MapWideSpawn(), 0, out var position);

        Assert.False(success);
        Assert.Equal(default, position);
    }

    [Fact]
    public void TrySelectCell_RectangularDeclaration_ThrowsRatherThanSilentlyFallingBack()
    {
        // Real collision-backed spawning is active (a genuine map is configured) but the
        // declaration's geometry (X/Y/Xs/Ys not all zero) is not yet implemented - this must
        // surface loudly as an unsupported feature, never silently place the monster via
        // UnverifiedFallbackMobSpawnCellSelector's fabricated coordinates as if nothing were wrong.
        var map = MakeAllWalkableMap("test_map", 500);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider, (min, _) => min);

        Assert.Throws<NotSupportedException>(() => selector.TrySelectCell(RectangularSpawn(), 0, out _));
    }

    [Fact]
    public void ProductionConstructor_ProducesAWalkableCell()
    {
        // Exercises the real System.Random.Shared-backed constructor (no injected RNG) to prove
        // the production path is wired correctly end-to-end, not just the test-injectable overload.
        var map = MakeAllWalkableMap("test_map", 200);
        var provider = new MapCollisionProvider([map]);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);

        Assert.True(selector.TrySelectCell(MapWideSpawn(), 0, out var position));
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }
}
