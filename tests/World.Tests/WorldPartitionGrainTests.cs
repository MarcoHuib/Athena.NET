using Athena.Net.World.Contracts;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Athena.Net.World.Runtime;

namespace Athena.Net.World.Tests;

public sealed class WorldPartitionGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    [Fact]
    public async Task PresenceRetryConflictAndStaleUnregisterSemanticsRemainIntact()
    {
        var grain = Partition("world-rest");
        var a = Presence(Guid.NewGuid(), "izlude");
        Assert.Equal(WorldPresenceRegistrationStatus.Registered, (await grain.RegisterPresenceAsync(a)).Status);
        Assert.Equal(WorldPresenceRegistrationStatus.AlreadyRegistered, (await grain.RegisterPresenceAsync(a)).Status);
        Assert.Equal(WorldPresenceRegistrationStatus.Conflict, (await grain.RegisterPresenceAsync(a with { PresenceId = Guid.NewGuid() })).Status);
        Assert.Equal(WorldPresenceUnregistrationStatus.Removed, (await grain.UnregisterPresenceAsync(a.MapId, a.CharacterId, a.PresenceId)).Status);
        Assert.Equal(WorldPresenceUnregistrationStatus.AlreadyAbsent, (await grain.UnregisterPresenceAsync(a.MapId, a.CharacterId, a.PresenceId)).Status);
        var b = a with { PresenceId = Guid.NewGuid() };
        await grain.RegisterPresenceAsync(b);
        Assert.Equal(WorldPresenceUnregistrationStatus.PresenceMismatch, (await grain.UnregisterPresenceAsync(a.MapId, a.CharacterId, a.PresenceId)).Status);
        Assert.Equal([b], (await grain.GetMapSnapshotAsync("izlude")).Players);
    }

    [Fact]
    public async Task MovementUpdatesAuthoritativePartitionPosition()
    {
        var grain = Partition("prontera-region");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await grain.RegisterPresenceAsync(presence);
        var command = new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 152, 181);

        var result = await grain.MovePlayerAsync(command);

        Assert.Equal(WorldMovementStatus.Moved, result.Status);
        Assert.Equal((ushort)150, result.Presence!.X);
        Assert.Equal((ushort)180, result.Presence.Y);
        var movementId = Assert.IsType<Guid>(result.MovementId);
        Assert.Equal(WorldMovementAdvanceStatus.Advanced, (await grain.AdvanceMovementAsync(
            new(movementId, presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 151, 181))).Status);
    }

    [Fact]
    public async Task SamePartitionTransferMovesBetweenLocalMapsWithSamePresence()
    {
        var grain = Partition("prontera-region");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await grain.RegisterPresenceAsync(presence);
        var transfer = Transfer(presence, "prt_fild08d");

        var result = await grain.TransferPlayerAsync(transfer);

        Assert.Equal(WorldTransferType.SamePartition, result.Type);
        Assert.Equal(WorldTransferStatus.Completed, result.Status);
        Assert.Equal(presence.PresenceId, result.Presence!.PresenceId);
        Assert.Equal(presence.ActorId, result.Presence.ActorId);
        Assert.Empty((await grain.GetMapSnapshotAsync("prontera")).Players);
        Assert.Single((await grain.GetMapSnapshotAsync("prt_fild08d")).Players);
        Assert.Equal(WorldPresenceUnregistrationStatus.MapMismatch,
            (await grain.UnregisterPresenceAsync("prontera", presence.CharacterId, presence.PresenceId)).Status);
        Assert.Single((await grain.GetMapSnapshotAsync("prt_fild08d")).Players);
    }

    [Fact]
    public async Task CrossPartitionTransferPreservesExactlyOneActiveOwner()
    {
        var source = Partition("prontera-region"); var target = Partition("world-rest");
        var presence = Presence(Guid.NewGuid(), "prontera"); await source.RegisterPresenceAsync(presence);

        var result = await source.TransferPlayerAsync(Transfer(presence, "izlude"));

        Assert.Equal(WorldTransferType.CrossPartition, result.Type);
        Assert.Empty((await source.GetMapSnapshotAsync("prontera")).Players);
        var active = Assert.Single((await target.GetMapSnapshotAsync("izlude")).Players);
        Assert.Equal(presence.PresenceId, active.PresenceId);
        Assert.Equal(presence.ActorId, active.ActorId);
    }

    [Fact]
    public async Task PrepareAndCommitReplaysAreIdempotent_AndStaleCleanupCannotRemoveOwner()
    {
        var target = Partition("world-rest");
        var presence = Presence(Guid.NewGuid(), "prontera");
        var transfer = Transfer(presence, "izlude");
        var incoming = new IncomingWorldTransfer(transfer.TransferId, presence, "prontera-region", presence.MapId, "izlude", 10, 20);
        Assert.Equal(IncomingTransferStatus.Prepared, (await target.PrepareIncomingTransferAsync(incoming)).Status);
        Assert.Equal(IncomingTransferStatus.AlreadyPrepared, (await target.PrepareIncomingTransferAsync(incoming)).Status);
        Assert.Equal(WorldPresenceRegistrationStatus.Conflict, (await target.RegisterPresenceAsync(presence with { PresenceId = Guid.NewGuid(), MapId = "izlude" })).Status);
        Assert.Equal(IncomingTransferStatus.Committed, (await target.CommitIncomingTransferAsync(transfer.TransferId)).Status);
        Assert.Equal(IncomingTransferStatus.AlreadyCommitted, (await target.CommitIncomingTransferAsync(transfer.TransferId)).Status);
        Assert.Equal(WorldPresenceUnregistrationStatus.PresenceMismatch,
            (await target.UnregisterPresenceAsync("izlude", presence.CharacterId, Guid.NewGuid())).Status);
        Assert.Single((await target.GetMapSnapshotAsync("izlude")).Players);
    }

    [Fact]
    public async Task PrepareRejectsDifferentTransferAndWrongPartitionRejectsMap()
    {
        var target = Partition("world-rest");
        var presence = Presence(Guid.NewGuid(), "prontera");
        var first = new IncomingWorldTransfer(Guid.NewGuid(), presence, "prontera-region", "prontera", "izlude", 10, 20);
        var second = first with { TransferId = Guid.NewGuid() };
        Assert.Equal(IncomingTransferStatus.Prepared, (await target.PrepareIncomingTransferAsync(first)).Status);
        Assert.Equal(IncomingTransferStatus.Conflict, (await target.PrepareIncomingTransferAsync(second)).Status);
        await Assert.ThrowsAnyAsync<Exception>(() => Partition("prontera-region").RegisterPresenceAsync(presence with { MapId = "izlude" }));
    }

    [Fact]
    public async Task FullTransferAndFinalizeReplayCannotDisturbNewerTransfer()
    {
        var prontera = Partition("prontera-region"); var rest = Partition("world-rest");
        var presence = Presence(Guid.NewGuid(), "prontera"); await prontera.RegisterPresenceAsync(presence);
        var a = Transfer(presence, "izlude");
        Assert.Equal(WorldTransferStatus.Completed, (await prontera.TransferPlayerAsync(a)).Status);
        Assert.Equal(WorldTransferStatus.AlreadyCompleted, (await prontera.TransferPlayerAsync(a)).Status);
        var izlude = Assert.Single((await rest.GetMapSnapshotAsync("izlude")).Players);
        var b = Transfer(izlude, "geffen");
        Assert.Equal(WorldTransferStatus.Completed, (await rest.TransferPlayerAsync(b)).Status);
        Assert.Equal(OutgoingTransferStatus.AlreadyFinalized, (await prontera.FinalizeOutgoingTransferAsync(a.TransferId)).Status);
        Assert.Empty((await rest.GetMapSnapshotAsync("izlude")).Players);
        Assert.Equal(presence.ActorId, Assert.Single((await rest.GetMapSnapshotAsync("geffen")).Players).ActorId);
    }

    [Fact]
    public async Task TruncateMovementAsync_ByValidIndex_ShortensPathAndAllowsAdvanceAlongIt()
    {
        var grain = Partition("prontera-region");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await grain.RegisterPresenceAsync(presence);
        var moveResult = await grain.MovePlayerAsync(new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 153, 180));
        Assert.True(moveResult.Path!.Count >= 3, "Test requires a path with an interior cell to truncate to.");
        var movementId = moveResult.MovementId!.Value;

        var truncated = await grain.TruncateMovementAsync(new WorldMovementTruncation(movementId, presence.PresenceId, presence.CharacterId, "prontera", 1));

        Assert.Equal(WorldMovementStatus.Moved, truncated.Status);
        Assert.Equal(2, truncated.Path!.Count);
        var nextCell = truncated.Path[1];
        Assert.Equal(WorldMovementAdvanceStatus.Advanced, (await grain.AdvanceMovementAsync(
            new(movementId, presence.PresenceId, presence.CharacterId, "prontera", 150, 180, nextCell.X, nextCell.Y))).Status);
    }

    [Fact]
    public async Task TruncateMovementAsync_ToStartCellOrPastPathEnd_IsRejected_AndLeavesFullRouteActive()
    {
        var grain = Partition("prontera-region");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await grain.RegisterPresenceAsync(presence);
        var moveResult = await grain.MovePlayerAsync(new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 153, 180));
        var movementId = moveResult.MovementId!.Value;
        var fullPath = moveResult.Path!;

        Assert.Equal(WorldMovementStatus.Rejected, (await grain.TruncateMovementAsync(
            new WorldMovementTruncation(movementId, presence.PresenceId, presence.CharacterId, "prontera", 0))).Status);
        Assert.Equal(WorldMovementStatus.Rejected, (await grain.TruncateMovementAsync(
            new WorldMovementTruncation(movementId, presence.PresenceId, presence.CharacterId, "prontera", fullPath.Count))).Status);

        // The rejected truncations must not have disturbed the original route.
        var nextCell = fullPath[1];
        Assert.Equal(WorldMovementAdvanceStatus.Advanced, (await grain.AdvanceMovementAsync(
            new(movementId, presence.PresenceId, presence.CharacterId, "prontera", 150, 180, nextCell.X, nextCell.Y))).Status);
    }

    [Fact]
    public async Task TruncateMovementAsync_WithStaleMovementIdOrWrongPresence_ReturnsMismatchStatuses()
    {
        var grain = Partition("prontera-region");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await grain.RegisterPresenceAsync(presence);
        var moveResult = await grain.MovePlayerAsync(new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 153, 180));

        Assert.Equal(WorldMovementStatus.SourceMismatch, (await grain.TruncateMovementAsync(
            new WorldMovementTruncation(Guid.NewGuid(), presence.PresenceId, presence.CharacterId, "prontera", 1))).Status);
        Assert.Equal(WorldMovementStatus.PresenceMismatch, (await grain.TruncateMovementAsync(
            new WorldMovementTruncation(moveResult.MovementId!.Value, Guid.NewGuid(), presence.CharacterId, "prontera", 1))).Status);
    }

    [Fact]
    public async Task CancelMovementAsync_ContractDistinguishesCancelledAbsentAndMismatchOutcomes()
    {
        var grain = Partition("prontera-region");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await grain.RegisterPresenceAsync(presence);
        var moveResult = await grain.MovePlayerAsync(new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 153, 180));
        var movementId = moveResult.MovementId!.Value;

        var wrongPresence = await grain.CancelMovementAsync(new WorldMovementCancellation(movementId, Guid.NewGuid(), presence.CharacterId, "prontera"));
        Assert.Equal(WorldMovementCancellationStatus.PresenceMismatch, wrongPresence.Status);

        var wrongMovementId = await grain.CancelMovementAsync(new WorldMovementCancellation(Guid.NewGuid(), presence.PresenceId, presence.CharacterId, "prontera"));
        Assert.Equal(WorldMovementCancellationStatus.SourceMismatch, wrongMovementId.Status);
        // A SourceMismatch cancellation must not have removed the still-active, correctly-identified movement.
        var nextCell = moveResult.Path![1];
        Assert.Equal(WorldMovementAdvanceStatus.Advanced, (await grain.AdvanceMovementAsync(
            new(movementId, presence.PresenceId, presence.CharacterId, "prontera", 150, 180, nextCell.X, nextCell.Y))).Status);

        // Re-establish a fresh route to cancel for real, since the one above already advanced.
        var moveAgain = await grain.MovePlayerAsync(new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", nextCell.X, nextCell.Y, 153, 182));
        var secondMovementId = moveAgain.MovementId!.Value;
        var cancelled = await grain.CancelMovementAsync(new WorldMovementCancellation(secondMovementId, presence.PresenceId, presence.CharacterId, "prontera"));
        Assert.Equal(WorldMovementCancellationStatus.Cancelled, cancelled.Status);
        Assert.Equal(WorldMovementAdvanceStatus.StaleRoute, (await grain.AdvanceMovementAsync(
            new(secondMovementId, presence.PresenceId, presence.CharacterId, "prontera", nextCell.X, nextCell.Y, moveAgain.Path![1].X, moveAgain.Path[1].Y))).Status);

        var alreadyAbsent = await grain.CancelMovementAsync(new WorldMovementCancellation(secondMovementId, presence.PresenceId, presence.CharacterId, "prontera"));
        Assert.Equal(WorldMovementCancellationStatus.AlreadyAbsent, alreadyAbsent.Status);
    }

    [Fact]
    public async Task CancelMovementAsync_ForUnknownCharacter_ReturnsPresenceNotFound()
    {
        var grain = Partition("prontera-region");
        var result = await grain.CancelMovementAsync(new WorldMovementCancellation(Guid.NewGuid(), Guid.NewGuid(), 999999, "prontera"));
        Assert.Equal(WorldMovementCancellationStatus.PresenceNotFound, result.Status);
    }

    [Fact]
    public async Task ConflictingCrossPartitionTransfer_PreservesInFlightMovementOnSourcePartition()
    {
        var prontera = Partition("prontera-region"); var rest = Partition("world-rest");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await prontera.RegisterPresenceAsync(presence);
        var moveResult = await prontera.MovePlayerAsync(new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 153, 180));
        var movementId = moveResult.MovementId!.Value;

        // Force a conflict on the destination partition by pre-preparing a different transfer for the same character.
        var conflicting = new IncomingWorldTransfer(Guid.NewGuid(), presence, "prontera-region", "prontera", "izlude", 10, 20);
        await rest.PrepareIncomingTransferAsync(conflicting);

        var transfer = Transfer(presence, "izlude");
        var result = await prontera.TransferPlayerAsync(transfer);
        Assert.Equal(WorldTransferStatus.Conflict, result.Status);

        // The conflicted transfer must not have discarded the in-flight movement on the source partition.
        var nextCell = moveResult.Path![1];
        Assert.Equal(WorldMovementAdvanceStatus.Advanced, (await prontera.AdvanceMovementAsync(
            new(movementId, presence.PresenceId, presence.CharacterId, "prontera", 150, 180, nextCell.X, nextCell.Y))).Status);
    }

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);
    private static WorldPlayerPresence Presence(Guid id, string map) => new(id, 2001, 1001, map, 150, 180);
    private static WorldTransferCommand Transfer(WorldPlayerPresence presence, string destination) =>
        new(Guid.NewGuid(), presence.PresenceId, presence.CharacterId, presence.MapId, destination, 10, 20);

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.Services.AddSingleton<IWorldPartitionResolver>(
            WorldPartitionResolver.CreateDevelopment(["prontera", "prt_fild08d", "izlude", "geffen"]))
            .AddSingleton<IMovementPathProvider, UnverifiedGridLineMovementPathProvider>();
    }
}
