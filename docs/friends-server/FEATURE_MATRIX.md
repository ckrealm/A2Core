# Feature matrix (A2Core @ 2b5f72c)

> Built from a read of the actual source, data tables and migrations on 2026-11-08, not from the README. Status means *code exists*; nothing here is verified in-game yet (no client has connected). See TEST_RESULTS.md for what has actually run.


Repo: `/home/user/work/A2Core` (branch `audit/phase1`). Everything below comes from reading the source, data tables and migrations, not the README. Paths are relative to the repo root. Line numbers are from the current tree.

**Status key**
- **Implemented**: works end to end with data behind it.
- **Partial**: works, but with real gaps or invented (OURS) rules.
- **Stub**: answers the client with an empty or fixed reply only.
- **Missing**: no code.
- **Unknown**: can't be confirmed from source alone.

## 0. How the code marks known vs. guessed values (CONTRIBUTING.md)

- `OBSERVED`: seen on the official server.
- `CANDIDATE`: inferred, not confirmed.
- `OURS`: the project's own choice.

There are **no** `TODO`, `FIXME` or `NotImplementedException` markers anywhere in `server/`. Gaps are written up in prose comments instead: "not modelled", "not stored yet", "not tracked" (16 such notes in `Aion.World`).

| Scope | OBSERVED | CANDIDATE | OURS |
|---|---|---|---|
| All of `server/**/*.cs` | 181 | 138 | 52 |
| GameSession.cs (core: login, combat, movement, death, stats) | 31 | 11 | 7 |
| Protocol/Native GamePackets.cs | 36 | 36 | 0 |
| Protocol/Native PlayerPackets.cs (other-player messages) | 35 | 9 | 0 |
| Protocol/Native SocialPackets.cs (party/trade/duel/friends) | 6 | 12 | 0 |
| Protocol/Native RidePackets.cs | 8 | 4 | 0 |
| GameSession.MapEvents.cs | 10 | 5 | 2 |
| GameSession.PartyDungeons.cs | 7 | 1 | 1 |
| NpcAi.cs | 5 | 2 | 6 |
| GameSession.Gather.cs | 0 | 1 | 5 |
| GameSession.Craft.cs | 2 | 2 | 3 |
| GameSession.Achievements.cs | 2 | 3 | 4 |
| GameSession.Experience.cs | 0 | 0 | 3 |
| GameSession.ClientRequests.cs | 2 | 7 | 0 |
| GameSession.Social.cs | 0 | 1 | 2 |
| GameSession.Guild.cs / Arena.cs / Abyss.cs | 0/0/0 | 1/1/0 | 0/1/1 |
| Combat.cs | 4 | 1 | 2 |

Takeaway: the wire protocol for login, movement, combat and other players' messages is mostly OBSERVED. Social, guild, arena, mail, loot, shop and gather layouts are mostly CANDIDATE: built from usmap struct names and never seen in an official capture. `docs/CAPTURE.md:87-125` lists exactly these as "still wanted". **Game rules** (XP per kill, respawn time, leash, regen, party split, deletion rules) are mostly OURS.

---

## 1. Accounts, login, characters, lobby

