# Character progression: what the data holds, what is ours

This page answers four questions for levels 1 to 100: which skills a character knows, what a skill level does, what a
character's stats are, and what the client is told. Each answer says whether it comes from the client's tables
(recovered data), was seen from the official server (OBSERVED), is inferred (CANDIDATE), or is our own choice (OURS).
The OURS values live in one file, [tools/data/progression.json](../tools/data/progression.json), so a server owner can
tune them without touching code.

## Skill unlocks

**Recovered data.** `tools/class_skills.py` reads SkillAcquireData (WorldProbe export
`research/local/starter-skills-tables/table-002`) and keeps the rows that are `bAutoLearn`, cost nothing (no skill
point, stigma point or item) and need no earlier skill or ascension grade. These rows are the `unlocks` of each class in
`tools/data/class-skills.json`: 25 to 31 per class, at levels 1 to 25 (and 19 common skills at levels 1 to 20).

**Why the unlocks stop at 25.** The data stops there, not only the filter:

- No auto-learned row needs a level above 25, for any class.
- The Mastery rows (skill levels 2 to 10, read without the auto-learn filter) exist only for the skills unlocked at
  levels 1 to 25, plus the 22 skills of the unplayable Fighter class. A skill unlocked at 25 has all of its Mastery rows
  at level 25. No row of any kind names a later skill.
- The filter does drop rows: those with costs, prerequisites or an ascension grade (stigma and ascension skills). The
  raw table is not in the repository, so the number of dropped rows cannot be counted here.

The client's developer buttons show what is missing. DevTeleportDataTable (`tools/data/dev-menu.json`, buttons
`Class_<class>`, `_12`, `_20`) teaches a level-45, ascension-3 character its whole kit with `LearnSkill <id> <level>`.
Per class, 12 or 13 of those skills are absent from the auto-learned rows, for example Gladiator 11080000, 11110000 and
11700000. The other route is CANDIDATE: those skills are probably the ones the filter drops.

**OURS fallback.** `tools/progression.py` takes each class's developer-kit skills that no recovered unlock gives it. It
mixes actives and passives and spreads them evenly from level 26 (`params.unlock.first_level`) to the level cap
(`last_level`, where null means `max_level`, 100). The result is `generated.unlocks`. These rows go through the
existing unlock mechanism:

- the server: `GameData.LearnedSkills` adds them after the recovered rows;
- the database: `python tools/class_skills.py install` puts them in `game.skill_unlocks` (`class_skills.unlock_rows`),
  and the migration-008 trigger `game.sync_character_skills` grants them on level-up like any other row;
- the Python tools: `class_skills.learned_skills` and `class_skills.class_unlocks`.

A character at the cap knows 56 to 62 skills, below the 127 that fit one 0x5100 skill list.

**Stigmas.** With `Aion:Stigmas:Enabled` (the default) the developer-kit actives above are stigmas
([STIGMAS.md](STIGMAS.md)): the server leaves them out of the level kit, and characters buy them with Stigma Shards
instead. Only the passives among the OURS unlocks still come by level.

## Skill levels

**What a skill level is.** A skill has level 1 when learned. Levels 2 to 10 are bought with skill points. The levels
come from SkillAcquireData Mastery rows, kept as `class-skills.json` `levels` in the form `[level, CostSkillPoint,
NeedCharacterLevel]`. Levels 2-4 cost 1 point, 5-7 cost 2 and 8-10 cost 4. A skill unlocked at level 1 reaches level 10
at character level 25; later skills open their levels sooner. The purchase messages are OBSERVED: SkillLevelUp_RQ 0x383f,
0x3840, SkillLevelUpdate_NT 0x3813 and SkillPointUpdate_NT 0x5105 (`GameSession.SkillLevels.cs`). Points come from
Exp.dat BonusSkillPoint (`levels.json` `skill_points`, CANDIDATE as a running total). They reach 86 at level 25 and stop
at 203 from level 45. The developer kits teach levels 12 and 20 (`Class_<class>_12`, `_20`), so levels above 10 exist
through some route the export does not show (CANDIDATE: ascension or stigma).

