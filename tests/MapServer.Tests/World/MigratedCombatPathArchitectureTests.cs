using System.Text.RegularExpressions;

namespace Athena.Net.MapServer.Tests.World;

// Step 5 architectural regression: the migrated live combat path (MonsterCombatCoordinator,
// MonsterEngagementTickProcessor) must route CurrentHp/NextAttackAt exclusively through
// MonsterCombatStateStore - never MobInstance.ApplyDamage/ScheduleNextAttack directly (those are
// superseded on this path, retained only for MobInstanceTests' own direct unit coverage). This
// scans the actual production source files rather than relying on developer discipline, so a
// future edit that reintroduces a direct MobInstance.ApplyDamage/ScheduleNextAttack call on the
// migrated path fails this test immediately instead of silently reintroducing a second mutable HP
// authority.
public sealed class MigratedCombatPathArchitectureTests
{
    // Repo-root-relative paths of the exact files this migration touched - deliberately narrow
    // (not "every file under src/MapServer/World") so this test's own failure points precisely at
    // the migrated combat path, not at MobInstanceTests.cs's own legitimate direct calls or
    // MobInstance.cs's own retained (superseded) method bodies.
    private static readonly string[] MigratedCombatFiles =
    [
        "src/MapServer/World/MonsterCombatCoordinator.cs",
        "src/MapServer/Net/MonsterEngagementTickProcessor.cs",
    ];

    // Matches a call of the form `<identifier>.ApplyDamage(` or `<identifier>.ScheduleNextAttack(`
    // where the receiver is NOT `combatState`/`combatEntry`-shaped (the store's own methods share
    // these exact names by design - MonsterCombatStateStore.ApplyDamage/ScheduleNextAttack are the
    // correct, intended call sites this test must NOT flag). Only a receiver that could plausibly
    // be a MobInstance (e.g. `target`, `mob`, `instance`) reaching these method names is a
    // violation - this project's own convention consistently uses `combatState` as the store
    // variable name at every real call site (see the production files this test scans), so
    // matching against `combatState.` covers every legitimate use.
    private static readonly Regex SupersededCallPattern = new(@"(?<!combatState)\.(ApplyDamage|ScheduleNextAttack)\(", RegexOptions.Compiled);

    [Fact]
    public void MigratedCombatPath_NeverCallsMobInstanceApplyDamageOrScheduleNextAttackDirectly()
    {
        var repoRoot = FindRepoRoot();
        foreach (var relativePath in MigratedCombatFiles)
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            Assert.True(File.Exists(fullPath), $"Expected migrated combat file not found: {fullPath}");
            var source = File.ReadAllText(fullPath);

            foreach (Match match in SupersededCallPattern.Matches(source))
            {
                var lineNumber = source[..match.Index].Count(c => c == '\n') + 1;
                var line = source.Split('\n')[lineNumber - 1].Trim();
                Assert.Fail(
                    $"{relativePath}:{lineNumber} calls a superseded MobInstance combat method directly: '{line}'. " +
                    "The migrated combat path must route CurrentHp/NextAttackAt through MonsterCombatStateStore " +
                    "(combatState.ApplyDamage/ScheduleNextAttack), never MobInstance.ApplyDamage/ScheduleNextAttack.");
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Athena.NET.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repo root (Athena.NET.sln) from " + AppContext.BaseDirectory);
    }
}
