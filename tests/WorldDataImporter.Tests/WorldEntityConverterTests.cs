public sealed class WorldEntityConverterTests
{
    [Fact]
    public void DeclarativeWarp_BecomesVisibleActorOnTouchAndWarpAction()
    {
        using var fixture = new ImportFixture("map_a,10,20,0\twarp\t#ordinary\t1,2,map_b,30,40\n");
        var entity = Assert.Single(Convert(fixture).Entities);
        Assert.Equal("warp:map_a:ordinary", entity.Id);
        Assert.Equal((ushort)45, entity.Actor!.Class);
        var trigger = Assert.Single(entity.Triggers);
        Assert.Equal("OnTouch", trigger.Type);
        var warp = Assert.IsType<WarpAction>(Assert.Single(trigger.Actions));
        Assert.Equal(("map_b", (ushort)30, (ushort)40), (warp.Map, warp.X, warp.Y));
    }

    [Fact]
    public void ScriptedDuplicate_IsReevaluatedWithDuplicateName()
    {
        using var fixture = new ImportFixture(ShipScript + "iz_int03,56,15,0\tduplicate(#ship_out)\t#ship_out03\tWARPNPC,1,1\n");
        var entity = Assert.Single(Convert(fixture, "#ship_out03").Entities);
        var actions = Assert.Single(entity.Triggers).Actions;
        Assert.Equal(2, actions.Count);
        var save = Assert.IsType<SetSavePointAction>(actions[0]);
        var warp = Assert.IsType<WarpAction>(actions[1]);
        Assert.Equal(("int_land03", (ushort)77, (ushort)101), (save.Map, save.X, save.Y));
        Assert.Equal(("int_land03", (ushort)85, (ushort)107), (warp.Map, warp.X, warp.Y));
        Assert.Equal(("iz_int03", (ushort)56, (ushort)15, (ushort)1, (ushort)1), (entity.Actor!.Map, entity.Actor.X, entity.Actor.Y, entity.Triggers[0].RadiusX, entity.Triggers[0].RadiusY));
    }

    [Fact]
    public void ScriptedWarpNpc_BecomesActorTriggerAndWarp()
    {
        using var fixture = new ImportFixture(ShipScript);
        var entity = Assert.Single(Convert(fixture, "#ship_out").Entities);
        Assert.NotNull(entity.Actor);
        Assert.Contains(entity.Triggers[0].Actions, action => action is WarpAction);
    }

    [Fact]
    public void NonDeterministicScript_DoesNotInventWarp()
    {
        using var fixture = new ImportFixture("map,1,2,0\tscript\t#unsafe\tWARPNPC,1,1,{\nOnTouch:\n\tif (rand(2)) warp \"a\",1,1;\n}\n");
        var result = Convert(fixture);
        var entity = Assert.Single(result.Entities);
        Assert.Empty(entity.Triggers);
        Assert.False(Assert.Single(entity.Scripts!).RuntimeExecutable);
        Assert.Empty(result.Unsupported);
    }

    [Fact]
    public void SameFilteredInput_HasStableIdsAndByteIdenticalJson()
    {
        using var fixture = new ImportFixture("map_a,10,20,0\twarp\t#ordinary\t1,2,map_b,30,40\n");
        var first = Assert.Single(Convert(fixture).Entities);
        var second = Assert.Single(Convert(fixture).Entities);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(DeterministicJson.Serialize(first), DeterministicJson.Serialize(second));
    }

    [Fact]
    public void BaseTutorialMapFilter_ProducesExactlyThreeExpectedEntities()
    {
        using var fixture = new ImportFixture(
            "iz_int,27,30,0\twarp\t#room_out\t1,1,iz_int,51,30\n" +
            "iz_int,47,30,0\twarp\t#room_in\t1,1,iz_int,22,30\n" +
            ShipScript +
            "iz_int03,27,30,0\twarp\t#room_out03\t1,1,iz_int03,51,30\n");

        var result = WorldEntityConverter.Convert([fixture.Directory], new(null, "iz_int", null, "warp"));

        Assert.Equal(3, result.Entities.Count);
        Assert.Empty(result.Unsupported);
        Assert.Equal("iz_int", Warp(result, "#room_out").Map);
        Assert.Equal(((ushort)51, (ushort)30), (Warp(result, "#room_out").X, Warp(result, "#room_out").Y));
        Assert.Equal(((ushort)22, (ushort)30), (Warp(result, "#room_in").X, Warp(result, "#room_in").Y));
        var ship = Assert.Single(result.Entities, entity => entity.Actor!.Name == "#ship_out");
        Assert.Collection(
            ship.Triggers[0].Actions,
            action => Assert.Equal(new SetSavePointAction("int_land", 77, 101), action),
            action => Assert.Equal(new WarpAction("int_land", 85, 107), action));
    }

    [Fact]
    public void IntroToIzlude_IsStructurallyPreservedAndNonExecutable()
    {
        using var fixture = new ImportFixture(IntroToIzludeScript);

        var result = WorldEntityConverter.Convert(
            [fixture.Directory],
            new("source.txt", "int_land", "#intro_to_izlude", "warp"));

        var entity = Assert.Single(result.Entities);
        Assert.Empty(result.Unsupported);
        Assert.Equal("warp:int_land:intro_to_izlude", entity.Id);
        Assert.Equal(new WorldActorComponent("#intro_to_izlude", "int_land", 49, 57, 0, 45), entity.Actor);
        Assert.Empty(entity.Triggers);
        var script = Assert.Single(entity.Scripts!);
        Assert.Equal(("OnTouch", "int_land", (ushort)49, (ushort)57, (ushort)2, (ushort)2),
            (script.Trigger, script.Map, script.X, script.Y, script.RadiusX, script.RadiusY));
        Assert.True(script.SourceParsed);
        Assert.False(script.RuntimeExecutable);
        Assert.Contains("QuestState", script.RequiredCapabilities);
        Assert.Contains("Dialogue", script.RequiredCapabilities);
        Assert.Contains("Selection", script.RequiredCapabilities);
        Assert.Contains("CompleteQuest", script.RequiredCapabilities);
        Assert.Contains("Warp", script.RequiredCapabilities);
        Assert.Contains("SavePoint", script.RequiredCapabilities);
        Assert.Contains("warp .@map$,196,209;", script.NormalizedSource);
        Assert.Contains("savepoint .@map$,128,142,1,1;", script.NormalizedSource);

        var repeated = Assert.Single(WorldEntityConverter.Convert(
            [fixture.Directory], new("source.txt", "int_land", "#intro_to_izlude", "warp")).Entities);
        Assert.Equal(DeterministicJson.Serialize(entity), DeterministicJson.Serialize(repeated));
    }

    private static ConversionResult Convert(ImportFixture fixture, string? name = null) => WorldEntityConverter.Convert([fixture.Directory], new(null, null, name, "warp"));
    private static WarpAction Warp(ConversionResult result, string name) => Assert.IsType<WarpAction>(Assert.Single(result.Entities, entity => entity.Actor!.Name == name).Triggers[0].Actions[^1]);
    private const string ShipScript = "iz_int,56,15,0\tscript\t#ship_out\tWARPNPC,1,1,{\n\tend;\nOnTouch:\n\t.@num$ = replacestr( strnpcinfo(2), \"ship_out\", \"\" );\n\t.@map$ = \"int_land\" + .@num$;\n\tsavepoint .@map$,77,101;\n\twarp .@map$,85,107;\n\tend;\n}\n";
    private const string IntroToIzludeScript = "int_land,49,57,0\tscript\t#intro_to_izlude\tWARPNPC,2,2,{\n\tend;\nOnTouch:\n\tif (isbegin_quest(21008) == 1) {\n\t\tmes \"Leave?\";\n\t\tif (select(\"Stay\", \"Sail\") == 1) close;\n\t\tcompletequest 21008;\n\t}\n\tif (isbegin_quest(21001) == 1) completequest 21001;\n\t.@map$ = \"izlude\" + replacestr( strnpcinfo(2), \"intro_to_izlude\", \"\" );\n\twarp .@map$,196,209;\n\tsavepoint .@map$,128,142,1,1;\n\tend;\n}\n";

    private sealed class ImportFixture : IDisposable
    {
        public ImportFixture(string content) { Directory = Path.Combine(Path.GetTempPath(), $"athena-world-import-{Guid.NewGuid():N}"); System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Path.Combine(Directory, "source.txt"), content); }
        public string Directory { get; }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
