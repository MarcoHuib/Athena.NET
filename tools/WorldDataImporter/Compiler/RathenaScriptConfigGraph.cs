using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

internal enum ScriptDirectiveKind { Npc, Import }

// Generator/analyzer-internal source-load classification for one pinned mob-spawn declaration's
// SOURCE FILE (ai/world-data.md's "Generated mob spawns" - RathenaRenewalDefault/AthenaIroEffective
// profiles). Deliberately NOT a field on the runtime MobSpawnDefinition record - load-profile
// membership is metadata ABOUT a declaration, not a property that changes what the declaration IS
// (see GeneratedMobSpawnLoadProfiles for the profile-aware views built from this classification).
//
// RenewalDefault: the file is reachable through the active npc:/import: graph rooted at pinned
// npc/re/scripts_main.conf (RathenaScriptConfigGraph.ResolveActiveNpcFiles).
// AthenaOverlay: the file is NOT part of that resolved graph, but is explicitly listed in
// AthenaOverlaySourceFiles.Files - a deliberate Athena.NET activation of pinned-disabled content.
// PreRenewalSource: the file lives under npc/pre-re/ and is neither RenewalDefault nor
// AthenaOverlay. This label is deliberately NOT named "PreRenewalOnly"/"PreRenewalDefault" - this
// project never resolves the actual pre-Renewal config graph, so it proves only "this is
// pre-Renewal-flavored source content outside the resolved Renewal graph", never "this is
// confirmed active under some pre-Renewal profile".
// Disabled: everything else - reachable by neither the resolved Renewal graph nor the Athena
// overlay allow-list, and not under npc/pre-re/ (e.g. a real pinned-disabled event file).
internal enum MobSpawnLoadClass { RenewalDefault, AthenaOverlay, PreRenewalSource, Disabled }

internal static class MobSpawnLoadClassifier
{
    private const string PreRenewalPrefix = "npc/pre-re/";
    private const string RathenaRootPrefix = "legacy/rathena/";

    // Classifies ONE declaration's source file (task section 29: config-graph/allow-list membership
    // is always decided FIRST; a path-prefix check is only ever the LAST fallback for the leftover
    // bucket, never the primary signal for RenewalDefault/AthenaOverlay). Accepts EITHER form a
    // caller may hold: WorldSourceInfo.File/MobDataCompiler's own canonical
    // "legacy/rathena/npc/..." (CanonicalSourceFile, Program.cs), or the plain rathena-root-relative
    // "npc/..." form RathenaScriptConfigGraph.ResolveActiveNpcFiles/RepositoryDomainAnalyzers.Relative
    // already use - normalized to the latter here so renewalActiveFiles/AthenaOverlaySourceFiles/the
    // pre-re prefix check never need two copies of themselves.
    internal static MobSpawnLoadClass Classify(string sourceFile, IReadOnlySet<string> renewalActiveFiles)
    {
        var relativeSourceFile = sourceFile.StartsWith(RathenaRootPrefix, StringComparison.Ordinal)
            ? sourceFile[RathenaRootPrefix.Length..]
            : sourceFile;
        if (renewalActiveFiles.Contains(relativeSourceFile)) return MobSpawnLoadClass.RenewalDefault;
        if (AthenaOverlaySourceFiles.Files.Contains(relativeSourceFile)) return MobSpawnLoadClass.AthenaOverlay;
        if (relativeSourceFile.StartsWith(PreRenewalPrefix, StringComparison.Ordinal)) return MobSpawnLoadClass.PreRenewalSource;
        return MobSpawnLoadClass.Disabled;
    }
}

// One directive line from a pinned rAthena *.conf script-config file. Active reflects whether the
// line is a real directive (not commented-out/blank) - a commented directive is still represented
// here (never silently dropped from ParseFile's own output) so callers/tests can assert both the
// active and inactive cases explicitly, matching AnalyzeMobFlags/AnalyzeFunctions' existing
// "commented lines are seen, just excluded from activation" convention (RepositoryDomainAnalyzers.
// IsCommentedOrBlank).
internal sealed record ScriptDirective(ScriptDirectiveKind Kind, string TargetPath, string SourceFile, int SourceLine, bool Active);

// Parses and follows the real rAthena npc script-config graph (npc:/import: directives in *.conf
// files, starting at npc/re/scripts_main.conf for the Renewal default loadout) - the config-driven
// authority for "which source files does a real rAthena server actually load", as opposed to
// Directory.EnumerateFiles(..., AllDirectories)'s filesystem-wide discovery (still used elsewhere
// for lossless repository-wide source coverage - see ai/world-data.md). Deliberately a fresh, small
// parser: CharacterDataCompiler's existing conf/battle/player.conf reader parses an unrelated
// "key: value" dialect and has no directive/import-graph concept to reuse.
internal static class RathenaScriptConfigGraph
{
    private static readonly Regex DirectiveLine = new(@"^(?<kind>npc|import):\s*(?<path>\S+)", RegexOptions.None);