| System | Status | Evidence | Notes |
|---|---|---|---|
| Account register/login/logout, sessions | Implemented | `server/Aion.Host/Authentication/AccountEndpoints.cs:24-66`; `Aion.Infrastructure/Postgres/PostgresAccountAuthentication.cs`; migrations 003, 006 | Web account site on port 8780. Registration toggled by `Aion:Accounts:Registration`. Password change and session revoke at `GameplayEndpoints.cs:37-39`. |
| Staff roles, account disable | Implemented | `GameplayEndpoints.cs:111-114`; migration 009 `auth.account_roles` | Roles `admin` and `gamemaster`. Can also be set from the CLI with `tools/postgres_dev.py role`. |
| Launcher handoff / game tickets | Implemented | `AccountEndpoints.cs:16,83`; migrations 004, 011, 022 | The login port (3700) checks launch tickets: `NativeListeners.cs:358-368`. |
| Native login server (0x39xx) | Implemented | `server/Aion.World/LoginSession.cs`; `Protocol/Native/LoginPackets.cs` (1 CANDIDATE) | Has a world/race picker (`WorldChoices.cs`). |
| Character name check / create | Implemented | `GameSession.cs:270-282` (0x3619, 0x361b), `CreateAsync` at `GameSession.cs:1555` | Staff-style names are banned for players. Duplicate names are checked. One lobby slot per race, then per account (migrations 037/038). |
| Character select / load | Implemented | `SelectAsync` at `GameSession.cs:1589`, `LoadComplete` at `:1682` | Loads quests, social, guild, settings, skills and wings. |
| Character delete / cancel / rename | Partial | `GameSession.Lobby.cs:7-17` | Layouts are CANDIDATE. Deletion rule is OURS: immediate below level 20, otherwise after 24 h. |
| Save | Partial | `CheckpointAsync` at `GameSession.cs:2938-2953` (every 30 s and on logout or lobby) | The checkpoint saves **position only**. Level/XP, items, quests and so on are written per action. HP/MP aren't persisted. The equipped wing and hotbar context skills are "not stored yet" (`GameSession.cs:343`, `ClientRequests.cs:58`). |
| Lobby (back to character select) | Implemented | `LobbyAsync` at `GameSession.cs:509-519` | The plain (uncompressed) list was settled by live testing. |
| Development login mode | Implemented | `Aion:Native:Logins=development` (`NativeListeners.cs:25,52`) | Unsaved characters, no DB. Used by the checks and useful for bots (see §9). |

## 2. Character progression

| System | Status | Evidence | Notes |
|---|---|---|---|
| Inventory (bag, move, sort, remove, restore) | Implemented | `GameSession.cs:1238-1415`; `GameSession.Bag.cs`; migrations 005, 034 | Items are DB rows (`game.item_instances`, `item_stacks`). |
| Warehouse / storage | Partial | `GameSession.Bag.cs:35-45` | Per character (OURS). Whether it's account-wide is unknown (CAPTURE.md item 15). |
| Equipment equip/unequip | Implemented | `EquipRequestAsync` at `GameSession.cs:1329`, live opcodes 0xe247/0xe249 at `:1327` | Uses `game.equipment_rules`. Includes soul-binding (0x562b/2d/2f). |
| Enchant, decompose, skins, titles, wings, rides | Partial | `GameSession.Enchant.cs`, `.Decompose.cs`, `.Skins.cs`, `.Titles.cs`, `.Rides.cs` | Data-driven (enchant.json 3535 items, decompose.json, skins.json, titles.json 1504). Results and costs are CANDIDATE (CAPTURE.md items 13, 18, 19). |
| Leveling / XP | Partial | `GameSession.Experience.cs:15-46` | Exp curve is data (levels.json, 100 levels). **Kill XP is OURS**: NeedExp(mob level)/25 × a level-gap factor. Party share is an even split among nearby members (OURS). Quest XP comes from quest data. |
| Stats | Partial | `StatSheet` at `GameSession.cs:2275-2322` | HP comes from PcStatLevel plus gear, enchant, ride, buffs, board and title. MPMax is a flat 1500 for every class and level (CANDIDATE, `:2269`). Every other stat is a constant from the official level-1 sample (`GamePackets.DefaultStats`). |
| Combat power | Missing | `PlayerPackets.cs:84` `CombatPower` field is always left at default | Never computed. |
| Skills learned by level | Partial | `GameData.LearnedSkills` `GameData.cs:272`; class-skills.json | **Unlock data stops at character level 25 for every class**, while max level is 100. |
| Skill levels / skill points | Implemented | `GameSession.SkillLevels.cs` (OBSERVED flow) | Points from Exp.dat (CANDIDATE). **Skill level doesn't change damage**: `Combat.SkillHits` (`Combat.cs:90`) ignores it. |
| Passives | Partial | `PassiveAbnormals` at `GameSession.Buffs.cs:242` | Passive abnormals are sent (needed for weapon equip). Only the stats the server models apply. |
| Stigmas | Partial (built, not tested in a client) | `GameSession.Stigmas.cs`, `tools/data/stigmas.json`, migration 043 | Global rules: 4 slots at 22/27/32/37, Stigma Shards from levelling, levels to 20, free reset, `//stigma`, bots slot their own. Stigma list and messages are CANDIDATE; specialties not modelled. docs/STIGMAS.md |
| Daevanion board | Stub | `GameSession.EntryConstants.cs:21-22` sends five zero point types and an empty board | — |
| Creature boards | Implemented | `GameSession.Board.cs`, migration 020 | — |
| Ascension | Implemented | `Ascend` at `GameSession.cs:1052`, migration 010 | — |

