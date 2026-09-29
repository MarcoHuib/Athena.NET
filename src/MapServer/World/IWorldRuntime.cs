using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Item 14: PlayerPresenceRegistry/PlayerVisibilityCoordinator are now World-fed PROJECTIONS,
// populated by PlayerFeedProjection reconciling PollPlayerFeedAsync results (both local and remote
// players) - MapClientSession no longer registers with them directly. World (via this interface)
// remains the sole player-presence authority.
public interface IWorldRuntime
{
    Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, WorldPlayerPublicState publicState, CancellationToken cancellationToken);
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
    // ApplyMonsterDamageAsync is the sole player->monster HP-mutation seam. NotifyMonsterAttackedAsync
    // is a separate, narrower seam for engagement semantics only where still used (target
    // acquisition/refresh) - it is NOT the live player-hit HP-mutation path.
    Task<WorldMonsterDamageResult> ApplyMonsterDamageAsync(WorldMonsterDamageCommand command, CancellationToken cancellationToken);
    Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken);
    Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken);
    // `mapId` routes this call to the correct partition - see OrleansWorldRuntime's own doc
    // comment on why this is a separate parameter rather than a field on WorldPresenceLifeStateUpdate.
    Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken);

    // Item 14: the World-owned player presence/event feed - same shape/pattern as
    // PollMonsterFeedAsync above, applied to players.
    Task<WorldPlayerFeedPage> PollPlayerFeedAsync(WorldPlayerFeedCursor? cursor, string mapId, CancellationToken cancellationToken);
    Task<WorldPlayerLookUpdateResult> UpdatePlayerLookAsync(string mapId, uint characterId, Guid presenceId, byte direction, byte headDirection, CancellationToken cancellationToken);
    Task<WorldPlayerPublicStateUpdateResult> UpdatePlayerPublicStateAsync(string mapId, uint characterId, Guid presenceId, WorldPlayerPublicState publicState, CancellationToken cancellationToken);
    // The ordering-critical seam for item 14 §3 - see WorldMovementProjectionConfirmation's own doc
    // comment. Must only ever be called AFTER any TruncateMovementAsync for the same MovementId has
    // already resolved.
    Task<WorldMovementProjectionResult> ConfirmMovementProjectionAsync(WorldMovementProjectionConfirmation confirmation, CancellationToken cancellationToken);
    // Item 14 §6 "Monster -> player" cross-replica projection publish - see
    // WorldMonsterAttackActionCommand's own doc comment.
    Task<WorldMonsterAttackPublishResult> PublishMonsterAttackActionAsync(WorldMonsterAttackActionCommand command, CancellationToken cancellationToken);
}
