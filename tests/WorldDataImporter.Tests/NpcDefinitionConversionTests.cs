public sealed class NpcDefinitionConversionTests
{
    [Fact]
    public void WoundedSwordsmanDuplicates_GroupIntoOneDefinitionAndFivePlacements()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/jobs/novice") };
        var filter = new ConversionFilter("academy.txt", null, "Wounded Swordsman#intro_npc02_iz_int", "npc");

        var result = WorldEntityConverter.ConvertNpcDefinitions(roots, filter);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("Wounded Swordsman#intro_npc02_iz_int", definition.TemplateNpcName);
        Assert.Equal(5, result.Placements.Count);
        Assert.All(result.Placements, placement => Assert.Equal(definition.DefinitionId, placement.DefinitionId));

        var byMap = result.Placements.ToDictionary(p => p.Map);
        foreach (var map in new[] { "iz_int", "iz_int01", "iz_int02", "iz_int03", "iz_int04" })
        {
            Assert.True(byMap.ContainsKey(map));
            Assert.Equal((ushort)56, byMap[map].X);
            Assert.Equal((ushort)32, byMap[map].Y);
            Assert.Equal((byte)3, byMap[map].Direction);
            Assert.Equal((ushort)688, byMap[map].Class);
        }
        Assert.Equal(4u, byMap["iz_int"].InitialEffectState);
    }

    // Captain Carocc and Lumin both have real, non-trivial rAthena click dialogue (getexp/switch/select/
    // sc_start commands) - they are NOT OnInit-only NPCs. ConvertNpcDefinitions is a lossless text-slicer
    // that parses whatever click/touch body exists, so it correctly returns non-empty Triggers for both;
    // semantic conversion does not know (and must not encode) that this migration deliberately keeps them
    // unregistered at the emission layer pending real healing/EXP/status/inventory runtime support.
    [Fact]
    public void CaptainCaroccSemanticConversion_ProducesAllFivePinnedPlacementsIncludingTemplate()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/jobs/novice") };
        var filter = new ConversionFilter("academy.txt", null, "Captain Carocc#intro_npc03", "npc");

        var result = WorldEntityConverter.ConvertNpcDefinitions(roots, filter);

        var definition = Assert.Single(result.Definitions);
        Assert.NotEmpty(definition.Triggers);
        Assert.Equal(5, result.Placements.Count);
        var maps = result.Placements.Select(p => p.Map).OrderBy(m => m, StringComparer.Ordinal).ToArray();
        Assert.Equal(["int_land", "int_land01", "int_land02", "int_land03", "int_land04"], maps);
    }

    [Fact]
    public void LuminSemanticConversion_ProducesAllFivePinnedPlacementsIncludingTemplate()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/jobs/novice") };
        var filter = new ConversionFilter("academy.txt", null, "Lumin#new_ship", "npc");

        var result = WorldEntityConverter.ConvertNpcDefinitions(roots, filter);

        var definition = Assert.Single(result.Definitions);
        Assert.NotEmpty(definition.Triggers);
        Assert.Equal(5, result.Placements.Count);
        var maps = result.Placements.Select(p => p.Map).OrderBy(m => m, StringComparer.Ordinal).ToArray();
        Assert.Equal(["int_land", "int_land01", "int_land02", "int_land03", "int_land04"], maps);
    }

    [Fact]
    public void ConvertNpcDefinitions_IsDeterministic()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/jobs/novice") };
        var filter = new ConversionFilter("academy.txt", null, "Wounded Swordsman#intro_npc02_iz_int", "npc");

        var first = WorldEntityConverter.ConvertNpcDefinitions(roots, filter);
        var second = WorldEntityConverter.ConvertNpcDefinitions(roots, filter);

        Assert.Equal(first.Definitions.Select(d => d.DefinitionId), second.Definitions.Select(d => d.DefinitionId));
        Assert.Equal(first.Placements.Select(p => p.PlacementId), second.Placements.Select(p => p.PlacementId));
    }

    [Fact]
    public void ResolutionScope_FindsDuplicateEvenWhenTemplateAndDuplicateLiveInDifferentFiles()
    {
        var repository = FindRepositoryRoot();
        using var fixture = new MultiFileFixture(
            ("template.txt", "map_a,10,20,0\tscript\tGuard#template\t4_TOWER_01,{\n\tmes \"hi\";\n\tclose;\n}\n"),
            ("duplicate.txt", "map_b,11,21,1\tduplicate(Guard#template)\tGuard#dup\t4_TOWER_02\n"));
        var filter = new ConversionFilter(null, null, "Guard#template", "npc");

        var result = WorldEntityConverter.ConvertNpcDefinitions([fixture.Directory, Path.Combine(repository, "legacy/rathena/src")], filter);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(2, result.Placements.Count);
        Assert.Contains(result.Placements, p => p.Map == "map_a");
        Assert.Contains(result.Placements, p => p.Map == "map_b");
        Assert.All(result.Placements, p => Assert.Equal(definition.DefinitionId, p.DefinitionId));
    }

    [Fact]
    public void NonDuplicatedNpc_StillProducesOneDefinitionAndOnePlacement()
    {
        var repository = FindRepositoryRoot();
        using var fixture = new MultiFileFixture(
            ("source.txt", "map_a,10,20,0\tscript\tLone Npc\t4_TOWER_01,{\n\tmes \"hi\";\n\tclose;\n}\n"));
        var filter = new ConversionFilter(null, null, "Lone Npc", "npc");

        var result = WorldEntityConverter.ConvertNpcDefinitions([fixture.Directory, Path.Combine(repository, "legacy/rathena/src")], filter);

        Assert.Single(result.Definitions);
        Assert.Single(result.Placements);
    }

    [Fact]
    public void ActorOnlyTemplate_ProducesDefinitionWithNoTriggers()
    {
        var repository = FindRepositoryRoot();
        using var fixture = new MultiFileFixture(
            ("source.txt", "map_a,10,20,0\tscript\tSilent Npc\t4_TOWER_01,{\nOnInit:\n\tend;\n}\n"));
        var filter = new ConversionFilter(null, null, "Silent Npc", "npc");

        var result = WorldEntityConverter.ConvertNpcDefinitions([fixture.Directory, Path.Combine(repository, "legacy/rathena/src")], filter);

        var definition = Assert.Single(result.Definitions);
        Assert.Empty(definition.Triggers);
    }

    [Fact]
    public void DefinitionId_IsCanonicalizedAcrossPathSeparators()
    {
        Assert.Equal(
            DeterministicId.ForDefinition("legacy/rathena/npc/re/jobs/novice/academy.txt", "Wounded Swordsman#x"),
            DeterministicId.ForDefinition(@"C:\repo\legacy\rathena\npc\re\jobs\novice\academy.txt", "Wounded Swordsman#x"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    private sealed class MultiFileFixture : IDisposable
    {
        public MultiFileFixture(params (string FileName, string Content)[] files)
        {
            Directory = Path.Combine(Path.GetTempPath(), $"athena-npc-conversion-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var (fileName, content) in files) File.WriteAllText(Path.Combine(Directory, fileName), content);
        }
        public string Directory { get; }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