## 3. World, NPCs, quests, economy

| System | Status | Evidence | Notes |
|---|---|---|---|
| Maps / zone transitions / teleport | Implemented | `Teleport` at `GameSession.cs:2423`; `Kibelisks.cs` (teleport artifacts); map-events `teleports`; MoveMap/Teleport GM commands | 739 maps with NPC data. Unknown map ids are refused (they would log the client out). |
| Movement, sprint, dodge, fly, glide, swim, dive | Implemented (heavily OBSERVED) | `GameSession.cs:298-330`, `:1094-1237`, `:2620-2760`; `GameSession.Movement.cs` speed check | The dodge-while-sprinting reply is still being bisected with live variants (`DashVariant`, `GameSession.cs:1913-2010`). |
| **Shared world: players see each other** | Implemented | `SharedWorld.cs` (whole file); `GameSession.cs:2505 EnterSharedWorld`, `:327` movement broadcast; checks at `tests/Aion.Native.Checks/Program.cs:1858-1900` | One `MapInstance` per map: shared NPCs, AI and respawns. Players appear within 8000 cm (`SharedWorld.cs:19`). Movement, rolls, parkour, casts, levels, rest and buffs are relayed. Unique player keys start at 100000. A world lock lets maps run in parallel (`WorldLock.cs`). |
| NPC / monster spawning | Implemented | `Combat.MapTargets` `Combat.cs:166-190`; world-npcs.json (82,547 spawns on 707 maps, 9,120 templates) | HP comes from OBSERVED per-level curves and rank units (`Combat.cs:110-150`). Respawn is a flat 30 s for everything (OURS, `GameSession.cs:49`). Dungeon mobs don't respawn. |
| NPC AI / aggro | Implemented / Partial | `NpcAi.cs` (1002 lines): wander, patrol routes, aggro shapes (NpcAggroData), chase on navmesh/terrain, leash 3000 (OURS, `:25`), hate table (OURS, `:92`), skill cooldowns, scripted walk/follow, companion NPCs that fight (`:171-210`, `:684-690`) | NPC skills are single-target damage only (`Resolve` `:636-655`): no AoE, heals, buffs or telegraphs. NPC damage = level HP × 7.5% × rank scale (OBSERVED fit, `:28-31`, `:620-628`). |
| Quests | Implemented / Partial | `Quests.cs`; `GameSession.cs:544-1052`; quests.json (1,429 quests) | Objective types used in the data: KillNpc 1132, AskNpc 587, ClearMapEvent 524, UseEnvObj 350, EnterVolumePC 148, CompleteCelviceTalk 92, and others. `ClearQuestExplorationType` (4 uses) has no handler. Auto-acquire quests are supported. The client request layouts for accept/abandon (0x8d0a/0x8d0c) were never recorded and are guessed (`GameSession.cs:462-467`). |
| Map events (scripted layers and dungeons) | Implemented / Partial | `MapEvents.cs`, `GameSession.MapEvents.cs` (863 lines); map-events.json (1,083 events, 730 served maps) | About 45 directive types are supported. **Objectives the server can't track** (ClearWave, ClearBoardPuzzle, CollectItem, GetAbnormal, …) **auto-complete after a grace period** (`MapEvents.cs:60-62`). Non-admins can't enter unserved maps (`GameSession.MapEvents.cs:108`). |
| Vendors / NPC shops | Partial | `GameSession.Shop.cs`; shops.json (127 merchants, 51 shops, 987 goods, 4,067 sell prices); migration 025 | Buy, sell, buyback and purchase limits with the official reset time. Reply layouts are CANDIDATE (CAPTURE.md item 4). The coin shop isn't served (`ClientRequests.cs:54`). |
| Gathering | Partial | `GameSession.Gather.cs:5-14` | Nodes come from world-objects.json. Mastery XP, bonus rate and respawn are OURS. The gather skill tree isn't modelled. |
| Crafting | Partial | `GameSession.Craft.cs`; crafts.json (1,098 recipes) | Craft XP is OURS (CAPTURE.md item 18). |
| Loot | Partial | `GameSession.Loot.cs`, `LootTables.cs`; loot.json | Ground drops are **owned by the killer only**; they expire after 60 s (`Loot.cs:17`). Only **1,385 of 5,988 monster templates (≈23%) have loot tables**, and prologue mobs have none. Roll reading is CANDIDATE. |
| Currency (Kina) | Partial | Kina is an item: `KinaItemId = 930100001`, `GameSession.cs:547` | Kina comes from quest rewards (`:1016`), selling, mail and staff rewards. **No loot table contains Kina** (0 hits in loot.json). |
| Mail | Partial | `GameSession.Mail.cs`; migration 030 | Load, read, delete and take attachments. Staff/GM mail is supported. Player-to-player sending isn't visible here (only system/staff posts). |
| Achievements, collections, attendance, battle pass | Partial | `.Achievements.cs`, `.Collections.cs`, `.BattlePass.cs` | Data is large (6,346 achievements). Battle-pass purchase always fails (`GameSession.cs:444`). |

