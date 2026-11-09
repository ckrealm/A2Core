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
  the live test has to confirm**, so by default the server does not send it: `Aion:Rules:BrawlerModel` is `gladiator`
  (default, safe: a Brawler is drawn with the Gladiator body of its race and gender, with Brawler skills and fists) or
  `own` (pc_id 45-48).
- **Weapon.** The starter weapon is the Brawler's Fist (Light 150960002, Dark 150960003). `tools/equipment_rules.py`
  lets only Brawlers wield gauntlets. Armour has no class limit, so Brawlers wear the same armour as everyone.
- **Weapon scaling (OURS).** With only level-1 gauntlets in the client, a Brawler would hit like level 1 forever. So a
  gauntlet hits like the Greatsword (the other two-handed melee weapon) whose item level is the Brawler's gear level:
  the character level or the mean item level of the rest of their worn gear, whichever is higher
  (`GameData.StatWeapon`, `GameSession.StatWeapon`). Better armour makes the fists stronger. The client still shows
  the fist's own level-1 tooltip; damage, the stat sheet and combat power use the scaled values.
- **Bots.** Not yet: the bot rotations work adds the Fighter with a hand-made PLAN (and the dodge 19000100) next.

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
4. With the default `BrawlerModel=gladiator` this is safe. Then set `Aion__Rules__BrawlerModel=own` in the game env,
   restart, and log in again: if the character list and world draw the Brawler, keep `own`.
5. If `own` crashes the client or the character list, set it back to `gladiator` and restart: every Brawler is drawn
   as a Gladiator again at once (nothing stored changes). To turn a Brawler back into another class, use the staff
   dashboard's class change (it does not need the character in the world); its fists go back to the bag.

## The Korean live Brawler (research, 2026-11-09)

- **Release:** Korea and Taiwan, 1 July 2026, update "Chapter 1. 모래와 서리의 땅" (with level cap 50 and stigma level 25).
  Korean name 권성, weapon 권갑 (gauntlet). NCSOFT has no official English name; Korean press in English says "Brawler".
- **Role:** front-line melee DPS, not a tank. **Resource:** rage (분노) instead of MP; 폭주 (Rampage) spends rage over
  time for +15% combat speed and swaps in stronger skill versions. The server has no rage system: Brawlers use MP
  (0-cost skills) like the client data says.
- **Typical PvE opener** (inven guide): Rage (격노) at full rage → Rampage (폭주) → Pulverize (파쇄) → Phantom (분신) →
  Surging Cleave (공파참) → Whirlwind (질풍격) → Flurry chain mixed with Exploding Fist and Ascending Blow on cooldown.
- **Not documented online as text:** gauntlet item names and stats, the Brawler's Daevanion nodes, per-level numbers.

