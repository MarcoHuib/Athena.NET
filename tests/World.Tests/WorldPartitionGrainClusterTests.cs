using Athena.Net.World.Contracts;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

public sealed class WorldPartitionGrainClusterTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { _cluster = new TestClusterBuilder().Build(); await _cluster.DeployAsync(); }
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
        var command = new WorldMovementCommand(presence.PresenceId, presence.CharacterId, "prontera", 150, 180, 152, 181,
            [new(150, 180), new(151, 181), new(152, 181)]);

        var result = await grain.MovePlayerAsync(command);

        Assert.Equal(WorldMovementStatus.Moved, result.Status);
        Assert.Equal((ushort)152, result.Presence!.X);
        Assert.Equal((ushort)181, result.Presence.Y);
    }

    [Fact]
    public async Task SamePartitionTransferMovesBetweenLocalMapsWithSamePresence()
    {
        var grain = Partition("prontera-region");
        var presence = Presence(Guid.NewGuid(), "prontera");
        await grain.RegisterPresenceAsync(presence);
        var transfer = Transfer(presence, "prt_fild08d", "prontera-region");

        var result = await grain.TransferPlayerAsync(transfer);

        Assert.Equal(WorldTransferType.SamePartition, result.Type);
        Assert.Equal(WorldTransferStatus.Completed, result.Status);
        Assert.Equal(presence.PresenceId, result.Presence!.PresenceId);
        Assert.Empty((await grain.GetMapSnapshotAsync("prontera")).Players);
        Assert.Single((await grain.GetMapSnapshotAsync("prt_fild08d")).Players);
    }

    [Fact]
    public async Task CrossPartitionTransferPreservesExactlyOneActiveOwner()
    {
        var source = Partition("prontera-region"); var target = Partition("world-rest");
        var presence = Presence(Guid.NewGuid(), "prontera"); await source.RegisterPresenceAsync(presence);

        var result = await source.TransferPlayerAsync(Transfer(presence, "izlude", "world-rest"));

        Assert.Equal(WorldTransferType.CrossPartition, result.Type);
        Assert.Empty((await source.GetMapSnapshotAsync("prontera")).Players);
        var active = Assert.Single((await target.GetMapSnapshotAsync("izlude")).Players);
        Assert.Equal(presence.PresenceId, active.PresenceId);
    }

    [Fact]
    public async Task PrepareAndCommitReplaysAreIdempotent_AndStaleCleanupCannotRemoveOwner()
    {
        var target = Partition("world-rest");
        var presence = Presence(Guid.NewGuid(), "prontera");
        var transfer = Transfer(presence, "izlude", "world-rest");
        Assert.Equal(IncomingTransferStatus.Prepared, (await target.PrepareIncomingTransferAsync(transfer)).Status);
        Assert.Equal(IncomingTransferStatus.AlreadyPrepared, (await target.PrepareIncomingTransferAsync(transfer)).Status);
        Assert.Equal(IncomingTransferStatus.Committed, (await target.CommitIncomingTransferAsync(transfer.TransferId)).Status);
        Assert.Equal(IncomingTransferStatus.AlreadyCommitted, (await target.CommitIncomingTransferAsync(transfer.TransferId)).Status);
        Assert.Equal(WorldPresenceUnregistrationStatus.PresenceMismatch,
            (await target.UnregisterPresenceAsync("izlude", presence.CharacterId, Guid.NewGuid())).Status);
        Assert.Single((await target.GetMapSnapshotAsync("izlude")).Players);
    }

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);
    private static WorldPlayerPresence Presence(Guid id, string map) => new(id, 2001, 1001, map, 150, 180);
    private static WorldTransferCommand Transfer(WorldPlayerPresence presence, string destination, string partition) =>
        new(Guid.NewGuid(), presence.PresenceId, presence.CharacterId, presence.MapId, destination, 10, 20, partition);
}
