# Brawler (client class "Fighter")

Brawler is Korea-only in the live game. The global 2.0.6.0 client we run ships most of its data but not all of it.
This page says what is there, what the server now does with it, and what still needs a live test.

## What the 2.0.6.0 client has (checked 2026-11-09)

| Part | In the client? | Where we saw it |
|---|---|---|
| Class and weapon type | Yes: `ECharacterClass::Fighter` = 12, `EWeaponType::Gauntlet` = 12, UI icons and tabs `Class_Fighter_*` | usmap `research/local/tooling/mappings` |
| Base stats per level | Yes, levels 1-100 | PcStatLevel (`class-skills.json` `stats.Fighter`) |
| Skills | Yes, the whole kit: 1210 skill rows (19xxxxxx), more than any other class, with animation names `Fighter_Skill001`-`047`, a glide skill and a dodge | server data bundle catalogue |
| Skill unlocks | Yes: 25 auto-learned skills (levels 1-25), 223 Mastery rows (skill levels 2-10), 13 stigma skills from level 22 | SkillAcquireData (catalogue `relationships.acquisition`) |
| English skill names | **No**: every Brawler skill is named "???" (`STR_SKILL_PC_FIGHTER_<id>` has placeholder text) | catalogue |
| Weapons | **Only two**: 150960002 / 150960003 "Brawler's Fist", level 1, the creation-screen preview fists (`equip_wp_Gauntlet_L/D_c_Lobby_001a_b`). Every other weapon type has 152 items | catalogue |
| Creation screen row | **No** PCCreationInfo row, so the client's creation screen does not offer Brawler | starter-skills tables |
| Character model (PcData) | **Unknown**: PcData is not in the bundle; reading it needs the client's archive key, which we do not have | - |
| Skill animations / meshes | **Unknown** for the same reason (the client's loose-file manifests have no Fighter paths, but the animations would be in the encrypted paks) | - |

## What the server does (all OURS where marked)

- **Class kit.** `tools/brawler.py` writes `class-skills.json` `classes.Fighter`. The unlock level of each auto-learned
  skill is the character level its skill level 2 needs: that rule holds for all 176 recovered unlocks of the eight
  released classes. The rest of the developer kit (`Class_Fighter`, 14 skills, mostly the stigma skills) unlocks from
  level 26 to the cap like every other class (`tools/progression.py`, OURS).
- **Profiles.** Light and Dark, male and female Brawler profiles in `starter-skills.json` with OURS creation IDs 45
  (Light) and 46 (Dark). The `pc_id` sent to the client is 45-48 (CANDIDATE: PcData numbers four rows per class in class
  order, Chanter 33-36, Test 37-40, Tutorial 41-44; Fighter, the newest class, would be next). **This is the one value
  the live test has to confirm.**
- **Weapon.** The starter weapon is the Brawler's Fist (Light 150960002, Dark 150960003). `tools/equipment_rules.py`
  lets only Brawlers wield gauntlets. Armour has no class limit, so Brawlers wear the same armour as everyone.
- **Weapon scaling (OURS).** With only level-1 gauntlets in the client, a Brawler would hit like level 1 forever. So a
  gauntlet hits like the Greatsword (the other two-handed melee weapon) whose item level is the Brawler's gear level:
  the character level or the mean item level of the rest of their worn gear, whichever is higher
  (`GameData.StatWeapon`, `GameSession.StatWeapon`). Better armour makes the fists stronger. The client still shows
  the fist's own level-1 tooltip; damage, the stat sheet and combat power use the scaled values.
- **Bots.** `tools/bot_rotations.py` knows the Fighter (range 250, stands behind, roles dps and tank). Its hand-made
  PLAN (and the dodge 19000100 in the dodge code) come next from the bot rotations work; until then a Brawler bot
  only uses its past-25 unlocks.

## How to make a Brawler

The creation screen cannot offer it, so make a character of any class with the race and gender you want, then:

- staff in game: `//ClassChange Fighter` (applies after re-entering the world), or the staff dashboard's class change;
- the client's developer button: `//Dev Class_Fighter` (class change, level 45, ascension 3, the whole kit).

Skills keep the client's "???" names and the level-1 fist keeps its tooltip.

## Live test (needs the game client)

1. Install this branch (`deploy/windows/wsl-install.sh` reruns `configure_starters.py`, `class_skills.py install` and
   `equipment_rules.py`).
2. Make a Light character, `//ClassChange Fighter`, log out to the character list and back in.
3. Look for: the character list entry and the character in the world (the pc_id test), the fists in the hand, skill
   icons on the bar, and a skill animation when casting `19010000`.
4. If the client crashes or shows nothing on the character list, pc_id 45-48 is wrong: the next thing to try is the
   Gladiator body (pc_id 5-8 in `tools/brawler.py` `PC_IDS`, rebuild), which shows a Gladiator model with Brawler skills.

## Gaps

- No English skill names (client text). No Brawler weapons past level 1 (client items); the scaling above stands in.
- Stigma skills unlock by level (OURS), as for every class, until stigmas are built.
- Brawler bots wait for their hand-made PLAN in `tools/bot_rotations.py`.
