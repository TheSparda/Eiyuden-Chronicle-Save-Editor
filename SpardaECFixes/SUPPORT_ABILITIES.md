# Support Unit Abilities

Every support unit's ability, character id (verified against this repo's own
`ec_unit_names.json`, not assumed), and whether `SpardaECFixes` currently touches it.
Character ids are useful for `DefaultSupportUnitId` in the plugin's config.

Ability names and effect text are sourced from
[TheGamer's support ability guide](https://www.thegamer.com/eiyuden-chronicle-support-ability-guide/).

| Id | Character | Ability | Effect | SpardaECFixes |
|---|---|---|---|---|
| 400 | Cassandra | Field Party Formation | Enables you to form parties at save points. | ✅ `AlwaysFormPartyAtSavePoints` |
| 70 | Perrielle | Go Get 'Em! | Will sometimes appear before battle to raise allies' attack and action speed. | ✅ `RandomSupportSkillsAlwaysActivate` (generic — any "sometimes appears" unit) |
| 1020 | Janquis | Get Rich Unhumanly Quick | Doubles baqua (money) gained from battles. | — |
| 300 | Marin | Gatherer's Talent | Slightly increases resource acquisition at all collection points. | — |
| 340 | Kerrin | Woodcutter's Nose | Increases resource acquisition at logging points. | ✅ `AlwaysHaveCollectionPointBonus` |
| 940 | Martha | I'll Be Your Forager | Increases resource acquisition at food ingredient points. | ✅ `AlwaysHaveCollectionPointBonus` |
| 330 | Ormond | Miner's Pride | Increases resource acquisition at mining points. | ✅ `AlwaysHaveCollectionPointBonus` |
| 1120 | Pastole | Prey's Aura | Increases resource acquisition at hunting points. | ✅ `AlwaysHaveCollectionPointBonus` |
| 620 | Aire | Burst of Speed | Doubles movement speed. | — |
| 210 | Ivy | Rune Arm Transport Mode | Increases maximum resource bag capacity. | — |
| 250 | Yaelu | Mist of Stealth | Reduces enemy encounter rate. | — |
| 930 | Cabana | Take It Easy | Will sometimes appear before combat to buff your stats. | ✅ `RandomSupportSkillsAlwaysActivate` (generic) |
| 370 | Kurtz | Food's Up! | Will sometimes appear before combat to provide stat buffs. | ✅ `RandomSupportSkillsAlwaysActivate` (generic) |
| 380 | Code L | Limiter Release | Will sometimes appear before combat to elevate everyone's starting SP by one. | ✅ `RandomSupportSkillsAlwaysActivate` (generic) |
| 430 | Nell | The Mysterious Stowpack | Increases maximum storage capacity. | — |
| 420 | Stadler | All In Formation | Activate: compels all allies to strike before the enemy. | — |
| 90 | Rody | Magical Pocket Watch | Activate: greatly increases your party's action speed. | — |
| 470 | Goldsmid | Gentle Yet Mighty | Greatly increases maximum resource bag capacity. | — |
| 1100 | Yulin | Unceasing Training | Doubles EXP earned after combat. | — |
| 630 | Rohan | Emergency Treatment | Restores 20% of the party's HP after combat. | ✅ `AlwaysHaveRohanHealBonus` (configurable minimum, default 20%) |
| 910 | Allaby | A Song on the House | The battle music randomly changes. | — (cosmetic; unlikely to ever be worth a fix) |
| 390 | Hogan | Negotiate Ceasefire | Lets you flee without relying on the odds-of-success factor. | — |
| 960 | Mandie | Egg Hunter | Increases the drop rate of eggfoot eggs after fights. | — |
| 350 | Douglas | Soul of Smithery | Sometimes appears to hone allies' weapons, increasing the party's attack. | ✅ `RandomSupportSkillsAlwaysActivate` (generic) |
| 1000 | Euma | Euchrisse Archer Volley | Activate: summons Euchrissian archers who damage the enemy party. | — |
| 1010 | Kassius | Euchrisse Cavalry Charge | Activate: summons Euchrissian cavalry who damage the enemy party. | — |

## Coverage summary

**Implemented (8 of 26):** Cassandra, Kerrin, Martha, Ormond, Pastole, Rohan, and the
five "sometimes appears before battle" units (Perrielle, Cabana, Kurtz, Code L, Douglas)
via the generic `RandomSupportSkillsAlwaysActivate` fix.

**Not yet implemented (18 of 26):** none of these have a corresponding entry in the
community Cheat Engine table, so each would need its own from-scratch investigation —
the same depth of work as the Cassandra and collection-point fixes took. Roughly
grouped by what kind of fix they'd likely need:

- **Numeric multipliers/bonuses** (probably similar in shape to the collection-point and
  heal-rate fixes — find the method that computes the value, patch or clamp it):
  Janquis (money x2), Marin (collection rate, smaller than the 100% four), Aire (move
  speed x2), Ivy/Nell/Goldsmid (bag/storage capacity), Yaelu (encounter rate), Yulin (EXP
  x2), Mandie (egg drop rate)
- **Manually-activated battle commands** (a different kind of gate — command
  availability, not a passive check): Stadler, Rody, Euma, Kassius
- **Cosmetic, likely not worth fixing:** Allaby (battle music)
- **Behavioral override:** Hogan (skip the flee odds check entirely)

If you want one of these prioritized, the process is: find it in
`reference/EiyudenChronicle.CT` (search by character name or the described effect), read
the AOB and decode what it does (see the History section in `Plugin.cs` for how the
existing fixes were reverse-engineered), then implement — Harmony patch first if a
substantial method is involved, native patch only if testing shows the check is inlined.