**What a skill level does.** The export does not say: `damage_rows` keeps only one row set per skill, with no rows per
level. **OURS:** damage, heals, heals over time and damage over time are multiplied by `1 + per_level × (skill level −
1) / 10000`, where `params.skill_level_damage.per_level` is 1000, so +10% per level. This applies in `Combat.SkillHits`
(PvE, duels, arena, Abyss) and in `GameSession.SkillEffects.cs` (`HealAmount`, dot ticks). The skills
`tools/data/skill-effects.json` defines keep their own values and only take this multiplier.

The OURS fallback skills have no Mastery rows. They get the recovered cost ladder (`params.mastery`), one step every 3
levels after the unlock (`generated.levels`).

## Damage rows

**Recovered data.** 89 of the 260 skills in `class-skills.json` have Damage rows. The other 171 are mostly passives
(128) and utility actives: dodges, summons, dismissals, Defiance, and the heals and buffs that `skill-effects.json`
covers. The reading of `EffectValueList` (flat min/max, weapon ratio min/max in 1/10000, PvP copy at [21..24]) is
CANDIDATE. Effect IDs follow `skill × 100 + 11, 21, …` in every recovered row.

**OURS fallback** (`generated.skills`, `generated.damage`). Each definition is built from the class's comparable
recovered damage skills, meaning those in the same cooldown band: short up to 10 s, mid up to 30 s, long above
(`params.cooldown_bands_ms`). It takes the band's median cooldown, MP cost, hit count and per-hit flat/ratio (PvE and
PvP), with effect ID `skill × 100 + 11`. Two kinds of skill get one:

- the 90 active skills unlocked after level 25, which have no recovered cast data. Their cooldown is unknown, so they
  take the short, mid and long bands in turn. The 9 passives get a passive definition with no damage. Active or passive
  is decided by an ID rule (CANDIDATE, `progression.is_passive`: the 7x/8x families and variant IDs). It matches 212 of
  the 235 recovered class skills; the 23 misses are the weapon-equip and dodge specials;
- recovered actives that do hit but whose export has no Damage row, named with a reason in `params.damage_fallbacks`:
  Explosion Trap 14170000 and Divine Aura 17150000.

`GameData.Skill` prefers recovered data, and `GameData.FallbackDamage(id)` says whether a skill's rows are ours. Some
post-25 actives may really be buffs. With the real SkillEffect rows they would move to `skill-effects.json`.

## Stats per level and class

| Stat | Source | Marker |
|---|---|---|
| HPMax | PcStatLevel (`class-skills.json` `stats`, levels 1-100, same values for every class: 100 at level 1, 2562 at level 25, 11000 at level 100) plus gear `HPMax`, enchant, ride, buffs, board, titles | recovered data |
| Defense | PcStatLevel `Defense` plus gear `ArmorDefense` and bonuses | recovered data |
| FixingDamage (attack added to the weapon roll) | PcStatLevel | recovered data |
| MPMax | not in PcStatLevel (its rows hold only FixingDamage, Defense and HPMax). 1500 at level 1 is OBSERVED (official 0x3649 sample). The growth is OURS: `params.mp_max`, `base + per_level × (level − 1)`; 50 per level by default, 60 for Cleric, Chanter, Sorcerer and Elementalist (6450 or 7440 at level 100) | OURS curve |
| Weapon damage, critical, accuracy | Item.dat `MainStats` of the equipped weapon | recovered data |
| Everything else on the stat sheet | the official 0x3649 sample's values | OBSERVED (sample values) |

**Equipment** (`tools/data/items.json`, `tools/item_data.py`). Each item has `c` EquipCategory, `l` ItemLevel, `p`
PermitLevelMin, `r` ItemRace and `s` its fixed MainStats: WeaponMinDamage, WeaponDamage, WeaponFixingDamage,
WeaponAccuracy, Critical, ArmorDefense, HPMax, Block. Random SubStats rolls belong to each item instance and are not
exported.

## What the client is told

