using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Item 14 §4: exactly ONE per-map consumer owns the player-feed cursor/epoch, mirroring
// MonsterFeedProjection's own contract exactly (see that type's doc comment for the binding
// bootstrap/resync ordering this type reuses verbatim). MapClientSession never independently polls
// PollPlayerFeedAsync or registers itself with PlayerVisibilityCoordinator any more - every player
// on a map (local session OR remote replica projection) is registered/updated/unregistered through
// THIS type's own ApplySnapshot/ApplyEntry, driven by MapTcpServer's shared tick loop, which then
// drives PlayerPresenceRegistry/PlayerVisibilityCoordinator uniformly for all of them. This is what
// makes PlayerPresenceRegistry/PlayerVisibilityCoordinator projections/caches rather than authority:
// their content is now entirely DERIVED from World's own feed, never independently mutated by a
// session's own packet-handling code.
//
// Binding bootstrap/resync ordering (never reordered - same rationale as MonsterFeedProjection):
//   1. receive Snapshot + Epoch + AsOfSequence from PollPlayerFeedAsync
//   2. reconcile PlayerPresenceRegistry/PlayerVisibilityCoordinator (this type's own ApplySnapshot/
//      ApplyEntry, via the caller-supplied ResolveObserver callback)
//   3. ONLY THEN advance Cursor (via CommitCursor)
public sealed class PlayerFeedProjection(string mapId)
{
    private readonly Lock _gate = new();
    private WorldPlayerFeedCursor? _cursor;
    private WorldSimulationEpoch? _currentEpoch;
    // Every ActorId this projection currently believes is registered in PlayerPresenceRegistry -
    // used purely to compute snapshot/resync diffs (vanished-actor cleanup), never as a second
    // source of presence truth (PlayerPresenceRegistry itself remains that).
    private readonly HashSet<uint> _registeredActorIds = [];

    public string MapId { get; } = mapId;
    public WorldPlayerFeedCursor? Cursor { get { lock (_gate) return _cursor; } }

    // Resolves the IPlayerPresenceObserver for a given ActorId/CharacterId - a live MapClientSession
    // for a LOCAL player, or a fresh NullPlayerPresenceObserver for a remote one (see that type's
    // own doc comment). MapTcpServer owns session enumeration, so this is supplied per-call rather
    // than held by this type.
    public delegate IPlayerPresenceObserver ResolveObserver(uint actorId, uint characterId);

    // Applies an atomic bootstrap or full resync snapshot - registers every currently-present
    // player into PlayerPresenceRegistry/PlayerVisibilityCoordinator (via RegisterAsync, so AOI
    // enter-fanout fires exactly as it always has), unregisters anything this projection previously
    // believed present but that the fresh snapshot no longer contains. Idempotent: a player already
    // registered under the SAME identity is left untouched (ReplacePublicStateAsync only, no
    // spurious leave/re-enter).
    public async Task ApplySnapshotAsync(IReadOnlyList<WorldPlayerPresenceEntry> snapshot, WorldSimulationEpoch epoch,
        PlayerPresenceRegistry registry, PlayerVisibilityCoordinator coordinator, ResolveObserver resolveObserver, CancellationToken cancellationToken)
    {
        HashSet<uint> previousActorIds;
        lock (_gate)
        {
            previousActorIds = [.. _registeredActorIds];
            _currentEpoch = epoch;
        }

        var freshActorIds = new HashSet<uint>();
        foreach (var entry in snapshot)
        {
            freshActorIds.Add(entry.Presence.ActorId);
            var presence = ToPlayerPresence(entry);
            if (registry.TryGetByActorId(entry.Presence.ActorId, out _))
            {
                await coordinator.ReplacePublicStateAsync(presence, cancellationToken);
            }
            else
            {
                var observer = resolveObserver(entry.Presence.ActorId, entry.Presence.CharacterId);
                await coordinator.RegisterAsync(presence, observer, cancellationToken);
            }
        }
        foreach (var staleActorId in previousActorIds)
        {
            if (!freshActorIds.Contains(staleActorId))
                await coordinator.UnregisterAsync(staleActorId, cancellationToken);
        }

        lock (_gate)
        {
            _registeredActorIds.Clear();
            _registeredActorIds.UnionWith(freshActorIds);
        }
    }

