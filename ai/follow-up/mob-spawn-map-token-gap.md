# Follow-up: `SpawnLine` map-token character class gap

Discovered while hardening `RepositoryDomainAnalyzers.AnalyzeMobSpawns`' fail-closed
behavior (PR #26). Deliberately **not fixed** in that PR — this document is the
deterministic input for a dedicated follow-up correctness branch.

## The gap

`MobDataCompiler.SpawnLine`'s `map` capture group is `[A-Za-z0-9_]+`. Pinned
`npc_parse_mob`'s own w1 parse (`sscanf(w1, "%15[^,],%6hd,%6hd,%6hd,%6hd", mapname, ...)`,
`legacy/rathena/src/map/npc.cpp:5233`) places **no character-class restriction** on the
map token beyond "not a comma" — the same `[^,]` convention used by a sibling parse at
`npc.cpp:3901`. Real pinned map names use `-` (PvP room instances: `pvp_n_1-2` ..
`pvp_n_8-5`; tutorial instances: `new_1-3` .. `new_5-3`) and `@` (client-side instance
convention: `1@md_gef`), all of which the current `[A-Za-z0-9_]+` class rejects.

**This is a real, currently-existing gap in this project's ordinary-`monster`-spawn
source coverage** — not something introduced by PR #26. It predates this branch; PR #26
only made it *visible* (as explicit `mob-spawn:parse-failure` analyzer diagnostics)
instead of leaving it silently invisible, which is what happened before.

## What PR #26 does and does not do about it

- PR #26's `RepositoryDomainAnalyzers.AnalyzeMobSpawns` hardening (line-isolated
  `MobDataCompiler.TryReadAllMobSpawns`, candidate-line detection via
  `OrdinaryMonsterCandidateLine`) now correctly recognizes these 171 lines as "this is
  trying to be an ordinary `monster` declaration" and reports each as its own
  `Unsupported` `DomainEntity` with a `mob-spawn:parse-failure` blocker, rather than
  silently absorbing the `SpawnLine` non-match as "not a declaration at all".
- PR #26 does **not** widen `SpawnLine`'s `map` character class. An experimental
  widening (`[^,\t\r\n]+`) was tried and reverted for two reasons:
  1. It is shared by `generate-mob-spawns` (`GenerateMobSpawnsAsync` in `Program.cs`),
     so widening it changes what `generate-mob-spawns` resolves and emits — out of
     scope for an analyzer-only hardening change, and explicitly excluded by this task
     ("do not change generation ... or any current generated data").
  2. The experimental widening also had a real correctness bug of its own: without a
     restricted character class, `//pvp_n_1-2,0,0` (a **commented-out** declaration)
     also matched, with `map = "//pvp_n_1-2"` — accidentally parsing disabled/commented
     content as if it were live. (This surfaced as 14 apparently-unresolved MobIds
     during investigation — verified those specific lines are ALL commented out in
     `npc/re/mobs/dungeons/tha_t.txt`, e.g. `//tha_t09,0,0\tmonster\tVoid Mimic\t20779,...`
     — not a real MobId-resolution gap. See "False lead" below.)
- As a result, PR #26's repository ordinary-monster declaration count (10,068) is the
  count reachable by the **currently supported** `SpawnLine` grammar, not a claim of
  exhaustive pinned coverage. `GeneratedScriptRegistry`/`ai/world-data.md` should be
  read with that caveat until this gap is fixed.

## False lead: "14 unresolved MobIds" — not real

An earlier investigation pass (testing the reverted experimental widening) reported 14
MobIds (2414, 20773-20783, 20844, 20845) that appeared unresolvable against
`db/re/mob_db.yml`. **This was an artifact of the widening bug above, not a real gap.**
Every one of those MobId references traced back to lines commented out in
`npc/re/mobs/dungeons/tha_t.txt` (e.g. lines 257-288, all `//`-prefixed). The
experimental unrestricted map-token regex accidentally treated the `//` prefix as part
of a (bogus) map name and parsed the commented-out declaration as if active. **Do not
carry this "14 unresolved MobIds" claim into the follow-up branch** — it does not
correspond to any real pinned content once comment-filtering is applied correctly (as
the reverted, correctly-scoped `TryReadAllMobSpawns.IsCommentedOrBlankLine` check
already does).

## Real inventory: 171 declarations

All 171 declarations below are genuinely **active** (non-commented) real pinned source.
Every map token resolves against the canonical map-cache layers
(`RathenaMapCacheLayers.Merge`). Every MobId token resolves against
`db/re/mob_db.yml`. **Neither map resolution nor MobId resolution is the blocker for any
of these 171 — the sole blocker is `SpawnLine`'s map-token character class.**