- **Stat sheet**: 0x3649 CharAllStats_RS and 0x364a CharStatsUpdate_NT `{u16 type, i32 value}` with i64 HP max. The
  layout and the 29 stat types are OBSERVED (official sample). The type mapping is CANDIDATE where the usmap name is
  unverified. The server changes only the values: 31/33 weapon max/min, 128 critical, 318 accuracy, 193 HPMax, 199 MPMax,
  242 SPMax and 307 defense. The layout is unchanged; a native check holds this.
- **Pools**: 0x8d00 CharPoints (HP, MP and their maxima) and the 0x3633 own appearance (HP, HPMax, MP, MPMax). These are
  OBSERVED layouts.
- **Combat power**: the client has fields for it. 0x3633 MyCharAppear_NT carries `_combat_power` and
  `_highest_combat_power`, and the server already writes them as two i64 in usmap order (CANDIDATE). They also appear in
  0x3645 CharAppear_NT, the lobby list, party and invitation records, and 0x3656 CharCombatPowerUpdate_NT (i64, i64; one
  official sample was fitted in `research/local/tooling/opcodes.json`, CANDIDATE). The server now fills the existing
  0x3633 field with an OURS value. It also adds `CombatPower` to the admin live view (`GET /api/admin/live`,
  `GameSession.LiveView`). Nothing new goes on the wire: no 0x3656, and the other records stay 0.
  - **OURS formula** (`params.combat_power.weights`): the sum of level, attack (mean weapon damage + FixingDamage +
    gear WeaponFixingDamage), defense, HPMax, MPMax, critical, accuracy, block and equip item level, each multiplied by
    its weight. All weights are positive, so better gear never lowers the value.

## What to extract to replace the OURS values

| Gap | Client table (export) | Builder to extend |
|---|---|---|
| Unlocks after level 25, and how the developer-kit skills are acquired | SkillAcquireData, unfiltered (`research/local/starter-skills-tables/table-002`): the rows with CostStigmaPoint, CostSkillPoint, NeedPrevSkillDataList or NeedAscensionGrade, and their AcquireType | `tools/class_skills.py` (`build`: record the dropped rows, with their reason, instead of discarding them) |
| Cast data of the post-25 skills (name, type, weapon, cooldown, cost, Damage rows) | Skill.dat with its SkillEffect rows (`research/local/game-catalog-v2/entries.jsonl`, kind `skills`, `relationships.effects[].rows`) | `tools/class_skills.py` (`cast` currently covers only unlocked skills: add every developer-kit skill) |
| Damage or effect per skill level | SkillEffect rows per skill level, if the client keys them by level (same catalogue) | `damage_rows` in `tools/class_skills.py` |
| Heals, buffs and debuffs | SkillEffect heal and Abnormal_True rows; SkillAbnormal (table-000) and SkillAbnormalEffect (table-001) | `damage_rows` and `tools/box_data.py _abnormals` (see `skill-effects.json` `regenerate`) |
| MPMax and other per-level stats | Any stat table beyond PcStatLevel's three keys (PcStatLevel `research/local/starter-skills-tables/table-007`, PcData table-001), or an official 0x3649 sample at a level above 1 | `tools/class_skills.py` (`stats` keeps every `StatList` key it finds) |
| Combat power formula | An official 0x3633 or 0x3656 sample with a known stat sheet and gear | the 0x3633 writer in `GamePackets.MyCharAppear`, then the weights |
| Item random stats | Item.dat SubStats per instance | `tools/item_data.py` |

## Files

- `tools/data/progression.json`: `params` (hand-edited, OURS) and `generated` (`python tools/progression.py`, rerun
  automatically by `python tools/class_skills.py`).
- `tools/progression.py`: the builder; tests in `tests/test_progression.py`.
- `server/Aion.World/Progression.cs`: `ProgressionTable`, embedded like the other tables; read through
  `GameData.Progression`.
- Native checks: the "progression checks" block in `tests/Aion.Native.Checks/Program.cs`, and the combat-power-with-gear
  check in the stored-session block.