    // Applies one incremental feed entry, in feed order (callers must never batch-apply out of
    // order - same convention as MonsterFeedProjection.ApplyEntry).
    public async Task ApplyEntryAsync(WorldPlayerFeedEntry entry, PlayerPresenceRegistry registry, PlayerVisibilityCoordinator coordinator,
        ResolveObserver resolveObserver, CancellationToken cancellationToken)
    {
        var presence = ToPlayerPresence(entry.Entry);
        switch (entry.Kind)
        {
            case WorldPlayerFeedEntryKind.Registered:
            case WorldPlayerFeedEntryKind.TransferredIn:
                if (registry.TryGetByActorId(entry.ActorId, out _))
                {
                    await coordinator.ReplacePublicStateAsync(presence, cancellationToken);
                }
                else
                {
                    var observer = resolveObserver(entry.ActorId, entry.CharacterId);
                    await coordinator.RegisterAsync(presence, observer, cancellationToken);
                    lock (_gate) _registeredActorIds.Add(entry.ActorId);
                }
                break;

            case WorldPlayerFeedEntryKind.Unregistered:
            case WorldPlayerFeedEntryKind.TransferredOut:
                await coordinator.UnregisterAsync(entry.ActorId, cancellationToken);
                lock (_gate) _registeredActorIds.Remove(entry.ActorId);
                break;

            case WorldPlayerFeedEntryKind.Moved:
            case WorldPlayerFeedEntryKind.MovementStarted:
            case WorldPlayerFeedEntryKind.MovementFinished:
                await coordinator.UpdateMovementAsync(presence, broadcastMovement: true, cancellationToken);
                break;

            case WorldPlayerFeedEntryKind.LookChanged:
                await coordinator.UpdateLookAsync(presence, cancellationToken);
                break;
        }
    }

    // Step 5 of the binding ordering - advances Cursor ONLY after the caller has confirmed the
    // reconciliation above completed successfully. Never call this before that.
    public void CommitCursor(WorldSimulationEpoch epoch, long asOfSequence)
    {
        lock (_gate) _cursor = new WorldPlayerFeedCursor(epoch, asOfSequence);
    }

    // Item 14 §5: assembles the FULL 27-field wire projection PlayerPresence from a
    // WorldPlayerPresenceEntry (WorldPlayerPresence + WorldPlayerPublicState + optional
    // WorldPlayerMovementProjection) - the exact shape IroPlayerActorPackets' existing builders
    // already expect, with zero changes to those builders (item 14 §5's explicit requirement).
    internal static PlayerPresence ToPlayerPresence(WorldPlayerPresenceEntry entry)
    {
        var presence = entry.Presence;
        var publicState = entry.PublicState;
        var movement = entry.Movement is { } m
            ? new PlayerMovementPresence(m.StartX, m.StartY, m.DestinationX, m.DestinationY, unchecked((uint)m.StartedAtLocalTickStamp))
            : null;
        return new PlayerPresence(
            presence.ActorId, presence.CharacterId, publicState.CharacterName, presence.MapId,
            presence.X, presence.Y, publicState.Direction, publicState.HeadDirection,
            movement, publicState.JobClass, publicState.Sex, publicState.BaseLevel, publicState.WalkSpeed,
            publicState.HairStyle, publicState.HairColor, publicState.ClothesColor, publicState.BodyStyle,
            publicState.WeaponAppearance, publicState.ShieldAppearance, publicState.HeadBottomAppearance,
            publicState.HeadTopAppearance, publicState.HeadMidAppearance, publicState.RobeAppearance,
            publicState.Manner, publicState.Karma, publicState.Option, publicState.Font);
    }
}
