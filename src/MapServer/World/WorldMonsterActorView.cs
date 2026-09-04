using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// The World-projection-backed implementation of IMonsterActorView (see that interface's own doc
// comment: "today: MobInstance directly; later: a World projection type" - this IS that later
// type). Wraps one WorldMonsterInstance snapshot (position/identity/movement, all World-authoritative
// per the approved Phase 2B boundary) plus a static GeneratedMobRegistry lookup by MobId (Name/
// WalkSpeed - static mob data, never live simulation state). Deliberately does NOT reconstruct a
// MobInstance/MonsterRegistry merely to satisfy this interface - see the plan's own "do not create
// a second movement/lifecycle/engagement authority in MapServer" requirement.
//
// This is a per-poll SNAPSHOT (readonly struct wrapping an immutable WorldMonsterInstance record) -
// a caller that needs a fresh view after the next feed poll must obtain a new instance, matching
// MobPosition/MonsterCombatState's own established "atomic snapshot, re-read when needed" convention
// elsewhere in this codebase.
public readonly struct WorldMonsterActorView : IMonsterActorView
{
    private readonly WorldMonsterInstance _instance;
    private readonly MobDefinition _staticMob;

    // Fails LOUDLY (KeyNotFoundException, via GeneratedMobRegistry.Get) if `instance.MobId` cannot
    // be resolved against the generated static mob table - a World-authoritative monster whose
    // MobId has no corresponding generated MobDefinition is a genuine configuration error (World
    // and MapServer's generated content have diverged), never something to silently paper over
    // with a placeholder name/walk-speed.
    public WorldMonsterActorView(WorldMonsterInstance instance)
    {
        _instance = instance;
        _staticMob = GeneratedMobRegistry.Get(instance.MobId);
    }

    public uint ActorId => _instance.ActorId;

    // WorldMonsterIncarnationId (the World wire type) -> MonsterIncarnationId (MapServer's own
    // domain type, per IMonsterActorView's own contract) - the one conversion point between the
    // two representations, mirroring WorldMonsterMapSimulation.ToWireInstance's own role as the
    // single conversion boundary on the World side.
    public MonsterIncarnationId IncarnationId => new(_instance.IncarnationId.Value);

    public string Map => _instance.MapId;
    public MobPosition GetPosition() => new(_instance.X, _instance.Y);
    public int MobId => _instance.MobId;
    public string Name => _staticMob.Name;
    public int WalkSpeed => _staticMob.WalkSpeed;
    public bool IsWalking => _instance.IsWalking;
    public (ushort X, ushort Y) MovementDestination => (_instance.DestinationX, _instance.DestinationY);

    // The underlying static mob definition - not part of IMonsterActorView's own narrow contract,
    // but a caller of THIS concrete type (rather than the interface) legitimately needs it for
    // combat-relevant static stats (AttackRange, MaxHp, Mode, AttackDelay, etc.) that
    // IMonsterActorView deliberately does not expose (see that interface's own doc comment).
    public MobDefinition StaticMob => _staticMob;

    // The underlying World-authoritative instance - exposed for callers that need fields
    // IMonsterActorView doesn't carry (Lifecycle, Engagement, EngagedTarget) without a second
    // World-projection type duplicating WorldMonsterInstance's own shape.
    public WorldMonsterInstance Instance => _instance;
}
