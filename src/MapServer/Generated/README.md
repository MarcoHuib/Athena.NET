# Generated Content

This folder contains C# code generated from pinned rAthena sources.

The generated files are part of the normal Athena.NET build. There is no runtime scripting engine and no runtime source parsing.

> **Do not edit generated files by hand.**
> Change the compiler or source selection and regenerate them instead.

## How to read this folder

The main idea is:

```text
Definition  = what something is
Placement   = where it exists
Behavior    = what it does
Runtime     = what is happening to one instance right now
```

For example, an NPC may have:

```text
1 NpcDefinition
1 shared script
5 NpcPlacements
5 independent runtime actors
```

rAthena `duplicate(...)` declarations therefore do not need five copies of the same generated script.

## Folder structure

```text
Generated/
├── Jobs/
│   └── generated numeric job identity registry
│
├── Progression/
│   └── generated level / EXP / progression data
│
├── Skills/
│   └── generated canonical skills and direct/effective job trees
│
└── World/
    └── <area>/
        ├── world definitions and placements
        └── Scripts/
            └── generated executable behaviors
```

Area folders are for organizing related world content. They are not necessarily literal Ragnarok map names.

## World files

Generated world content is grouped by purpose where possible.

Typical files are:

- `*World.cs` — registers definitions and their placements.
- `*Npcs.cs` — reusable NPC definitions.
- `*Warps.cs` / related world files — warp or trigger definitions where applicable.
- `Scripts/*.cs` — executable NPC, warp, or trigger behavior.

A placed object has its own runtime identity even when it shares a definition or script with other placements.

## Simple NPC example

The easiest way to understand the model is to look at one very small NPC.

Imagine an NPC named **Greeter** in Prontera. When a player clicks the NPC, it says hello.

### 1. Define the NPC

The definition describes **what the NPC is** and which behavior it uses.

```csharp
public static readonly NpcDefinition Greeter = new(
    DefinitionId: "custom:greeter",
    TemplateNpcName: "Greeter",
    Behaviors:
    [
        new NpcBehaviorBinding(
            "OnClick",
            static () => new GreeterOnClickScript())
    ]);
```

The definition does **not** contain a map position. The same definition can be placed more than once.

### 2. Write the behavior

The script describes **what the NPC does**.

```csharp
internal sealed class GreeterOnClickScript : INpcScript
{
    public async Task ExecuteAsync(
        ScriptContext context,
        CancellationToken cancellationToken)
    {
        await context.MesAsync("[Greeter]", cancellationToken);
        await context.MesAsync("Hello, adventurer!", cancellationToken);
        await context.NextAsync(cancellationToken);
        await context.MesAsync("Welcome to Prontera.", cancellationToken);
        await context.Close2Async(cancellationToken);
    }
}
```

This is normal compiled C#. A generated rAthena script and a hand-written custom script use the same runtime API.

### 3. Place the NPC in the world

A placement describes **where this particular NPC instance exists**.

```csharp
var placement = new NpcPlacement(
    PlacementId: "npc:prontera:greeter",
    DefinitionId: Greeter.DefinitionId,
    NpcName: "Greeter",
    Map: "prontera",
    X: 150,
    Y: 180,
    Direction: 4,
    Class: 123,
    RadiusX: 0,
    RadiusY: 0);
```

Then register the definition with its placement:

```csharp
world.AddNpc(
    Greeter,
    [
        placement
    ]);
```

The result is:

```text
Greeter definition
        |
        +-- GreeterOnClickScript
        |
        +-- prontera (150,180)
                |
                +-- independent WorldActor at runtime
```

### Reusing the same NPC

If the same NPC should exist in two places, keep **one definition and one script** and add another placement:

```csharp
world.AddNpc(
    Greeter,
    [
        new(
            "npc:prontera:greeter",
            Greeter.DefinitionId,
            "Greeter",
            "prontera",
            150, 180, 4, 123, 0, 0),

        new(
            "npc:izlude:greeter",
            Greeter.DefinitionId,
            "Greeter",
            "izlude",
            120, 90, 4, 123, 0, 0)
    ]);
```

Now there is still:

```text
1 definition
1 script
2 placements
2 independent runtime actors
```

That is also the basic idea used when rAthena contains `duplicate(...)`.

## Generated scripts

Generated scripts are ordinary compiled C#.

They should read similarly to hand-written Athena code, for example:

```csharp
await context.MesAsync("Hello.", cancellationToken);
await context.NextAsync(cancellationToken);
await context.SetQuestAsync(new QuestId(21001), cancellationToken);
```

Compiler AST nodes, parser models, network packet details, and persistence internals should not appear in generated gameplay scripts.

## Source of truth

Generated content comes from the pinned rAthena source tree under:

```text
legacy/rathena/
```

Each generated file contains a header showing its source file and pinned rAthena commit.

`#line` directives may also point back to the original rAthena script so compiler errors and debugging remain traceable.

## Generated vs custom content

Generated and hand-written Athena content use the same runtime contracts.

The runtime should not need to know whether content came from:

```text
rAthena -> Athena.WorldCompiler -> generated C#
```

or was written directly as custom C#.

## Regenerating

Use the relevant `WorldDataImporter` / world compiler command documented in:

```text
tools/WorldDataImporter/README.md
```

Generated output must be deterministic: running the same compiler against the same pinned sources should produce the same files.

## When something looks wrong

Do not fix the generated `.cs` file directly.

Instead check:

1. the pinned rAthena source;
2. the parser / semantic conversion;
3. the lowering model;
4. the C# emitter;
5. the generation command and selection arguments.

Then regenerate the output and run the relevant tests.
