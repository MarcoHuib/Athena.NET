using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Phase-one boundary between the Ragnarok transport adapter and distributed map authority.
// The local PlayerVisibilityCoordinator remains the packet-facing AOI projection until movement
// and visibility migrate together in a later phase.
public interface IWorldRuntime
{
    Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken);
    Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken);
    Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken);
    Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken);
    Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken);
    Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken);
    Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken);

    // Step 6/7 monster-authority RPCs - the World-authoritative monster simulation seam. As of
    // Step 7, World owns identity/epoch/incarnation/position/movement/lifecycle/engagement/chase/
    // respawn timing AND monster CurrentHp/the Alive->Dead transition (ApplyMonsterDamageAsync);
    // MapServer keeps damage calculation/quest-drop/packet-projection/cadence-timing only - it no
    // longer stores authoritative HP locally. Every implementation must apply the SAME
    // normalize-map + resolve-partition + WaitAsync(cancellationToken) + telemetry pattern already
    // used by the player-presence/movement RPCs above (see OrleansWorldRuntime's own
    // MovePlayerAsync for the reference shape).
    Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken);
    Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken);
    // TEMPORARY: retained only until the live MapServer attack path is cut over to
    // ApplyMonsterDamageAsync - scheduled removal in that same substep. Do not add new callers.
    Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference, CancellationToken cancellationToken);
    Task<WorldMonsterDamageResult> ApplyMonsterDamageAsync(WorldMonsterDamageCommand command, CancellationToken cancellationToken);
    Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken);
    Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken);
    // `mapId` routes this call to the correct partition - see OrleansWorldRuntime's own doc
    // comment on why this is a separate parameter rather than a field on WorldPresenceLifeStateUpdate.
    Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken);
}
