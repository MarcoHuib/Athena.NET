namespace Athena.Net.MapServer.Logging;

// DEBUG-ONLY switch for the verbose monster-projection investigation logs (per-monster discovery,
// 0x09FD walk-entry, 0x0088 fixpos, 0x0080 vanish and the per-poll "Feed poll" line). Default ON so
// behavior is unchanged; set ATHENA_MAP_DEBUG_VERBOSE_MONSTER_LOGS=0 (or false/off) to silence
// them for a live A/B timing comparison. Never influences gameplay, projection or cadence - it only
// decides whether a diagnostic line is built and enqueued. The compact per-tick timing summaries are
// deliberately NOT guarded by this switch.
internal static class MonsterDebugLog
{
    internal const string VerboseEnvironmentVariable = "ATHENA_MAP_DEBUG_VERBOSE_MONSTER_LOGS";

    // Settable so a test/benchmark can flip it in-process without touching the environment.
    internal static bool Verbose { get; set; } = ReadVerboseDefault();

    private static bool ReadVerboseDefault()
    {
        var value = Environment.GetEnvironmentVariable(VerboseEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value)) return true;
        return !(value.Equals("0", StringComparison.Ordinal)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase));
    }
}