Summary by source file:

| File | Count |
|---|---|
| `npc/custom/etc/mvp_arena.txt` | 114 |
| `npc/pre-re/jobs/novice/novice.txt` | 25 |
| `npc/re/jobs/novice/novice.txt` | 19 |
| `npc/mobs/pvp.txt` | 9 |
| `npc/re/instances/FridayDungeon.txt` | 1 |
| `npc/re/jobs/novice/academy.txt` | 1 |
| `npc/re/mobs/academy.txt` | 1 |
| `npc/re/mobs/championmobs.txt` | 1 |
| **Total** | **171** |

Summary by Renewal source-load classification (`MobSpawnLoadClassifier.Classify`,
`ai/world-data.md`'s "Generated mob spawns" section):

| Load class | Count |
|---|---|
| `Disabled` | 133 |
| `PreRenewalSource` | 25 |
| `RenewalDefault` | 12 |
| `AthenaOverlay` | 1 |
| **Total** | **171** |

Distinct map tokens involved: `pvp_n_1-2` .. `pvp_n_8-5` (all 40 PvP-room combinations),
`new_1-3` .. `new_5-3` (5 tutorial-instance variants), `1@md_gef` (1 client-instance
map). All hyphenated or `@`-containing.

### Full declaration table

| Source file | Line | Map token | Spawn name | MobId token | Load class | Map resolves | MobId resolves |
|---|---|---|---|---|---|---|---|
| `npc/custom/etc/mvp_arena.txt` | 123 | `pvp_n_1-2` | Eddga | `1115` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 124 | `pvp_n_1-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 125 | `pvp_n_2-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 126 | `pvp_n_2-2` | Moonlight | `1150` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 127 | `pvp_n_3-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 128 | `pvp_n_3-2` | Moonlight | `1150` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 129 | `pvp_n_3-2` | Maya | `1147` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 130 | `pvp_n_4-2` | Eddga | `1115` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 131 | `pvp_n_4-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 132 | `pvp_n_4-2` | Moonlight | `1150` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 133 | `pvp_n_4-2` | Maya | `1147` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 134 | `pvp_n_5-2` | Eddga | `1115` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 135 | `pvp_n_5-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 136 | `pvp_n_5-2` | Moonlight | `1150` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 137 | `pvp_n_5-2` | Maya | `1147` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 138 | `pvp_n_6-2` | Eddga | `1115` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 139 | `pvp_n_6-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 140 | `pvp_n_6-2` | Moonlight | `1150` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 141 | `pvp_n_6-2` | Maya | `1147` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 142 | `pvp_n_7-2` | Eddga | `1115` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 143 | `pvp_n_7-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 144 | `pvp_n_7-2` | Moonlight | `1150` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 145 | `pvp_n_7-2` | Maya | `1147` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 146 | `pvp_n_8-2` | Eddga | `1115` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 147 | `pvp_n_8-2` | Mistress | `1059` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 148 | `pvp_n_8-2` | Moonlight | `1150` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 149 | `pvp_n_8-2` | Maya | `1147` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 152 | `pvp_n_1-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 153 | `pvp_n_1-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 154 | `pvp_n_2-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 155 | `pvp_n_2-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 156 | `pvp_n_2-3` | Orc Hero | `1087` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 157 | `pvp_n_3-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 158 | `pvp_n_3-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 159 | `pvp_n_3-3` | Orc Hero | `1087` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 160 | `pvp_n_3-3` | Orc Lord | `1190` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 161 | `pvp_n_4-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 162 | `pvp_n_4-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 163 | `pvp_n_4-3` | Orc Hero | `1087` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 164 | `pvp_n_4-3` | Orc Lord | `1190` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 165 | `pvp_n_5-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 166 | `pvp_n_5-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 167 | `pvp_n_5-3` | Orc Hero | `1087` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 168 | `pvp_n_5-3` | Orc Lord | `1190` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 169 | `pvp_n_6-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 170 | `pvp_n_6-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 171 | `pvp_n_6-3` | Orc Hero | `1087` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 172 | `pvp_n_6-3` | Orc Lord | `1190` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 173 | `pvp_n_7-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 174 | `pvp_n_7-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 175 | `pvp_n_7-3` | Orc Hero | `1087` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 176 | `pvp_n_7-3` | Orc Lord | `1190` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 177 | `pvp_n_8-3` | Phreeoni | `1159` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 178 | `pvp_n_8-3` | Turtle General | `1312` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 179 | `pvp_n_8-3` | Orc Hero | `1087` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 180 | `pvp_n_8-3` | Orc Lord | `1190` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 183 | `pvp_n_1-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 184 | `pvp_n_1-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 185 | `pvp_n_2-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 186 | `pvp_n_2-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 187 | `pvp_n_2-4` | Doppelganger | `1046` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 188 | `pvp_n_3-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 189 | `pvp_n_3-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 190 | `pvp_n_3-4` | Doppelganger | `1046` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 191 | `pvp_n_3-4` | Lord of Death | `1373` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 192 | `pvp_n_4-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 193 | `pvp_n_4-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 194 | `pvp_n_4-4` | Doppelganger | `1046` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 195 | `pvp_n_4-4` | Lord of Death | `1373` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 196 | `pvp_n_5-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 197 | `pvp_n_5-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 198 | `pvp_n_5-4` | Doppelganger | `1046` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 199 | `pvp_n_5-4` | Lord of Death | `1373` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 200 | `pvp_n_6-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 201 | `pvp_n_6-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 202 | `pvp_n_6-4` | Doppelganger | `1046` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 203 | `pvp_n_6-4` | Lord of Death | `1373` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 204 | `pvp_n_7-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 205 | `pvp_n_7-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 206 | `pvp_n_7-4` | Doppelganger | `1046` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 207 | `pvp_n_7-4` | Lord of Death | `1373` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 208 | `pvp_n_8-4` | Drake | `1112` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 209 | `pvp_n_8-4` | Osiris | `1038` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 210 | `pvp_n_8-4` | Doppelganger | `1046` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 211 | `pvp_n_8-4` | Lord of Death | `1373` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 214 | `pvp_n_1-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 215 | `pvp_n_1-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 216 | `pvp_n_2-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 217 | `pvp_n_2-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 218 | `pvp_n_2-5` | Dark Lord | `1272` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 219 | `pvp_n_3-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 220 | `pvp_n_3-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 221 | `pvp_n_3-5` | Dark Lord | `1272` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 222 | `pvp_n_3-5` | Baphomet | `1039` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 223 | `pvp_n_4-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 224 | `pvp_n_4-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 225 | `pvp_n_4-5` | Dark Lord | `1272` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 226 | `pvp_n_4-5` | Baphomet | `1039` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 227 | `pvp_n_5-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 228 | `pvp_n_5-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 229 | `pvp_n_5-5` | Dark Lord | `1272` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 230 | `pvp_n_5-5` | Baphomet | `1039` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 231 | `pvp_n_6-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 232 | `pvp_n_6-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 233 | `pvp_n_6-5` | Dark Lord | `1272` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 234 | `pvp_n_6-5` | Baphomet | `1039` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 235 | `pvp_n_7-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 236 | `pvp_n_7-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 237 | `pvp_n_7-5` | Dark Lord | `1272` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 238 | `pvp_n_7-5` | Baphomet | `1039` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 239 | `pvp_n_8-5` | Incantation Samurai | `1492` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 240 | `pvp_n_8-5` | Pharoh | `1157` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 241 | `pvp_n_8-5` | Dark Lord | `1272` | Disabled | True | True |
| `npc/custom/etc/mvp_arena.txt` | 242 | `pvp_n_8-5` | Baphomet | `1039` | Disabled | True | True |
| `npc/mobs/pvp.txt` | 16 | `pvp_n_8-1` | Side Winder | `1037` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 17 | `pvp_n_8-1` | Bigfoot | `1060` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 22 | `pvp_n_8-2` | Cramp | `1209` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 27 | `pvp_n_8-3` | Whisper | `1179` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 28 | `pvp_n_8-3` | Giant Whisper | `1186` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 33 | `pvp_n_8-4` | Zombie | `1015` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 34 | `pvp_n_8-4` | Ghoul | `1036` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 39 | `pvp_n_8-5` | Khalitzburg | `1132` | RenewalDefault | True | True |
| `npc/mobs/pvp.txt` | 40 | `pvp_n_8-5` | Raydric | `1163` | RenewalDefault | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4278 | `new_1-3` | Poring | `1002` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4279 | `new_1-3` | Drops | `1113` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4280 | `new_1-3` | Lunatic | `1063` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4281 | `new_1-3` | ChonChon | `1011` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4282 | `new_2-3` | Condor | `1009` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4283 | `new_2-3` | Picky | `1050` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4284 | `new_2-3` | Willow | `1010` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4285 | `new_2-3` | Roda Frog | `1012` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4286 | `new_3-3` | Condor | `1009` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4287 | `new_3-3` | Picky | `1050` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4288 | `new_3-3` | Willow | `1010` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4289 | `new_3-3` | Roda Frog | `1012` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4290 | `new_4-3` | Rocker | `1052` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4291 | `new_4-3` | Thief Bug | `1051` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4292 | `new_4-3` | Thief Bug | `1053` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4293 | `new_4-3` | Spore | `1014` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4294 | `new_5-3` | Rocker | `1052` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4295 | `new_5-3` | Thief Bug | `1051` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4296 | `new_5-3` | Thief Bug | `1053` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4297 | `new_5-3` | Spore | `1014` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4298 | `new_1-3` | Fabre | `1184` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4299 | `new_2-3` | Fabre | `1184` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4300 | `new_3-3` | Fabre | `1184` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4301 | `new_4-3` | Fabre | `1184` | PreRenewalSource | True | True |
| `npc/pre-re/jobs/novice/novice.txt` | 4302 | `new_5-3` | Fabre | `1184` | PreRenewalSource | True | True |
| `npc/re/instances/FridayDungeon.txt` | 104 | `1@md_gef` | Shining Plant | `1083` | RenewalDefault | True | True |
| `npc/re/jobs/novice/academy.txt` | 4921 | `new_1-3` | Little Poring | `2398` | RenewalDefault | True | True |
| `npc/re/jobs/novice/novice.txt` | 2352 | `new_1-3` | Poring | `1002` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2353 | `new_1-3` | Drops | `1113` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2354 | `new_1-3` | Lunatic | `1063` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2356 | `new_2-3` | Chonchon | `1011` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2357 | `new_2-3` | Lunatic | `1063` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2358 | `new_2-3` | Willow | `1010` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2359 | `new_2-3` | Poring | `1002` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2361 | `new_3-3` | Chonchon | `1011` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2362 | `new_3-3` | Lunatic | `1063` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2363 | `new_3-3` | Willow | `1010` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2364 | `new_3-3` | Poring | `1002` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2366 | `new_4-3` | Hornet | `1004` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2367 | `new_4-3` | Willow | `1010` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2368 | `new_4-3` | Fabre | `1184` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2369 | `new_4-3` | Picky | `1049` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2371 | `new_5-3` | Hornet | `1004` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2372 | `new_5-3` | Willow | `1010` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2373 | `new_5-3` | Fabre | `1184` | Disabled | True | True |
| `npc/re/jobs/novice/novice.txt` | 2374 | `new_5-3` | Picky | `1049` | Disabled | True | True |
| `npc/re/mobs/academy.txt` | 101 | `new_1-3` | Little Poring | `2398` | AthenaOverlay | True | True |
| `npc/re/mobs/championmobs.txt` | 221 | `new_1-3` | Baby Poring Ringleader | `2776` | RenewalDefault | True | True |

## Recommended follow-up scope

1. Widen `MobDataCompiler.SpawnLine`'s `map` group from `[A-Za-z0-9_]+` to a
   comma/tab-excluding class (`[^,\t\r\n]+`), matching pinned's actual w1 grammar.
2. Verify the widened regex does NOT match commented-out lines — either rely on
   `generate-mob-spawns`/`ReadAllMobSpawns` gaining the same
   `IsCommentedOrBlankLine`-style filter `TryReadAllMobSpawns` already has (currently
   the shared parser has no comment awareness at all, which only "worked" by accident
   because `//` fell outside the old narrow map character class), or add an explicit
   comment-line guard directly to `TryParseSpawnLine`'s caller.
3. Regenerate `src/MapServer/Generated/World` and expect the repository ordinary-monster
   count to grow by exactly 171 (10,068 → 10,239), fully re-deriving
   RenewalDefault/AthenaOverlay/PreRenewalSource/Disabled counts and every downstream
   test/doc count in `ai/world-data.md`/`tools/WorldDataImporter/README.md`.
4. Re-run `tests/WorldDataImporter.Tests` and `tests/MapServer.Tests` fully; expect
   several hardcoded counts to need updating (mirroring the same reconciliation done for
   the bare-map-name fix in the prior PR #25/#26 work).
5. Confirm `PascalCaseMapName`/folder-naming logic in `Program.cs` produces valid C#
   identifiers for the newly-recognized map names (`pvp_n_1-2` → currently would
   produce `PvpN1-2`, containing a literal `-`, which is NOT a valid C# identifier;
   `1@md_gef` → `1@mdGef`, containing `@` and starting with a digit — also invalid).
   This is a second, SEPARATE piece of required work beyond the parser fix itself:
   `PascalCaseMapName` needs its own hardening to strip/sanitize non-identifier
   characters (e.g. `pvp_n_1-2` → `PvpN12` or similar deterministic scheme, `1@md_gef`
   → `MapMdGef` or similar) before these 171 declarations can be safely generated.