    // Parses ONE .conf file's own direct npc:/import: lines, in source order. A line is Active
    // unless its content after trimming leading whitespace begins with "//" (rAthena's own line
    // comment syntax) - matches RepositoryDomainAnalyzers.IsCommentedOrBlank's existing convention.
    // Lines that are neither a recognized directive nor blank/comment (stray prose, section-header
    // comments without "//" prefix - none occur in the real pinned files, but this stays permissive
    // rather than throwing) are simply not represented; ParseFile only ever emits genuine directive
    // lines, active or not.
    internal static IReadOnlyList<ScriptDirective> ParseFile(string text, string relativeSourceFile)
    {
        var results = new List<ScriptDirective>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.TrimStart();
            var active = true;
            var content = trimmed;
            if (content.StartsWith("//", StringComparison.Ordinal))
            {
                active = false;
                content = content[2..].TrimStart();
            }
            var match = DirectiveLine.Match(content);
            if (!match.Success) continue;
            var kind = string.Equals(match.Groups["kind"].Value, "import", StringComparison.Ordinal) ? ScriptDirectiveKind.Import : ScriptDirectiveKind.Npc;
            results.Add(new ScriptDirective(kind, match.Groups["path"].Value, relativeSourceFile, i + 1, active));
        }
        return results;
    }

    // Recursively follows ACTIVE import: directives starting at entryConfRelativePath (default:
    // the real Renewal entry point), resolving every directive path relative to rathenaRoot -
    // pinned convention: every npc:/import: target in the real config graph (e.g.
    // "npc/re/mobs/towns.txt", "npc/re/scripts_monsters.conf") is already root-relative, never
    // relative to the importing file's own directory (verified: scripts_main.conf itself lives at
    // "npc/re/scripts_main.conf" yet its own import: targets read "npc/scripts_athena.conf" /
    // "npc/re/scripts_athena.conf", not "../scripts_athena.conf" or "scripts_athena.conf").
    //
    // Returns the deterministic, source-order-preserved list of every ACTIVE npc: target path
    // reachable through the active import: graph - duplicates collapsed to their FIRST occurrence
    // (matches pinned npc_addsrcfile's own real semantics: `util::vector_exists(npc_src_files,
    // name)` is a silent no-op re-add, src/map/npc.cpp:3605-3629 - a file already loaded, whether
    // reached again via a diamond import or a literal duplicate npc: line, is never loaded/queued
    // twice). This ALSO governs revisiting an already-fully-processed conf file itself (the
    // `completed` set below) - a diamond import graph (two different conf files both importing one
    // shared common conf) is real, legitimate DAG re-use, not a cycle.
    //
    // Fails closed (task section 13/12): a cycle in the ACTIVE import: graph throws with the exact
    // chain; a referenced-but-missing ACTIVE file (import: or npc:) throws with the referencing
    // conf file/line - a file referenced only by a commented-out directive is never even read.
    internal static IReadOnlyList<string> ResolveActiveNpcFiles(string rathenaRoot, string entryConfRelativePath = "npc/re/scripts_main.conf")
    {
        var activeNpcFiles = new List<string>();
        var seenNpcFiles = new HashSet<string>(StringComparer.Ordinal);
        var currentlyVisiting = new HashSet<string>(StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var callStack = new List<string>();
        Visit(entryConfRelativePath);
        return activeNpcFiles;

        void Visit(string confRelativePath)
        {
            if (completed.Contains(confRelativePath)) return; // Already fully processed via another import path - a DAG re-use, not a cycle.
            if (!currentlyVisiting.Add(confRelativePath))
                throw new InvalidOperationException($"rAthena script-config import cycle detected: {string.Join(" -> ", callStack)} -> {confRelativePath}.");
            callStack.Add(confRelativePath);

            var fullPath = Path.Combine(rathenaRoot, confRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"rAthena script-config graph references missing conf file '{confRelativePath}' (import chain: {string.Join(" -> ", callStack)}).", fullPath);

            foreach (var directive in ParseFile(File.ReadAllText(fullPath), confRelativePath))
            {
                if (!directive.Active) continue;
                if (directive.Kind == ScriptDirectiveKind.Import)
                {
                    Visit(directive.TargetPath);
                }
                else
                {
                    var npcFullPath = Path.Combine(rathenaRoot, directive.TargetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(npcFullPath))
                        throw new FileNotFoundException($"rAthena script-config graph references missing source file '{directive.TargetPath}' at {confRelativePath}:{directive.SourceLine}.", npcFullPath);
                    if (seenNpcFiles.Add(directive.TargetPath)) activeNpcFiles.Add(directive.TargetPath);
                }
            }

            callStack.RemoveAt(callStack.Count - 1);
            currentlyVisiting.Remove(confRelativePath);
            completed.Add(confRelativePath);
        }
    }
}