## 4. Combat

| System | Status | Evidence | Notes |
|---|---|---|---|
| Classes supported | Partial | class-skills.json `classes`: Gladiator, Templar, Assassin, Cleric, Ranger, Elementalist, Sorcerer, Chanter (plus Novice/Test/Tutorial) | 25–31 unlocks per class, all at **level ≤ 25**. Of 260 skills, only **89 have damage rows**. The skill schema has no heal/buff/debuff fields at all (keys: name, icon, type, target, weapons, cooldown_ms, casting_ms, cost, damage). |
| Cast pipeline (cost, cooldown, weapon requirement, learned check) | Implemented | `CastReplies` `GameSession.cs:1913-2026` | Cooldowns are per skill. `NoCool` GM flag. |
| Damage formula | Partial (CANDIDATE) | `Combat.cs:84-110` | raw = flat + (weapon roll + FixingDamage) × ratio / 10000. Crit = weapon Critical/1000, capped at 50%, ×1.5. Damage = raw × 1000/(1000 + def). PvP uses the PvP rows from Skill.dat. The source itself says this is "not recovered retail formulas". |
| Defense | Partial | `Defense` `GameSession.cs:2284` | PcStatLevel Defense plus armour, enchant, buffs, board and titles. No other mitigation stats (block, parry, magic resist). |
| AoE | Partial | `CombatReplies` `GameSession.cs:2057-2100` | A self-centred cast hits hostiles within 400 cm (`AreaRange`, `:47`). There are no cone or line shapes. |
| Buffs / debuffs | Partial | `GameSession.Buffs.cs` | **Item buffs only** (potions, food, scrolls, polymorph, rebirth) plus map-event abnormals. Class skill buffs and debuffs aren't applied. Players can't stun, slow or DoT monsters. |
| Healing | Partial | Potions (`GameSession.Rides.cs:262-266`), HoT items (`Buffs.cs:105-157`), regen 2%/s out of combat (OURS, `GameSession.cs:2840-2846`) | **Heal skills (Cleric/Chanter) do nothing**: `CombatReplies` only damages hostile NPCs and returns early when a skill has no damage rows (`:2067`). |
| Resurrection | Partial | `Die`/`RespawnRequestAsync`/`UseRebirth`/`Revive` `GameSession.cs:2193-2265` | Respawn point, Spiritstone revive in place, and auto-revive after 60 s (OURS). **No player-cast resurrection.** |
| Boss mechanics | Partial | Driven only by map-event directives: StartSpawner, Stun, ImmuneSkillEffectGroup, AddAbnormal, SetAggroNpc, HP triggers (`MapEventHp`), DungeonFail | No boss AI scripting in code: no phases, enrage, AoE telegraphs or adds beyond what the client's MapEvent data scripts. `MapEvents.cs:602`: "RemoveFromAggroList, HpRemaining: not tracked". |
| Party combat | Partial | Hate table is shared per map NPC (`NpcAi.Engage` `:394-403`); XP split (`Experience.cs:39-43`) | No heals or buffs on allies, no taunt mechanics beyond damage-based hate. |
| Duels | Partial | `GameSession.Social.cs:500-560` `DuelHits` | Layouts are CANDIDATE. Countdown 3 s, limit 120 s (`Social.cs:36`). |

## 5. Group content

