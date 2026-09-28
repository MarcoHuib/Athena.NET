using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

// Map-routing provenance at the CharServer layer. Pinned rAthena's `start_point` is a list of PARALLEL
// intro instances (iz_int, iz_int01..04) from which a new character's start map is picked at RANDOM at
// creation (conf/templates/char_athena.conf: "Location is randomly picked on character creation").
// Each instance leads through int_land0N -> izlude_{a..d} -> prt_fild08{a..d} (base: int_land -> izlude
// -> prt_fild08), so two characters legitimately end up on different canonical maps. These tests pin
// that contract (and the diagnostic provenance text) without changing any selection behavior.
public sealed class CharacterMapRoutingTests
{
    private static readonly StartPoint[] IntroVariants =
    [
        new("iz_int", 18, 26), new("iz_int01", 18, 26), new("iz_int02", 18, 26), new("iz_int03", 18, 26), new("iz_int04", 18, 26),
    ];

    private static CharConfig Config(bool pre = false, StartPoint[]? points = null, StartPoint[]? preOnly = null, StartPoint[]? doram = null) => new()
    {
        StartPoints = points ?? IntroVariants,
        StartPointsPre = preOnly ?? [new("new_1-1", 53, 111)],
        StartPointsDoram = doram ?? [new("lasa_fild01", 48, 297)],
        UsePreRenewalStartPoints = pre,
    };

    [Fact]
    public void StartPoint_IsAlwaysOneOfTheConfiguredParallelIntroVariants_AndEveryVariantIsReachable()
    {
        var config = Config();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 400; i++)
        {
            var point = ClientSession.SelectStartPoint(config, job: 0, out var provenance);
            Assert.Contains(IntroVariants, candidate => candidate.Map == point.Map && candidate.X == point.X && candidate.Y == point.Y);
            Assert.StartsWith("start_point[", provenance);
            Assert.Contains("/5]", provenance);
            Assert.Contains("iz_int03", provenance); // The candidate list is shown, so a log reader sees the alternatives.
            seen.Add(point.Map);
        }

        // With 5 equally likely variants, 400 independent draws miss one variant with probability
        // ~5 * (4/5)^400 (astronomically small) - so all five parallel instances are genuinely reachable.
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void StartPoint_PreRenewalPoolIsUsedOnlyWhenEnabled_AndDoramHasItsOwnPool()
    {
        var pre = ClientSession.SelectStartPoint(Config(pre: true), job: 0, out var preProvenance);
        Assert.Equal("new_1-1", pre.Map);
        Assert.StartsWith("start_point_pre[", preProvenance);

        var doram = ClientSession.SelectStartPoint(Config(), job: 4218, out var doramProvenance); // Summoner.
        Assert.Equal("lasa_fild01", doram.Map);
        Assert.StartsWith("start_point_doram[", doramProvenance);
    }

    [Fact]
    public void StartPoint_WithNothingConfigured_UsesBuiltInFallback_AndSaysSo()
    {
        var point = ClientSession.SelectStartPoint(new CharConfig(), job: 0, out var provenance);

        Assert.Equal("iz_int", point.Map);
        Assert.Contains("built-in-fallback", provenance);
    }

    // The map a returning character is routed to is whatever was PERSISTED (never re-derived from a
    // start point or normalized to a canonical variant): a character stored on prt_fild08c stays on
    // prt_fild08c, one stored on the base prt_fild08 stays on the base map.
    [Theory]
    [InlineData("prt_fild08c", 257, 204)]
    [InlineData("prt_fild08", 259, 197)]
    public void PersistedLastMap_IsRoutedUnchanged_NeverNormalizedToAnotherVariant(string map, int x, int y)
    {
        var character = new CharCharacter { LastMap = map, LastX = (ushort)x, LastY = (ushort)y, SaveMap = "izlude_a", SaveX = 128, SaveY = 142 };

        Assert.Equal((map, (ushort)x, (ushort)y), ClientSession.ResolveCharacterLocation(character));
        Assert.Equal("LastMap", ClientSession.DescribeCharacterLocationSource(character));
    }

    [Fact]
    public void LocationSource_IdentifiesSaveMapAndBuiltInFallback()
    {
        Assert.Equal("SaveMap", ClientSession.DescribeCharacterLocationSource(new CharCharacter { SaveMap = "iz_int02" }));
        Assert.Equal("built-in-fallback(prontera)", ClientSession.DescribeCharacterLocationSource(new CharCharacter()));
        Assert.Equal(("prontera", (ushort)0, (ushort)0), ClientSession.ResolveCharacterLocation(new CharCharacter()));
    }
}
