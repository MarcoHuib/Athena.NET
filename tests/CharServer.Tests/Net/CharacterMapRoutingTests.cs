using Athena.Net.CharServer.Config;
using Athena.Net.CharServer.Db.Entities;
using Athena.Net.CharServer.Net;
using Athena.Net.Shared.MapIdentity;

namespace Athena.Net.CharServer.Tests.Net;

// Map routing at the CharServer layer. Athena.NET runs ONE shared world: pinned rAthena's parallel
// "channel" copies (iz_int01..04, int_land01..04, izlude_a..d, prt_fild08a..d, iz_ac01_a..d, iz_ac02_a..d)
// are folded onto their canonical maps by CanonicalMapPolicy. Renewal character creation therefore only
// ever starts on iz_int, and a character persisted on any alias loads on the canonical map at the same
// coordinates (no manual DB edit). Persistence only ever writes canonical names.
public sealed class CharacterMapRoutingTests
{
    // The legacy `start_point` list older confs still carry: it must never produce a channel copy.
    private static readonly StartPoint[] LegacyIntroVariants =
    [
        new("iz_int", 18, 26), new("iz_int01", 18, 26), new("iz_int02", 18, 26), new("iz_int03", 18, 26), new("iz_int04", 18, 26),
    ];

    private static CharConfig Config(bool pre = false, StartPoint[]? points = null, StartPoint[]? preOnly = null, StartPoint[]? doram = null) => new()
    {
        StartPoints = points ?? [new("iz_int", 18, 26)],
        StartPointsPre = preOnly ?? [new("new_1-1", 53, 111)],
        StartPointsDoram = doram ?? [new("lasa_fild01", 48, 297)],
        UsePreRenewalStartPoints = pre,
    };

    private static readonly (string Alias, string Canonical)[] ExpectedAliasTable =
    [
        ("izlude_a", "izlude"), ("izlude_b", "izlude"), ("izlude_c", "izlude"), ("izlude_d", "izlude"),
        ("prt_fild08a", "prt_fild08"), ("prt_fild08b", "prt_fild08"), ("prt_fild08c", "prt_fild08"), ("prt_fild08d", "prt_fild08"),
        ("iz_int01", "iz_int"), ("iz_int02", "iz_int"), ("iz_int03", "iz_int"), ("iz_int04", "iz_int"),
        ("int_land01", "int_land"), ("int_land02", "int_land"), ("int_land03", "int_land"), ("int_land04", "int_land"),
        ("iz_ac01_a", "iz_ac01"), ("iz_ac01_b", "iz_ac01"), ("iz_ac01_c", "iz_ac01"), ("iz_ac01_d", "iz_ac01"),
        ("iz_ac02_a", "iz_ac02"), ("iz_ac02_b", "iz_ac02"), ("iz_ac02_c", "iz_ac02"), ("iz_ac02_d", "iz_ac02"),
    ];

    public static TheoryData<string, string> RepresentativeAliases => new()
    {
        { "iz_int03", "iz_int" }, { "int_land04", "int_land" }, { "izlude_c", "izlude" },
        { "prt_fild08c", "prt_fild08" }, { "iz_ac01_b", "iz_ac01" }, { "iz_ac02_d", "iz_ac02" },
    };

    [Fact]
    public void AliasTable_IsExactlyTheTwentyFourExplicitAliases_ToSixCanonicalMaps()
    {
        Assert.Equal(24, CanonicalMapPolicy.AliasTable.Count);
        Assert.Equal(ExpectedAliasTable.OrderBy(e => e.Alias, StringComparer.Ordinal), CanonicalMapPolicy.AliasTable.Select(kv => (kv.Key, kv.Value)).OrderBy(e => e.Key, StringComparer.Ordinal));
        Assert.Equal(new[] { "int_land", "iz_ac01", "iz_ac02", "iz_int", "izlude", "prt_fild08" }, CanonicalMapPolicy.CanonicalMaps.Order(StringComparer.Ordinal));
        foreach (var (alias, canonical) in ExpectedAliasTable) Assert.Equal(canonical, CanonicalMapPolicy.Canonicalize(alias));
        foreach (var canonical in CanonicalMapPolicy.CanonicalMaps) Assert.Equal(canonical, CanonicalMapPolicy.Canonicalize(canonical));
    }

