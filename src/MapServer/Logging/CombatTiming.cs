using System.Diagnostics;
using System.Globalization;

namespace Athena.Net.MapServer.Logging;

// DEBUG-ONLY latency instrumentation helpers for the live monster-combat investigation. Log-only:
// nothing here is ever read by a gameplay, projection or cadence decision. Every duration is measured
// with Stopwatch timestamps (monotonic); `t=` values use the SAME clock origin as the other
// investigation logs (Stopwatch.GetElapsedTime(0)), so every "[iRO MAP TIMING]" and "[iRO MAP DEBUG]"
// line carrying `t=` can be placed on one timeline.
//
// `Context` is an AsyncLocal correlation label ("player-attack:actorId=..:seq=..",
// "mob-attack:actorId=..") set by the combat entry points, so a deep, generic step (gameplay-state
// mutation, CharServer round trip) can say WHICH attack it belonged to without changing any method
// signature. It carries no gameplay data and is never used for a decision.
internal static class CombatTiming
{
    // Ordinary packet writes are only logged when the write lock wait or the socket write itself takes
    // at least this long (keeps a per-packet timer from flooding the log).
    internal const double SlowWriteThresholdMs = 5;

    private static readonly AsyncLocal<string?> CurrentContext = new();

    internal static string Context => CurrentContext.Value ?? "none";

    internal static void SetContext(string context) => CurrentContext.Value = context;

    internal static long Now() => Stopwatch.GetTimestamp();

    internal static double ElapsedMs(long startTimestamp, long endTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMilliseconds;

    internal static double ElapsedMsSince(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    internal static string F(double milliseconds) => milliseconds.ToString("F1", CultureInfo.InvariantCulture);

    internal static string ClockMs() => F(Stopwatch.GetElapsedTime(0).TotalMilliseconds);

    // One line per call: "[iRO MAP TIMING][CATEGORY] <message> ctx=<context> t=<clock>ms".
    internal static void Log(string category, string message) =>
        MapLogger.Info($"[iRO MAP TIMING][{category}] {message} ctx={Context} t={ClockMs()}ms");
}
