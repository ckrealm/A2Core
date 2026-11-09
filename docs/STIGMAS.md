# Stigmas

Stigmas work the way they do on global Aion 2 (October 2026). Every value is marked: CANDIDATE values come from public
global guides and still need the client's own tables or a capture to confirm; OURS values are our choice where global
publishes nothing.

## How it plays

- **Stigma skills.** Each class has its own stigma skills on the Stigma tab. They are no longer handed out by level: a
  character buys them with Stigma Shards.
- **Opening.** The stigma tab opens at level 22. The third Ascension quest's reward shard is given at level 22 (we don't
  check the quest).
- **Slots.** 4 stigma slots open at levels 22, 27, 32 and 37, as on global. A stigma casts only while it is on a stigma
  slot. Korea has 6 slots: `Aion:Stigmas:Slots=6` opens slots 5 and 6 at 42 and 45 (OURS; Korea's levels are not
  published).
- **Shards.** 1 shard at 22, then 1 per level from 23 to 39 and 2 per level from 40 to 45: 16 at 37 and 30 at 45.
  Global's other sources (Abyss, Nightmare and Shugo Festival shops, achievements, Unstable Stigma Shards) are not
  modelled. `Aion:Stigmas:ShardRate` multiplies the levelling shards instead.
- **Levels.** Learning a stigma costs 1 shard (stigma level 1). Levels 2 to 5 cost 1 shard each, 6 to 10 cost 2, 11 to 15
  cost 4 and 16 to 20 cost 8. That is 75 shards for one stigma at 20. Global stops at 20. Korea's Superior shards and
  levels above 20 are not modelled.
- **Power.** Each stigma level adds 5% to the stigma's damage and heals (+95% at 20, OURS). The specialties that global
  unlocks at stigma levels 5, 10, 15 and 20 are not modelled: their effects are not in our data.
- **Reset.** Resetting a stigma, or all of them, forgets it, refunds every shard and clears its slot. It is free (OURS;
  global's price is not published, and Korea's has been free since Chapter 1). Taking one level back also refunds it.
- **`/stigma`** in chat shows any player their shards, open slots and stigmas.
- **Playerbots** buy and slot their own stigmas with their shards at their level, damage stigmas first, raised evenly.
  They cast them when no rotation entry fits. Bot stigmas are not saved.

## Settings (`Aion:Stigmas`)

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Global's stigma rules. `false` gives back the old behaviour: every developer-kit skill unlocks by level (PROGRESSION.md). |
| `Slots` | `4` | Stigma slots (0 to 6). 4 matches global, 6 matches Korea. |
| `ShardRate` | `1` | Multiplies the shards from levelling (0 to 100). |
| `MaxLevel` | `20` | Highest stigma level (1 to 20). |
| `RequireSlot` | `true` | A stigma casts only from a stigma slot. Set `false` if in-game testing shows the client registers stigmas some other way. |

In an env file: `Aion__Stigmas__Slots=4`, `Aion__Stigmas__ShardRate=2`.

## Which skills are stigmas

`tools/stigmas.py` writes the list to `tools/data/stigmas.json` (CANDIDATE). The client's developer kit `Class_<class>`
(DevTeleportDataTable, `tools/data/dev-menu.json`) teaches a level-45, Ascension-3 character its whole kit. The active
skills in that kit that no auto-learned SkillAcquireData row gives the class are the ones `class_skills.py` dropped for
having a cost. Those are the stigmas: 11 or 12 per class. Global lists 13. The 13th, added in March 2026, is not in the
developer kit. The passives in that kit (the 7x family) stay level unlocks.

The Brawler (client class `Fighter`) has 14 candidates: the active kit skills without Mastery rows. The Brawler is not
playable yet. Making it playable is separate work, which has to check this list.

The OURS level unlocks in `progression.json` still list these skills and give them their fallback cast data. With
stigmas on, the server leaves them out of the level kit (`GameSession.LevelKit`).

## Messages (CANDIDATE)

No official stigma purchase has been captured. The client's usmap gives the shapes:

| Action | Client | Server |
|---|---|---|
| Learn or raise | SkillLevelUp_RQ 0x383f {skill} (ESkillAcquireType Stigma rows cost CostStigmaPoint) | 0x3840 {result, skill}, then AddSkillList 0x5101 (reason SkillPointLevelUp) when it is new or SkillLevelUpdate 0x3813 when it is raised, then SkillPointUpdate 0x5105 {type StigmaShard 2, shards left} |
| One level back | SkillLevelDown_RQ 0x3841 | 0x3842, 0x3813 (reason SkillPointLevelDown), 0x5105 |
| Reset | SkillLevelReset_RQ 0x3843 {type Stigma 2, skill or 0 for all} | 0x3844, RemoveSkillList 0x5102, 0x5105 |
| Slot | QuickSlotSet_RQ 0x5601 with a stigma skill (EQuickSlotType Stigma 4) | 0x5602 result 0, or 1 when the stigma is not learned or every open slot is taken |

World entry sends the learned stigmas in the skill list (0x5100) and the shards left (0x5105). Results other than 0 are
1 (not a stigma of the class, or at the cap) and 2 (level below 22, or too few shards), as for skill points.

## Storage

Migration `043_character_stigmas.sql`: `game.character_stigmas (character_id, skill_id, level)`. It is kept apart from
`game.character_skills`, which the level trigger manages. Shards are not stored: they are the level's total less what the
stigma levels cost. Stigma slots are ordinary quick slots.

## To replace the CANDIDATE values

| Value | Client table |
|---|---|
| Shards and slots per level | Exp.dat `BonusStigmaPoint`, `BonusStigmaSuperiorPoint`, `StigmaSkillContextSlotMax` (the `levels.json` export only kept `BonusSkillPoint`) |
| The stigma skills and their level costs | SkillAcquireData rows with `AcquireType` Stigma, `CostStigmaPoint`, `CostStigmaSuperiorPoint`, `GainStigmaSuperiorPoint` |
| Reset prices | GlobalSettingTable `stigma_skill_reset_cost`, `stigma_skill_reset_all_cost` (and the Superior versions) |
| Which skills are stigmas | Skill.dat `bIsStigmaSkill` |
| The unlock quest | GlobalSettingTable `stigma_unlock_condition`, `stigma_unlock_condition_quest_light` / `_dark` |

`tools/stigmas.py build` is the place to read them.