    [Theory]
    [InlineData("prontera")]
    [InlineData("prt_fild07")]
    [InlineData("izlude_in")]
    [InlineData("new_1-1")]
    [InlineData("new_5-1")]
    [InlineData("1@tower")]
    [InlineData("pvp_n_1-1")]
    [InlineData("iz_int05")]
    [InlineData("izlude_e")]
    [InlineData("prt_fild08e")]
    [InlineData("int_land00")]
    [InlineData("iz_ac01")]
    [InlineData("iz_ac01_e")]
    [InlineData("lasa_fild01")]
    public void UnrelatedMaps_AreNeverTouched_AndNoSuffixStrippingIsInferred(string map) =>
        Assert.Equal(map, CanonicalMapPolicy.Canonicalize(map));

    // ---- Character creation ------------------------------------------------------------------

    [Fact]
    public void RenewalCreation_UsesOnlyIzInt_EveryTime()
    {
        var config = Config();
        for (var i = 0; i < 200; i++)
        {
            var point = ClientSession.SelectStartPoint(config, job: 0, out var provenance);
            Assert.Equal(("iz_int", (ushort)18, (ushort)26), (point.Map, point.X, point.Y));
            Assert.StartsWith("start_point[1/1]", provenance);
        }
    }

    [Fact]
    public void RenewalCreation_WithALegacyFiveVariantConf_StillNeverPicksAChannelCopy()
    {
        var config = Config(points: LegacyIntroVariants);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 400; i++)
        {
            var point = ClientSession.SelectStartPoint(config, job: 0, out var provenance);
            seen.Add(point.Map);
            Assert.Equal(((ushort)18, (ushort)26), (point.X, point.Y));
            Assert.StartsWith("start_point[", provenance);
        }
        Assert.Equal(new[] { "iz_int" }, seen);
    }

    [Fact]
    public void ShippedConfigTemplate_ListsOnlyIzIntForRenewalStart()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        var template = File.ReadAllLines(Path.Combine(directory!.FullName, "conf", "templates", "char_athena.conf"));
        var line = Assert.Single(template, l => l.StartsWith("start_point:", StringComparison.Ordinal));
        Assert.Equal("start_point: iz_int,18,26", line);
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

    // ---- Persisted LastMap / SaveMap ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(RepresentativeAliases))]
    public void PersistedLastMapAlias_LoadsOnTheCanonicalMap_AtTheSameCoordinates(string alias, string canonical)
    {
        var character = new CharCharacter { LastMap = alias, LastX = 257, LastY = 204, SaveMap = "prontera", SaveX = 156, SaveY = 191 };

        Assert.Equal((canonical, (ushort)257, (ushort)204), ClientSession.ResolveCharacterLocation(character, out var persisted));
        Assert.Equal(alias, persisted);
        Assert.Equal("LastMap", ClientSession.DescribeCharacterLocationSource(character));
    }

    [Theory]
    [MemberData(nameof(RepresentativeAliases))]
    public void PersistedSaveMapAlias_LoadsOnTheCanonicalMap_WhenLastMapIsEmpty(string alias, string canonical)
    {
        var character = new CharCharacter { SaveMap = alias, SaveX = 128, SaveY = 142 };

        Assert.Equal((canonical, (ushort)128, (ushort)142), ClientSession.ResolveCharacterLocation(character, out var persisted));
        Assert.Equal(alias, persisted);
        Assert.Equal("SaveMap", ClientSession.DescribeCharacterLocationSource(character));
    }

    [Fact]
    public void PersistedCanonicalMap_IsRoutedUnchanged()
    {
        var character = new CharCharacter { LastMap = "prt_fild08", LastX = 259, LastY = 197 };

        Assert.Equal(("prt_fild08", (ushort)259, (ushort)197), ClientSession.ResolveCharacterLocation(character, out var persisted));
        Assert.Equal("prt_fild08", persisted);
    }

    [Fact]
    public void MapAuthNode_NeverCarriesAnAliasMapName()
    {
        foreach (var (alias, canonical) in ExpectedAliasTable)
            Assert.Equal(canonical, new MapAuthNode(1, 2, 3, 4, 0, alias, 10, 20, 0, 0, 0, 0, false).MapName);
        Assert.Equal("prontera", new MapAuthNode(1, 2, 3, 4, 0, "prontera", 10, 20, 0, 0, 0, 0, false).MapName);
    }

    [Fact]
    public void CharacterSelectRoutingLog_MakesCanonicalizationObservable()
    {
        var character = new CharCharacter { CharId = 7, LastMap = "prt_fild08c", LastX = 257, LastY = 204 };
        var location = ClientSession.ResolveCharacterLocation(character, out var persisted);

        var line = ClientSession.FormatCharacterSelectRouting(character, accountId: 3, location, persisted);

        Assert.Contains("[MAP ROUTING]", line);
        Assert.Contains("fromMap='prt_fild08c' toMap='prt_fild08'", line);
        Assert.Contains("reason=character-load-canonicalized", line);
        Assert.Contains("position=(257,204)", line);
    }

    [Fact]
    public void CharacterSelectRoutingLog_KeepsOrdinaryReasonWhenNothingWasCanonicalized()
    {
        var character = new CharCharacter { CharId = 7, LastMap = "prt_fild08", LastX = 259, LastY = 197 };
        var location = ClientSession.ResolveCharacterLocation(character, out var persisted);

        var line = ClientSession.FormatCharacterSelectRouting(character, accountId: 3, location, persisted);

        Assert.Contains("fromMap='prt_fild08' toMap='prt_fild08'", line);
        Assert.Contains("reason=character-select", line);
        Assert.DoesNotContain("canonicalized", line);
    }

    // ---- Persistence writes ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(RepresentativeAliases))]
    public void PositionPersistence_WritesOnlyCanonicalMapNames(string alias, string canonical)
    {
        var character = new CharCharacter { LastMap = "prontera", LastX = 1, LastY = 2 };

        MapServerSession.ApplyLastPosition(character, alias, 257, 204);

        Assert.Equal((canonical, (ushort)257, (ushort)204), (character.LastMap, character.LastX, character.LastY));
    }

    [Theory]
    [MemberData(nameof(RepresentativeAliases))]
    public void SavePointPersistence_WritesOnlyCanonicalMapNames(string alias, string canonical)
    {
        var character = new CharCharacter { SaveMap = "prontera", SaveX = 1, SaveY = 2 };

        MapServerSession.ApplySavePoint(character, alias, 128, 142);

        Assert.Equal((canonical, (ushort)128, (ushort)142), (character.SaveMap, character.SaveX, character.SaveY));
    }

    [Fact]
    public void Persistence_LeavesUnrelatedMapsUntouched()
    {
        var character = new CharCharacter();
        MapServerSession.ApplyLastPosition(character, "prt_fild07", 10, 20);
        MapServerSession.ApplySavePoint(character, "prontera", 156, 191);
        Assert.Equal("prt_fild07", character.LastMap);
        Assert.Equal("prontera", character.SaveMap);
    }

    [Fact]
    public void LocationSource_IdentifiesSaveMapAndBuiltInFallback()
    {
        Assert.Equal("SaveMap", ClientSession.DescribeCharacterLocationSource(new CharCharacter { SaveMap = "iz_int02" }));
        Assert.Equal("built-in-fallback(prontera)", ClientSession.DescribeCharacterLocationSource(new CharCharacter()));
        Assert.Equal(("prontera", (ushort)0, (ushort)0), ClientSession.ResolveCharacterLocation(new CharCharacter()));
    }
}