| System | Status | Evidence | Notes |
|---|---|---|---|
| Parties | Partial | `Social.cs:5-16`; `GameSession.Social.cs:49-56` (create, invite, accept, leave, kick, set leader, disband) | **In memory only**, lost on restart. Size is a hard-coded 4 (`Social.cs:8`, CANDIDATE). Recent-members list is empty. All layouts CANDIDATE. |
| Dungeons / instances | Partial | `GameSession.PartyDungeons.cs`, `GameSession.Instances.cs`, `SharedWorld.Map(mapId, instance)` `SharedWorld.cs:31-38` | Private copies shared by the party. 733 dungeon definitions (InstanceLayer 220, Seal 162, Quest 103, FieldEvent 84, Party 55, Awaken 38, Matching 23, Raid 4, …). Entry rules: min/max level, item level, member cap, quest gate (`PartyDungeons.cs:87-104`). Time limits are supported. `instances.json` holds **one** hand-entered instance layer. |
| Dungeon rewards | Partial | Reward cube (`CubeRewardRq`, `PartyDungeons.cs:217`) | The final-boss cube plus mid-boss rewards. The cube's 20 common tickets and ceiling aren't modelled. |
| Loot distribution | Missing | `GameSession.Loot.cs:5-7` | No round-robin, need/greed or party-owned drops. Only the killer gets drops. |
| Entry limits / energy / tickets / cooldowns | Missing | `GameSession.PartyDungeons.cs:13-14`: "Not modelled: entry tickets, the weekly currency limit, the cube's ceiling, DungeonCoolTimes, DeathLimit" | Unlimited runs. |
| Boss challenge | Partial | `GameSession.BossChallenge.cs`; bosschallenge.json (one dungeon, 26 groups) | Tickets and grade multipliers aren't modelled (`:52`). |

## 6. Social / PvP

| System | Status | Evidence | Notes |
|---|---|---|---|
| Legions (guilds) | Partial | `GameSession.Guild.cs` (create, info, members, search, join, leave, kick, disband); migration 026 | Creation costs 100,000 Kina. Joins of type Manual/AutoDeny are refused (no officer confirmation, `:10`). No guild chat room membership (`ChatRelay.cs:17`). |
| Friends / block | Implemented (CANDIDATE layouts) | `GameSession.Social.cs:41-47`, migration 015 | — |
| Trade | Partial | `GameSession.Social.cs:57-62` | — |
| Chat | Partial | `Aion.Application/Chat/ChatRelay.cs:10-27`; `Aion.Host/Chat/LimeChatEndpoints.cs` (Lime HTTP + STOMP) | World, region and shout rooms plus whispers. **Party, guild and race rooms aren't joinable** (no membership checks). Rate limit 5 per 5 s. Mutes and a chat log exist. |
| PvP (open world) | Partial | `GameSession.Abyss.cs:30-45` `FactionHits` | Only on Abyss maps 20/22/23, opposite race only. Gives 100 Abyss Points per kill (OURS). |
| Arena | Partial | `GameSession.Arena.cs:30-50` | Matchmaking queue, 1v1 and 5v5, lives, score, result. The flow is OURS from message names. Ranking, penalties and schedules aren't modelled. |
| Abyss | Partial | `GameSession.Abyss.cs:5-11` | Entry via portal NPCs and faction PvP. Ranks, fortresses, artifacts and rifts aren't modelled. AbyssArtifactEventInfo returns empty (`ClientRequests.cs:46`). |

## 7. Staff tools

### In-game builder (GM) commands
The client console sends `s <cmd>` (0x362e); in chat it's `//<cmd>`.

- **Where**: `server/Aion.World/BuilderCommands.cs:23-26`.
- **Rights**: a staff-flagged character on an account with a role, or `DevelopmentGmNames` in dev mode (`:72-82`). Every command is audited.
- **Supported (32)**: SetInvisible, SetInvincible, SetInvincibleHP, NoCool, DashVariant, Hp, Mp, Sp, FixHpPercent, Teleport, TeleportTo, MoveMap, LayerTest, Dev, AcquireItem, SetLevel, ClassChange, SetAscension, ContentsUnlock, ContentsUnlockAll, AcquireWingAll, QuestAdd, QuestComplete, QuestCompleteStep, QuestReward, QuestDelete, RevealMap, AcquireTitle, AcquireSkin, SetMastery, Mail, EnterDungeon.
- **For every player**: `//report <text>` opens a ticket (`GmCommands.cs:83-91`); `//help`.
- **Coverage**: 32 of the client's 656 BuilderCommand.json commands.
- **Missing commands**: no spawn-NPC, kill or heal-target, buff, or rate commands in-game. The dashboard covers spawning.

