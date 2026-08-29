using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

// Proves the Athena Test NPC's Base EXP / Job EXP / Full Heal / Show Character State menu
// options invoke the SAME INpcScriptHost capabilities a generated NPC script uses - not a
// parallel/duplicate mutation path - by exercising the real MapClientSession (cast to
// INpcScriptHost, matching how AthenaTestNpcOnClickScript's ScriptContext actually calls it).
// This is a wiring/reuse proof, not a re-test of CharacterProgressionService/CharacterHealService
// themselves (see CharacterProgressionServiceTests/CharacterHealServiceTests for that coverage) -
// see ai/map-server.md's "Handwritten custom world content" section, item 20.
public sealed class AthenaTestNpcScriptHostWiringTests
{
    private const uint AccountId = 4_000_000;
    private const uint CharId = 21;

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public CharacterGameplayState? Updated { get; private set; }
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken t)
        {
            Updated = updated;
            return Task.FromResult<CharacterGameplayState?>(updated);
        }
    }

    private static async Task<(TcpClient Client, MapClientSession Session, FixedGameplayStatePersistence Persistence, Task RunTask)> SetupAsync(CharacterGameplayState initial)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();

        var persistence = new FixedGameplayStatePersistence(initial);
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: persistence, accountId: AccountId, charId: CharId);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        return (client, session, persistence, run);
    }

    private static async Task DisposeAsync(TcpClient client, MapClientSession session, Task run)
    {
        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task GrantExperienceAsync_BaseExpOption_AppliesThroughRealProgressionPathAndPersists()
    {
        var initial = new CharacterGameplayState(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        await host.GrantExperienceAsync(10000, 0, CancellationToken.None);

        Assert.NotNull(persistence.Updated);
        Assert.True(persistence.Updated!.BaseExperience > 0);
        Assert.Equal(0u, persistence.Updated.JobExperience);
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task GrantExperienceAsync_JobExpOption_RaisesJobLevelAndSkillPointsThroughRealProgressionPath()
    {
        // A fresh Novice's job-exp-to-next-level threshold is small; 10000 raw job EXP guarantees
        // at least one job level crossing (and therefore a skill-point award) through the real
        // CharacterProgressionService pipeline - exactly the same one GrantExperienceAsync's other
        // real caller (CaptainCaroccOnClickScript's getexp) uses.
        var initial = new CharacterGameplayState(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        await host.GrantExperienceAsync(0, 10000, CancellationToken.None);

        Assert.NotNull(persistence.Updated);
        Assert.True(persistence.Updated!.JobLevel > initial.JobLevel);
        Assert.True(persistence.Updated.SkillPoints > initial.SkillPoints);
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task HealAsync_FullHealOption_ReusesExistingHealServiceAndClampsToMax()
    {
        var initial = new CharacterGameplayState(CharId, 1, 0, 1, 1, 0, 0, 5, 2, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        var (client, session, persistence, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        await host.HealAsync(999999, 999999, CancellationToken.None);

        Assert.NotNull(persistence.Updated);
        Assert.Equal(initial.MaxHp, persistence.Updated!.CurrentHp);
        Assert.Equal(initial.MaxSp, persistence.Updated.CurrentSp);
        await DisposeAsync(client, session, run);
    }

    [Fact]
    public async Task GetGameplayState_ReturnsTheSameInMemorySnapshotNoAdditionalPersistenceQuery()
    {
        var initial = new CharacterGameplayState(CharId, 1, 0, 5, 3, 100, 200, 30, 8, 40, 11, 48, 2, 1, 1, 1, 1, 1, 1);
        var (client, session, _, run) = await SetupAsync(initial);
        var host = (INpcScriptHost)session;

        var state = host.GetGameplayState();

        Assert.Equal(initial, state);
        await DisposeAsync(client, session, run);
    }
}
