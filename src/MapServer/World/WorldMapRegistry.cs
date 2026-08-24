using System.Text.Json;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.World;

public sealed class WorldMapRegistry
{
    private readonly IReadOnlyList<WarpDefinition> _warps;
    private readonly IReadOnlyList<WorldActor> _worldActors;
    private readonly IReadOnlyDictionary<string, WorldEntityDefinition> _entitiesById;
    private readonly IReadOnlyDictionary<uint, (WorldEntityDefinition Entity, ScriptBehaviorDefinition Script)> _interactionsByActorId;
    private readonly IReadOnlyList<ScriptTouchBinding> _touchScripts;
    private readonly int _dynamicWarpActorCount;
    public NpcScriptRegistry Scripts { get; }

    public WorldMapRegistry(IEnumerable<WarpDefinition> warps, IEnumerable<WarpActorDefinition>? dynamicWarpActors = null)
        : this(warps, [], dynamicWarpActors) { }

    internal WorldMapRegistry(IEnumerable<WarpDefinition> warps, IEnumerable<WorldEntityDefinition> entities, IEnumerable<WarpActorDefinition>? dynamicWarpActors = null, NpcScriptRegistry? scripts = null)
    {
        Scripts = scripts ?? GeneratedScriptRegistry.Registry;
        _warps = warps.ToArray();
        _entitiesById = entities.ToDictionary(entity => entity.Id, StringComparer.OrdinalIgnoreCase);
        var allocator = new WorldActorIdAllocator();
        var dynamicActors = (dynamicWarpActors ?? []).ToArray();
        _dynamicWarpActorCount = dynamicActors.Length;
        var entityActorKeys = _entitiesById.Values.Where(entity => entity.Actor is not null).Select(entity => SemanticKey(entity.Actor!.Map, entity.Actor.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entityActors = _entitiesById.Values.Where(entity => entity.Actor is not null).Select(ToActorDefinition).Where(actor => actor is not null).Select(actor => actor!);
        _worldActors = _warps.Where(warp => warp.HasWarpActor && warp.RadiusX <= byte.MaxValue && warp.RadiusY <= byte.MaxValue && !entityActorKeys.Contains(SemanticKey(warp.SourceMap, warp.Name)))
            .Select(warp => new WarpActorDefinition(warp.Name, warp.SourceMap, warp.SourceX, warp.SourceY, (byte)warp.RadiusX, (byte)warp.RadiusY))
            .Concat(entityActors)
            .Concat(dynamicActors)
            .Select(actor => new WorldActor(allocator.Allocate(), actor.Name.Length > 24 ? actor.Name[..24] : actor.Name, actor.MapName, actor.X, actor.Y, actor.RadiusX, actor.RadiusY, actor.SpriteClass, actor.Direction, actor.EffectState, actor.EntityId)).ToArray();
        _interactionsByActorId = _worldActors
            .Select(actor => (Actor: actor, Entity: actor.EntityId is not null && _entitiesById.TryGetValue(actor.EntityId, out var entity) ? entity : null))
            .Where(item => item.Entity is not null)
            .SelectMany(item => item.Entity!.Scripts.Where(script => script.RuntimeExecutable && string.Equals(script.Trigger, "OnClick", StringComparison.OrdinalIgnoreCase) &&
                (script.Instructions is { Count: > 0 } || Scripts.TryCreate(item.Entity.Id, script.Trigger, out _))).Select(script => (item.Actor.ActorId, item.Entity, Script: script)))
            .ToDictionary(item => item.ActorId, item => (item.Entity!, item.Script));
        _touchScripts = _worldActors
            .Select(actor => (Actor: actor, Entity: actor.EntityId is not null && _entitiesById.TryGetValue(actor.EntityId, out var entity) ? entity : null))
            .Where(item => item.Entity is not null)
            .SelectMany(item => item.Entity!.Scripts.Where(script => script.RuntimeExecutable && string.Equals(script.Trigger, "OnTouch", StringComparison.OrdinalIgnoreCase) &&
                (script.Instructions is { Count: > 0 } || Scripts.TryCreate(item.Entity.Id, script.Trigger, out _)))
                .Select(script => new ScriptTouchBinding(item.Entity!, item.Actor, script)))
            .OrderBy(item => item.Entity.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static WorldMapRegistry Tutorial { get; } = LoadGenerated();
    public int MapCount => _warps.Select(warp => warp.SourceMap).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int StaticWarpCount => _warps.Count;
    public int EntityCount => _entitiesById.Count;
    public int DynamicWarpActorCount => _dynamicWarpActorCount;
    public IReadOnlyDictionary<string, WorldEntityDefinition> EntitiesById => _entitiesById;
    public IEnumerable<WorldActor> GetVisibleWarpActors(string mapName, ushort x, ushort y, ushort range = 14) => _worldActors.Where(actor => string.Equals(actor.MapName, mapName, StringComparison.OrdinalIgnoreCase) && Math.Abs((int)actor.X - x) <= range && Math.Abs((int)actor.Y - y) <= range);
    public bool TryGetInteraction(uint actorId, string mapName, out WorldEntityDefinition entity, out ScriptBehaviorDefinition script)
    {
        if (_interactionsByActorId.TryGetValue(actorId, out var binding) && binding.Entity.Actor is not null && string.Equals(binding.Entity.Actor.Map, mapName, StringComparison.OrdinalIgnoreCase))
        {
            entity = binding.Entity; script = binding.Script; return true;
        }
        entity = null!; script = null!; return false;
    }
    public bool TryFindWarp(string mapName, ushort x, ushort y, out WarpDefinition warp) { warp = _warps.FirstOrDefault(candidate => candidate.Matches(mapName, x, y))!; return warp is not null; }
    public bool TryFindFirstWarpAlongRoute(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY, out WarpIntersection intersection)
    {
        foreach (var (x, y) in GridLineTraversal.Enumerate(fromX, fromY, toX, toY)) if (TryFindWarp(mapName, x, y, out var warp)) { intersection = new(warp, x, y); return true; }
        intersection = default; return false;
    }
    public bool TryFindFirstScriptTouchEnterAlongRoute(string mapName, ushort fromX, ushort fromY, ushort toX, ushort toY, out ScriptTouchIntersection intersection)
    {
        var candidates = _touchScripts.Where(binding => string.Equals(binding.Script.Map, mapName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var inside = candidates.ToDictionary(binding => binding.Entity.Id, binding => binding.Contains(fromX, fromY), StringComparer.OrdinalIgnoreCase);
        var first = true;
        foreach (var (x, y) in GridLineTraversal.Enumerate(fromX, fromY, toX, toY))
        {
            if (first) { first = false; continue; }
            foreach (var binding in candidates)
            {
                var now = binding.Contains(x, y);
                if (now && !inside[binding.Entity.Id]) { intersection = new(binding, x, y); return true; }
                inside[binding.Entity.Id] = now;
            }
        }
        intersection = default; return false;
    }

    internal static WorldMapRegistry Load(string entityRoot, string legacyWarpFile, string? additionalEntityRoot = null)
    {
        var roots = new[] { entityRoot, additionalEntityRoot }.OfType<string>();
        var jsonEntities = roots.Where(Directory.Exists).SelectMany(root => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)).Order(StringComparer.Ordinal).Select(LoadEntity)
            .Where(entity => !GeneratedScriptRegistry.ContainsEntity(entity.Id));
        var entities = jsonEntities.Concat(GeneratedScriptRegistry.Entities).OrderBy(entity => entity.Id, StringComparer.Ordinal).ToArray();
        var semanticKeys = entities.Where(entity => entity.Actor is not null).Select(entity => SemanticKey(entity.Actor!.Map, entity.Actor.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warps = entities.SelectMany(ToWarps).ToList();
        var dynamicActors = new List<WarpActorDefinition>();
        using var document = JsonDocument.Parse(File.ReadAllText(legacyWarpFile));
        foreach (var item in document.RootElement.GetProperty("StaticWarps").EnumerateArray())
        {
            var name = item.GetProperty("Name").GetString()!; var map = item.GetProperty("SourceMap").GetString()!;
            if (semanticKeys.Contains(SemanticKey(map, name))) continue;
            warps.Add(new(name, map, item.GetProperty("CenterX").GetUInt16(), item.GetProperty("CenterY").GetUInt16(), item.GetProperty("RadiusX").GetUInt16(), item.GetProperty("RadiusY").GetUInt16(), item.GetProperty("DestinationMap").GetString()!, item.GetProperty("DestinationX").GetUInt16(), item.GetProperty("DestinationY").GetUInt16(), item.GetProperty("HasWarpActor").GetBoolean(), item.GetProperty("SourceFile").GetString()!, item.GetProperty("SourceLine").GetInt32()));
        }
        foreach (var item in document.RootElement.GetProperty("DynamicWarps").EnumerateArray())
        {
            if (item.GetProperty("Name").ValueKind != JsonValueKind.String || item.GetProperty("SourceMap").ValueKind != JsonValueKind.String || item.GetProperty("CenterX").ValueKind != JsonValueKind.Number || item.GetProperty("CenterY").ValueKind != JsonValueKind.Number || item.GetProperty("Radius").ValueKind != JsonValueKind.Object) continue;
            var name = item.GetProperty("Name").GetString()!; var map = item.GetProperty("SourceMap").GetString()!;
            if (semanticKeys.Contains(SemanticKey(map, name))) continue;
            var radius = item.GetProperty("Radius"); var rx = radius.GetProperty("X").GetUInt16(); var ry = radius.GetProperty("Y").GetUInt16();
            if (rx <= byte.MaxValue && ry <= byte.MaxValue) dynamicActors.Add(new(name, map, item.GetProperty("CenterX").GetUInt16(), item.GetProperty("CenterY").GetUInt16(), (byte)rx, (byte)ry));
        }
        return new(warps, entities, dynamicActors);
    }

    private static WorldEntityDefinition LoadEntity(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var root = document.RootElement;
        WorldActorComponent? actor = null;
        if (root.TryGetProperty("Actor", out var actorJson)) actor = new(actorJson.GetProperty("Name").GetString()!, actorJson.GetProperty("Map").GetString()!, actorJson.GetProperty("X").GetUInt16(), actorJson.GetProperty("Y").GetUInt16(), actorJson.GetProperty("Direction").GetByte(), actorJson.GetProperty("Class").GetUInt16(), actorJson.TryGetProperty("EffectState", out var effect) ? effect.GetUInt32() : 0);
        var triggers = root.GetProperty("Triggers").EnumerateArray().Select(trigger => new WorldTriggerDefinition(trigger.GetProperty("Type").GetString()!, trigger.GetProperty("Map").GetString()!, trigger.GetProperty("X").GetUInt16(), trigger.GetProperty("Y").GetUInt16(), trigger.GetProperty("RadiusX").GetUInt16(), trigger.GetProperty("RadiusY").GetUInt16(), trigger.GetProperty("Actions").EnumerateArray().Select(ParseAction).ToArray())).ToArray();
        var scripts = root.TryGetProperty("Scripts", out var scriptsJson)
            ? scriptsJson.EnumerateArray().Select(script => new ScriptBehaviorDefinition(
                script.GetProperty("Trigger").GetString()!, script.GetProperty("Map").GetString()!, script.GetProperty("X").GetUInt16(), script.GetProperty("Y").GetUInt16(),
                script.GetProperty("RadiusX").GetUInt16(), script.GetProperty("RadiusY").GetUInt16(), script.GetProperty("SourceParsed").GetBoolean(), script.GetProperty("RuntimeExecutable").GetBoolean(),
                script.GetProperty("RequiredCapabilities").EnumerateArray().Select(value => value.GetString()!).ToArray(), script.GetProperty("NormalizedSource").GetString()!,
                script.TryGetProperty("Instructions", out var instructions) ? instructions.EnumerateArray().Select(ParseInstruction).ToArray() : null,
                script.TryGetProperty("BaseNpcName", out var baseNpcName) ? baseNpcName.GetString() : null)).ToArray()
            : [];
        var source = root.GetProperty("Source");
        return new(root.GetProperty("SchemaVersion").GetInt32(), root.GetProperty("Id").GetString()!, root.GetProperty("Kind").GetString()!, actor, triggers, scripts, new(source.GetProperty("Repository").GetString()!, source.GetProperty("Commit").GetString()!, source.GetProperty("File").GetString()!, source.GetProperty("Line").GetInt32()));
    }
    private static WorldActionDefinition ParseAction(JsonElement action) => action.GetProperty("Type").GetString() switch
    {
        "Warp" => new WarpAction(action.GetProperty("Map").GetString()!, action.GetProperty("X").GetUInt16(), action.GetProperty("Y").GetUInt16()),
        "SetSavePoint" => new SetSavePointAction(action.GetProperty("Map").GetString()!, action.GetProperty("X").GetUInt16(), action.GetProperty("Y").GetUInt16()),
        var type => throw new InvalidDataException($"Unsupported world action '{type}'."),
    };
    private static ScriptInstructionDefinition ParseInstruction(JsonElement instruction) => instruction.GetProperty("Type").GetString() switch
    {
        "Message" => new MessageInstruction(instruction.GetProperty("Text").GetString()!),
        "Next" => new NextInstruction(),
        "Close" => new CloseInstruction(),
        "Close2" => new Close2Instruction(),
        "Select" => new SelectInstruction(instruction.GetProperty("Options").EnumerateArray().Select(option => new SelectOptionDefinition(
            option.GetProperty("Text").GetString()!, option.GetProperty("Instructions").EnumerateArray().Select(ParseInstruction).ToArray())).ToArray()),
        "SetQuest" => new SetQuestInstruction(instruction.GetProperty("QuestId").GetUInt32()),
        "CompleteQuest" => new CompleteQuestInstruction(instruction.GetProperty("QuestId").GetUInt32()),
        "IfQuestState" => new IfQuestStateInstruction(instruction.GetProperty("QuestId").GetUInt32(), Enum.Parse<CharacterQuestStatus>(instruction.GetProperty("Expected").GetString()!, true),
            instruction.GetProperty("Then").EnumerateArray().Select(ParseInstruction).ToArray(), instruction.GetProperty("Else").EnumerateArray().Select(ParseInstruction).ToArray()),
        "Assign" => new AssignmentInstruction(instruction.GetProperty("Variable").GetString()!, ParseExpression(instruction.GetProperty("Value"))),
        "Warp" => new WarpInstruction(ParseExpression(instruction.GetProperty("Map")), instruction.GetProperty("X").GetUInt16(), instruction.GetProperty("Y").GetUInt16()),
        "SavePoint" => new SavePointInstruction(ParseExpression(instruction.GetProperty("Map")), instruction.GetProperty("X").GetUInt16(), instruction.GetProperty("Y").GetUInt16(),
            instruction.TryGetProperty("RadiusX", out var radiusX) ? radiusX.GetUInt16() : (ushort)0, instruction.TryGetProperty("RadiusY", out var radiusY) ? radiusY.GetUInt16() : (ushort)0),
        var type => throw new InvalidDataException($"Unsupported script instruction '{type}'."),
    };
    private static ScriptExpressionDefinition ParseExpression(JsonElement expression) => expression.GetProperty("Type").GetString() switch
    {
        "String" => new StringLiteralExpression(expression.GetProperty("Value").GetString()!),
        "Variable" => new VariableExpression(expression.GetProperty("Name").GetString()!),
        "Concat" => new ConcatExpression(ParseExpression(expression.GetProperty("Left")), ParseExpression(expression.GetProperty("Right"))),
        "StrNpcInfo" => new StrNpcInfoExpression(expression.GetProperty("InfoType").GetInt32()),
        "ReplaceString" => new ReplaceStringExpression(ParseExpression(expression.GetProperty("Value")), ParseExpression(expression.GetProperty("Search")), ParseExpression(expression.GetProperty("Replacement"))),
        var type => throw new InvalidDataException($"Unsupported script expression '{type}'."),
    };
    private static IEnumerable<WarpDefinition> ToWarps(WorldEntityDefinition entity)
    {
        foreach (var trigger in entity.Triggers.Where(trigger => trigger.Type == "OnTouch"))
        {
            var warp = trigger.Actions.OfType<WarpAction>().LastOrDefault(); if (warp is null) continue;
            yield return new(entity.Actor?.Name ?? entity.Id, trigger.Map, trigger.X, trigger.Y, trigger.RadiusX, trigger.RadiusY, warp.Map, warp.X, warp.Y, entity.Actor?.Class == 45, entity.Source.File, entity.Source.Line, trigger.Actions);
        }
    }
    private static WarpActorDefinition? ToActorDefinition(WorldEntityDefinition entity)
    {
        var actor = entity.Actor!;
        var trigger = entity.Triggers.FirstOrDefault();
        var script = entity.Scripts.FirstOrDefault();
        var radiusX = trigger?.RadiusX ?? script?.RadiusX ?? 0;
        var radiusY = trigger?.RadiusY ?? script?.RadiusY ?? 0;
        return radiusX <= byte.MaxValue && radiusY <= byte.MaxValue
            ? new(actor.Name, actor.Map, actor.X, actor.Y, (byte)radiusX, (byte)radiusY, actor.Class, actor.Direction, actor.EffectState, entity.Id)
            : null;
    }
    private static string SemanticKey(string map, string name) => $"{map}:{name}";
    private static WorldMapRegistry LoadGenerated() { var data = Path.Combine(AppContext.BaseDirectory, "data", "world"); return Load(Path.Combine(data, "entities"), Path.Combine(data, "warps.json")); }
}

public readonly record struct WarpIntersection(WarpDefinition Warp, ushort X, ushort Y);
public sealed record ScriptTouchBinding(WorldEntityDefinition Entity, WorldActor Actor, ScriptBehaviorDefinition Script)
{
    public bool Contains(ushort x, ushort y) => Math.Abs((int)x - Script.X) <= Script.RadiusX && Math.Abs((int)y - Script.Y) <= Script.RadiusY;
}
public readonly record struct ScriptTouchIntersection(ScriptTouchBinding Binding, ushort X, ushort Y);
public sealed record WarpActorDefinition(string Name, string MapName, ushort X, ushort Y, byte RadiusX, byte RadiusY, ushort SpriteClass = WorldActor.ClassId, byte Direction = 0, uint EffectState = 0, string? EntityId = null);