### Admin dashboard (`web/src/admin/*.tsx`, about 2.5k lines; APIs in `server/Aion.Host/Authentication/*Endpoints.cs`)

| Tab | Features | API evidence |
|---|---|---|
| Live map / WorldMap / Monsters | Players online on map images; list, respawn, clear, spawn and remove staff-spawned monsters | `StaffEndpoints.cs:159-166`; `StaffToolsEndpoints.cs:182-231`; `GameplayEndpoints.cs:75,103` |
| Characters | Search and view; commands grant, level, teleport, class, ascension, wings, staff flag | `GameplayEndpoints.cs:54-69`; `PostgresGameplayStore.cs:1317` |
| Moderation | Sanctions (ban/mute), chat log search, tickets | `StaffToolsEndpoints.cs:42-66` |
| Announcements / Broadcast | Announce, message a player, kick, restore, unstuck, teleport | `StaffEndpoints.cs:37-96` |
| Events | Scheduled `announce`, `exp` multiplier, `drop` multiplier | `StaffToolsEndpoints.cs:84-95`; `ServerControl.cs:84-96`; `web/src/admin/Events.tsx` |
| Rewards | Mass item rewards via mail | `StaffToolsEndpoints.cs:125` |
| Economy | Economy overview | `StaffToolsEndpoints.cs:106` |
| Activity log | Audit events | `StaffEndpoints.cs:115` |
| Server | Status, maintenance mode, scheduled restart, backups | `StaffEndpoints.cs:108-135`; `StaffToolsEndpoints.cs:68-80, 241-272` |
| New account | Create accounts | `AccountEndpoints.cs:34` |

## 8. Configurability

**Where config lives:**
- **ASP.NET configuration**: environment variables in `/etc/aion/aion.env`, generated by `tools/server_env.py`, called from `deploy/linux/install.sh:119`.
- **No `appsettings*.json`** is committed.
- **DB**: `game.server_settings` holds only the key `maintenance` (`PostgresStaffTools.cs:76-93`). `game.scheduled_events` holds the exp/drop multipliers.

**Config keys read by the host** (`server/Aion.Host/Program.cs`, `Native/NativeListeners.cs:15-52`, `NetworkOptions.cs`):

| Group | Keys |
|---|---|
| Connection strings | `ConnectionStrings:Aion`, `ConnectionStrings:Auth` |
| Network | `Aion:Network:BindAddress`, `Aion:Network:PublicAddress` |
| Native (login/game ports) | `Aion:Native:LoginPort`, `GamePort`, `Logins` (`accounts` or `development`), `WorldId` (1204), `IntroCharacter`, `CharacterLevel`, `DevelopmentStartMap`, `DevelopmentGmNames[]`, `TerrainDirectory`, `NavMeshDirectory`, `PreloadMaps[]`, `TraceDirectory`, **`MaxConnections` (default 16)**, `IdleTimeout` (1 h) |
| Chat | `Aion:ChatPort`, `Aion:ChatServerKey`, `Aion:ChatDevelopmentLogins` |
| Platform bootstrap and NCGuard | `Aion:Platform:*` (Port, FixtureDirectory, PolicyFile, CustomData…, GeolocationFile); `Aion:NcGuard:*` (Port, FixtureFile, DataDirectory, Capture) |
| Web and HTTPS | `Aion:LocalPort` (8780), `Aion:Https:CertificateFile`, `KeyFile`, `Proxy`; `Aion:Web:StaffPath`, `Aion:Web:Downloads`; `Aion:WardrobePath`, `Aion:Wardrobe:StaffOnly`; `Aion:MapImagesPath` |
| Accounts, backups, bridge | `Aion:Accounts:Registration`; `Aion:Backups:Directory`, `Tool`, `PgHome`; `Aion:BridgeSecret` |

**Rates and gameplay toggles:**

