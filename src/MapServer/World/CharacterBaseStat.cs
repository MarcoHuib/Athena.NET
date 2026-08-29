namespace Athena.Net.MapServer.World;

// The six base stats allocatable through ordinary Status Point spending. Deliberately
// excludes the pinned fourth-job trait stats (POW/STA/WIS/SPL/CON/CRT, SP_POW..SP_CRT in
// pinned src/map/pc.hpp's e_params) - those spend a SEPARATE trait-point pool through
// pc_need_trait_point/pc_traitstatusup2, not Status Points, and are an explicit future slice
// (see ai/map-server.md). CharacterStatService.ValidateIncrease only ever accepts a value from
// this enum, so a trait stat can never reach it even if a future wire boundary mis-maps one.
public enum CharacterBaseStat
{
    Strength,
    Agility,
    Vitality,
    Intelligence,
    Dexterity,
    Luck,
}
