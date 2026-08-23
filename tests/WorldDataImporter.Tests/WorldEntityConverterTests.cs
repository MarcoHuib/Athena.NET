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
        Assert.Empty(result.Entities);
        Assert.Single(result.Unsupported);
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

    private static ConversionResult Convert(ImportFixture fixture, string? name = null) => WorldEntityConverter.Convert([fixture.Directory], new(null, null, name, "warp"));
    private const string ShipScript = "iz_int,56,15,0\tscript\t#ship_out\tWARPNPC,1,1,{\n\tend;\nOnTouch:\n\t.@num$ = replacestr( strnpcinfo(2), \"ship_out\", \"\" );\n\t.@map$ = \"int_land\" + .@num$;\n\tsavepoint .@map$,77,101;\n\twarp .@map$,85,107;\n\tend;\n}\n";

    private sealed class ImportFixture : IDisposable
    {
        public ImportFixture(string content) { Directory = Path.Combine(Path.GetTempPath(), $"athena-world-import-{Guid.NewGuid():N}"); System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Path.Combine(Directory, "source.txt"), content); }
        public string Directory { get; }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