| Setting | Exists? | Where |
|---|---|---|
| EXP rate | Yes, only as a scheduled event multiplier, clamped to 0.1–10 | `SharedWorld.cs:54`; `Experience.cs:37`; `ServerControl.cs:84-96` |
| Drop rate | Yes, same mechanism (extra rolls) | `SharedWorld.cs:55`; `GameSession.Loot.cs:26-31` |
| Kina rate | **No** | — |
| Max players | Indirectly: `Aion:Native:MaxConnections` (16) | `NativeListeners.cs:42,65` |
| PvP toggle | **No** (PvP is implicit: Abyss maps, duels, arena) | — |
| Dungeon entry limits | **No** (not modelled) | — |
| Party size, respawn time, leash, kill-XP divisor, revive delay | Hard-coded constants | `Social.cs:8`; `GameSession.cs:49,2199`; `NpcAi.cs:25`; `Experience.cs:15` |

There is no permanent rate setting: the only way to get one is an event with no end date.

## 9. Bots / fake players / AI companions

| Item | Status | Evidence / notes |
|---|---|---|
| Playerbots / fake players | **Missing** | No code anywhere (grep for bot/playerbot/fake player finds nothing). |
| AI companions | Partial (NPC-side only) | `NpcAi` has `Mode.Follow` and a `Companion` function: an NPC follows a player, scans for hostiles every 500 ms within 1000 cm, fights, then resumes following (`NpcAi.cs:51-56,171-210,684-690`). Only map events trigger it (`FollowPc`/`StopFollowPc`, e.g. Robin in the HQ001 layer). Companions use NPC skills (single-target damage) and look like NPCs, not players. |
| Headless GameSession | Feasible today | `GameSession` is transport-free: `HandleAsync(byte[] plain, now)` and `TickAsync(now)`. The checks already run several sessions in one `SharedWorld` with no socket (`tests/Aion.Native.Checks/Program.cs:1858-1900`): they log in, create, select, load-complete, move and cast by building client packets with `PacketWriter`. Other players then see a real player (0x3645 CharAppear) with relayed movement and casts. |

**Bot design paths:**