Skill names (`tools/data/brawler-skills.json`; English names are aion2hub.me fan names or ours, none is official; the
client still shows "???", the names reach the server's tools, bots and docs). Matched by learn level and cooldown
against shugo.gg/ko (the 12 actives, 10 passive levels and 13 stigma cooldowns line up one-for-one):

| ID | Korean | English | Kind | Effect | Match |
|---|---|---|---|---|---|
| 19000100 | 회피 | Dodge | dodge | Class dodge (긴급 회피); enables 철산고 | medium |
| 19010000 | 연격 | Flurry | basic attack/chain | 3-stage chain 연격→중격→강격, up to 4 targets, +10 rage per hit | high |
| 19040000 | 비각 | Roundhouse | basic attack/chain | 3-stage chain 비각→승강권→충권, stagger damage; in Berserk cuts 연환권 cooldown by 1s | high |
| 19070000 | 질풍격 | Whirlwind | gap closer | 20m dash through up to 4 enemies; enables 환영각 pull for 5s | high |
| 19090000 | 지면 강타 | Smite Ground | AoE stun | AoE on up to 4 targets, 50% chance of 2s stun | high |
| 19110000 | 연속 차기 | Consecutive Kicks | AoE | 120s cooldown, heavy AoE, 75% chance of 3s knockdown | low |
| 19130000 | 폭주 | Rampage | buff | Enter Berserk: drains rage, +15% combat speed, +15% frontal damage amp, swaps in [폭주] skills | high |
| 19160000 | 승천타 | Ascending Blow | gap closer | Charged 10m rush, AoE, 50% chance of 3s air bind at full charge; main damage skill | high |
| 19170000 | 회전각 | Sweeping Kick | stun | Block counter, 2 hits, 50% chance of 2s stun | high |
| 19200000 | 폭렬권 | Exploding Fist | attack | 3-stage charge, +100/200/300 rage; instant in Berserk | high |
| 19230000 | 철산고 | Shoulder Tackle | gap closer | 10m rush after a dodge or in flight, 30% chance of 3s stun, +50 rage | high |
| 19250000 | 태풍 연타 | Typhoon Flurry | attack | Multi-hit flurry on groggy targets | high |
| 19270000 | 선풍각 | Cyclone Kick | knockback | Kick with 10m knockback | low |
| 19280000 | 격노 | Rage | buff | Restores 52% rage, PvE/PvP damage amp for 15s | high |
| 19290000 | 공파참 | Surging Cleave | gap closer | 20m dash, 50% chance of 2s stun | high |
| 19300000 | 진각 | Quake | gap closer | Move to target within 10m, AoE, 50% chance of 3s slow | high |
| 19310000 | 파쇄 | Pulverize | buff | For 20s, every 8 hits trigger a 2-hit AoE | high |
| 19320000 | 혈류 차단 | Sever Bloodstream | AoE | AoE, reduces healing received, 30% lifesteal | high |
| 19330000 | 분신 | Phantom | buff | 15s; 15% chance per hit to summon up to 5 clones | high |
| 19350000 | 집중 막기 | Focused Block | buff | Channelled guaranteed block with heavy damage reduction | high |
| 19360000 | 저지 불가 | Unstoppable | buff | HP cannot drop below 10% for 4s | high |
| 19370000 | 전력 | Full Force | AoE | Spends all rage on a large AoE hit | high |
| 19380000 | 강습 타격 | Assault Blow | stun | 20m strike on up to 4 targets, 30% chance of 3s stun | high |
| 19390000 | 권기 방출 | Discharge Energy | AoE | 20m ranged energy burst | high |
| 19400000 | 영역 소환 | Zone Summon | AoE stun | Places a 20s zone; 역행 returns to it, AoE with 50% chance of 3s stun | high |
| 19420000 | 연환권 | Chain Fist | attack | Usable after a crit (always in Berserk), up to 4 targets | high |
| 19440000 | 참영권 | Shadow-Cleaving Fist | gap closer | 20m dash strike (alternative: 환영각, the 질풍격 follow-up pull) | low |
| 19450000 | 충격 해제 | Defiance | CC break | Removes CC, 5s CC immunity | high |
| 19460000 | 순보 | Flash Step | gap closer | Moves behind target within 20m and strikes | high |
| 19710000 | 전선 유지 | Hold the Line | passive | +block, chance to heal on block | high |
| 19720000 | 정면 돌파 | Shatter Frontline | passive | +accuracy, +crit, 25% chance of bonus damage on frontal hits | high |
| 19730000 | 정신 전환 | Mental Conversion | passive | Converts part of max MP into max HP | high |
| 19740000 | 위세 | Dominance | passive | +frontal, PvE and PvP damage amp | high |
| 19750000 | 폭주 증폭 | Rampage Boost | passive | Attack and defense bonuses by Normal or Berserk state | high |
| 19760000 | 충격 적중 | Impact Hit | passive | +shock-hit chance, +heavy hit | high |
| 19770000 | 거대한 분노 | Massive Rage | passive | +max rage, rage on kill, lower Berserk drain | high |
| 19780000 | 충격타 | Impact Blow | passive | Bonus damage vs shocked or groggy targets | high |
| 19790000 | 회생의 계약 | Revitalization Contract | passive | +CC resistance; heals 35% of max HP at 10% HP or below | high |
| 19800000 | 경량화 | Lightweight | passive | Bonus damage vs targets above 50% HP | high |

## Gaps

- The client itself still shows "???" (its string table); our names live server-side. No Brawler weapons past level 1
  (client items); the scaling above stands in. No rage resource.
- Stigma skills unlock by level (OURS), as for every class, until stigmas are built.
- Brawler bots wait for their hand-made PLAN in `tools/bot_rotations.py`.
