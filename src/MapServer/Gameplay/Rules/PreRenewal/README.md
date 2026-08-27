# Pre-Renewal gameplay rules - not implemented

This folder is a placeholder for a future Pre-Renewal (`RagnarokRuleSet.PreRenewal`)
implementation of Athena.NET's gameplay-rules interfaces (e.g. `IBasicAttackRules`).

Pre-Renewal gameplay is **not implemented** in this codebase today. Athena.NET
currently targets the current official iRO client, which runs RENEWAL rules -
see `ai/map-server.md`'s "Gameplay ruleset selection" section for the full
composition-boundary rationale.

`RagnarokRuleSet.PreRenewal` exists as a real, parseable configuration/domain
value (`gameplay_ruleset: PreRenewal` in `map_athena.conf` parses successfully),
but selecting it fails MapServer composition with a clear
`NotSupportedException("Pre-Renewal gameplay rules are not implemented.")` from
`GameplayRulesFactory.Create`. There is no silent fallback to Renewal and no
stub/fake Pre-Renewal implementation anywhere in this codebase.

## When Pre-Renewal support is added

A future Pre-Renewal implementation belongs here, mirroring the `Renewal/`
folder's structure:

```text
src/MapServer/Gameplay/Rules/PreRenewal/
    PreRenewalBasicAttackRules.cs   - IBasicAttackRules implementation
    ...other Pre-Renewal-only helpers, as needed
```

It should be traced against pinned `legacy/rathena/` Pre-Renewal (`#ifndef
RENEWAL`) source paths the same way `RenewalBasicAttackRules` traces the RENEWAL
paths, registered from `GameplayRulesFactory.Create`'s `RagnarokRuleSet.
PreRenewal` branch, and must not require any change to `IBasicAttackRules`,
`BasicAttackContext`, `MonsterCombatCoordinator`, `MapClientSession`, or any
other ruleset-agnostic consumer - that is the entire point of the
`IBasicAttackRules` composition boundary.