1. **Headless-session bots** (closest to mod-playerbots). A `BotDriver` owns a `GameSession` bound to the host's `SharedWorld` plus a store character, drains `WorldPlayer.Outbox`, and feeds synthetic requests:
   - 0x3701 move updates along `NavMesh` paths (`NavMesh.cs` already pathfinds for NPCs);
   - 0x3800 casts built per `CastRequest` (`GameSession.cs:1882-1911`);
   - 0x3833 target selection;
   - party invite answers.

   A rotation engine picks skills from `learned`, `cooldowns` and resources. Bot rotations would also need ally-target skills to *do* something, which means first implementing heal/buff skill effects (§4). Needs: a `Store` for a stored bot character (or dev-mode in-memory characters), a session tick loop (today it's driven per connection in `NativeListeners.cs:320-410`), and the `WorldPresence` entry.
2. **NPC-side companions**: extend `NpcAi.Brain` (hate, follow, `CompanionPrey`) with a role and rotation table, and spawn them per player via a new staff/party command. Simpler, but they'd appear as NPCs and need player-appearance packets to look like players.

## 10. Unimplemented / unanswered client opcodes (for a "missing handlers" report)

- **Dispatch**: `GameSession.HandleAsync` (`GameSession.cs:244-502`) tries, in order:
  1. a fixed switch;
  2. `ClientRequest` (`GameSession.ClientRequests.cs:32-80`, small canned replies);
  3. guild, social and equip handlers;
  4. movement;
  5. the main switch;
  6. `GamePackets.WorldEntryQueryReplies` (fixed replies, `:499-500`).

  Anything else **falls through to `return []` at `GameSession.cs:501`**.
- **Logging** (`server/Aion.Host/Native/NativeListeners.cs:376-395`), once per opcode per connection, at Information level:
  - `"Native game unanswered opcode 0x{Opcode:x4} ({Length} bytes)"`: any request with zero replies that isn't a position report and isn't in `GameSession.SilentRequests` (`ClientRequests.cs:23`: 0x3601, 0x3651, 0xe34f, 0xe350, 0xe259, 0xffa0, 0xffa1).
  - `"Native game quest/NPC request 0x…: <hex>"`: full bodies for the quest, NPC, dungeon (0x9000-0x91ff), legion (0x8a00-0x8a40), 0x8d30-0x8d3f and glide/wind ranges whose layouts are unknown (`:384-386`).
  - `"Native game social/object request 0x… -> N replies: <hex>"`: first party, trade, duel or friend request of each kind (`:388-390`).
  - Full BuilderCmd_RQ bodies (`:391-393`).
- **Per-session trace**: optional full client trace files via `Aion:Native:TraceDirectory` (`ClientTrace`, written every minute).
- **How to build the report**: `journalctl -u aion-host | grep -E "unanswered opcode|quest/NPC request|social/object request"`, then map opcodes with `tools/data/opcode-names.json`. That file is small: 219 names, 61 of them client requests, all referenced in source. The full ledger is in `research/local/tooling/opcodes.json` (git-ignored, not in this checkout).
- **Known stubbed replies** (answered with "nothing" or "fail"):
  - CoinShopOpen → fail;
  - BattlePassPurchase → fail;
  - ArenaRecord → zeros;
  - SeasonRanking, PushRewardInfo, AbyssArtifactEventInfo, SkinDecoInstanceList, MessageCardBlockList → empty;
  - PartyRecentMember → empty;
  - Daevanion → zeros;
  - 0xe259 NCGuard deliberately unanswered.

## 11. Data tables (tools/data) — shape and coverage

| File | Size | Content / coverage note |
|---|---|---|
| world-npcs.json | 12.6 MB | 739 maps, 82,547 spawns, 9,120 templates, 3,904 NPC skills |
| world-objects.json | 11.1 MB | 697 maps |
| map-events.json | 4.3 MB | 1,083 events, 733 dungeons, 730 served maps |
| achievements.json | 1.6 MB | 6,346 achievements |
| quests.json | 1.4 MB | 1,429 quests |
| loot.json | 0.5 MB | 1,496 NPCs, 1,638 tables. Covers only about 23% of monster templates; no Kina. |
| items.json | 366 KB | 3,555 items |
| class-skills.json | 265 KB | 260 skills, 89 with damage. Unlocks only to level 25; no heal/buff effect data. |
| shops.json | 127 KB | 127 merchants |
| crafts.json | 114 KB | 1,098 recipes |
| instances.json | 714 B | **1 hand-entered instance layer** |
| npc-social-actions.json | 829 B | 1 map |
| levels.json | 2.4 KB | 100 levels |
| teleport-artifacts.json | — | 15 maps |
| bosschallenge.json | — | One dungeon |
| opcode-names.json | — | 219 names |

DB migrations 001–038 create about 50 tables across the `auth`, `game`, `catalog` and `audit` schemas. Notable absences: there are no tables for parties, dungeon entry counts, mail sending between players or server-wide rates.

---

## Top gaps for a 5–10 friend PvE server

1. **Support classes don't work.** Heal, buff, debuff and resurrection skills have no effect: there's no effect data in class-skills.json and `CombatReplies` only damages hostile NPCs. Clerics and Chanters can't heal or buff the party. This is the biggest blocker for group PvE and for bot rotations.
2. **Skill progression stops at level 25.** Class unlock data ends at 25 while the level cap is 100, and skill level doesn't scale damage.
3. **Loot.** Drops are killer-only with no party distribution. Only about 23% of monster templates have loot tables, and there's no Kina from mobs.
4. **Kill XP and party XP are invented (OURS).** There's no permanent rate config: EXP and drop rates only exist as scheduled events capped at 10×, and there's no Kina rate. Add real config keys.
5. **Dungeons.** Only the client-scripted mechanics run. Objectives the server can't track (waves, puzzles, collect) auto-complete. There are no entry limits, tickets or cooldowns, and boss fights rely on single-target NPC skills (no AoE or telegraphs).
6. **Parties are in memory and capped at 4.** Party and guild chat rooms aren't joinable.
7. **Stats are mostly constants.** MPMax is a flat 1500, combat power is 0, and the damage and defense formulas are CANDIDATE.
8. **Small gaps:**
   - The default `MaxConnections` is 16, which is fine for 10 friends.
   - HP/MP and the equipped wing aren't persisted.
   - The respawn timer is a flat 30 s.
   - In-game GM commands lack spawn, kill and heal; use the dashboard for those.
9. **Bots.** Nothing exists, but headless `GameSession`s in the `SharedWorld` (as the checks already do) are a clean base for playerbots. They first need gap 1 (support-skill effects) and a server-side tick and driver for sessions without a socket.
