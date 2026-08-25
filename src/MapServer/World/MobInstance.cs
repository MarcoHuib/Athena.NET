namespace Athena.Net.MapServer.World;

public enum MobLifecycleState { Alive, Dead }

// One runtime monster instance. Mutable current HP/lifecycle only lives here,
// never in the immutable generated MobDefinition/MobSpawnDefinition.
public sealed class MobInstance
{
    private readonly Lock _gate = new();
    private uint _currentHp;
    private MobLifecycleState _state;
    private long _deadUntilTimestamp;

    public MobInstance(uint actorId, MobSpawnDefinition spawn, ushort x, ushort y)
    {
        ActorId = actorId;
        Spawn = spawn;
        X = x;
        Y = y;
        _currentHp = spawn.Mob.MaxHp;
        _state = MobLifecycleState.Alive;
    }

    public uint ActorId { get; }
    public MobSpawnDefinition Spawn { get; }
    public ushort X { get; }
    public ushort Y { get; }
    public string Map => Spawn.Map;

    public uint CurrentHp { get { lock (_gate) return _currentHp; } }
    public MobLifecycleState State { get { lock (_gate) return _state; } }
    public bool IsAlive => State == MobLifecycleState.Alive;

    // Applies damage and reports whether THIS call caused the Alive->Dead
    // transition (never true twice for the same death - the state check and
    // the mutation happen under one lock, so two concurrent lethal hits
    // cannot both observe "still alive").
    public (uint HpBefore, uint HpAfter, bool KilledByThisHit) ApplyDamage(uint damage)
    {
        lock (_gate)
        {
            var before = _currentHp;
            if (_state != MobLifecycleState.Alive) return (before, before, false);
            var after = damage >= before ? 0u : before - damage;
            _currentHp = after;
            var killed = after == 0;
            if (killed) _state = MobLifecycleState.Dead;
            return (before, after, killed);
        }
    }

    // Idempotent: only the first caller after death schedules a respawn (via
    // the returned bool); a second call while already dead returns false and
    // changes nothing, so a respawn cannot be scheduled twice for one death.
    public bool TryScheduleRespawn(long dueTimestamp)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Dead || _deadUntilTimestamp != 0) return false;
            _deadUntilTimestamp = dueTimestamp;
            return true;
        }
    }

    // Returns true and resets to Alive/full HP exactly once when `now` has
    // reached the scheduled respawn time; false otherwise (including when no
    // respawn is scheduled, or it already fired).
    public bool TryRespawn(long now)
    {
        lock (_gate)
        {
            if (_state != MobLifecycleState.Dead || _deadUntilTimestamp == 0 || now < _deadUntilTimestamp) return false;
            _state = MobLifecycleState.Alive;
            _currentHp = Spawn.Mob.MaxHp;
            _deadUntilTimestamp = 0;
            return true;
        }
    }
}
