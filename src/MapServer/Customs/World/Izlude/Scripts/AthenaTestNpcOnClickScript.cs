// Handwritten Athena.NET custom development content.
// NOT generated from rAthena.
// Never modified by WorldDataImporter.
using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Customs.World.Izlude.Scripts;

// Development/test NPC menu. Every mutating option composes the SAME existing gameplay
// services generated NPC content already uses through ScriptContext - GrantExperienceAsync
// (-> ExperienceRewardService -> CharacterProgressionService, the real `getexp` path) and
// HealAsync (-> CharacterHealService, the real `heal` path). This script performs no direct
// character-state mutation, persistence, or DB access of its own - see ai/map-server.md's
// "Handwritten custom world content" section and AGENTS.md's authoritative-gameplay rule.
//
// EXP amounts are raw/pre-rate values, exactly like a generated script's own `getexp` call
// (e.g. CaptainCaroccOnClickScript's `GrantExperienceAsync(600, 600, ...)`); the server's
// configured base/job EXP rate still applies on top, matching real gameplay content instead of
// inventing an "ignore rates" test mode.
internal sealed class AthenaTestNpcOnClickScript : INpcScript
{
    private const long BaseExpReward = 10000;
    private const long JobExpReward = 10000;
    private const int FullHealAmount = 999999;

    public async Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken)
    {
        while (true)
        {
            await context.MesAsync("[Athena.NET Test NPC]", cancellationToken);
            await context.MesAsync("This NPC is Athena.NET development/test content. It is not part of the rAthena tutorial.", cancellationToken);
            var choice = await context.SelectAsync(
                [
                    "Give Base EXP",
                    "Give Job EXP",
                    "Give Base + Job EXP",
                    "Full Heal",
                    "Show Character State",
                    "Close",
                ],
                cancellationToken);

            switch (choice)
            {
                case 1:
                    await context.GrantExperienceAsync(BaseExpReward, 0, cancellationToken);
                    await context.MesAsync("[Athena.NET Test NPC]", cancellationToken);
                    await context.MesAsync($"Granted {BaseExpReward} raw Base EXP (before server rate).", cancellationToken);
                    await context.NextAsync(cancellationToken);
                    break;
                case 2:
                    await context.GrantExperienceAsync(0, JobExpReward, cancellationToken);
                    await context.MesAsync("[Athena.NET Test NPC]", cancellationToken);
                    await context.MesAsync($"Granted {JobExpReward} raw Job EXP (before server rate).", cancellationToken);
                    await context.NextAsync(cancellationToken);
                    break;
                case 3:
                    await context.GrantExperienceAsync(BaseExpReward, JobExpReward, cancellationToken);
                    await context.MesAsync("[Athena.NET Test NPC]", cancellationToken);
                    await context.MesAsync($"Granted {BaseExpReward} raw Base EXP and {JobExpReward} raw Job EXP (before server rate).", cancellationToken);
                    await context.NextAsync(cancellationToken);
                    break;
                case 4:
                    await context.HealAsync(FullHealAmount, FullHealAmount, cancellationToken);
                    await context.MesAsync("[Athena.NET Test NPC]", cancellationToken);
                    await context.MesAsync("Fully healed.", cancellationToken);
                    await context.NextAsync(cancellationToken);
                    break;
                case 5:
                    await ShowCharacterStateAsync(context, cancellationToken);
                    break;
                default:
                    await context.MesAsync("[Athena.NET Test NPC]", cancellationToken);
                    await context.MesAsync("Closing.", cancellationToken);
                    await context.CloseAsync(cancellationToken);
                    return;
            }
        }
    }

    private static async Task ShowCharacterStateAsync(ScriptContext context, CancellationToken cancellationToken)
    {
        var state = context.GetGameplayState();
        var jobName = JobClassNames.IsDefined(state.JobClass)
            ? JobClassNames.GetRathenaName((JobClass)state.JobClass)
            : $"Unknown ({state.JobClass})";

        await context.MesAsync("[Athena.NET Test NPC]", cancellationToken);
        await context.MesAsync($"Job/Class: {jobName}", cancellationToken);
        await context.MesAsync($"Base Level: {state.BaseLevel}  Job Level: {state.JobLevel}", cancellationToken);
        await context.MesAsync($"Base EXP: {state.BaseExperience}  Job EXP: {state.JobExperience}", cancellationToken);
        await context.MesAsync($"Stat Points: {state.StatPoints}  Skill Points: {state.SkillPoints}", cancellationToken);
        await context.MesAsync($"HP: {state.CurrentHp}/{state.MaxHp}  SP: {state.CurrentSp}/{state.MaxSp}", cancellationToken);
        await context.NextAsync(cancellationToken);
    }
}
