# Changelog

## 2026-11-09: stigmas
- **Stigmas, as on global** (`Aion:Stigmas:*`, docs/STIGMAS.md). Stigma skills are bought with Stigma Shards from level 22, not handed out by level. 4 slots open at 22, 27, 32 and 37 (`Slots=6` for Korea's count). Shards come from levelling (30 by 45; `ShardRate` multiplies them). Levels go to 20 at 1/2/4/8 shards a level. Reset is free. A stigma casts only from a stigma slot. `/stigma` shows shards and slots, and playerbots slot their own. Stigma lists for the 8 classes and the Brawler (`Fighter`) come from `tools/stigmas.py` (CANDIDATE). Migration 043 stores the bought levels. Native, persistence and Python checks added. Not tested in a real client yet.

## 2026-11-08
- Cloned upstream A2Core `2b5f72c` (AGPL-3.0). Branch `audit/phase1` created as the checkpoint; no source changes yet.
- Built the server and ran the full test suite on Linux (see TEST_RESULTS.md).
- Set up a local PostgreSQL cluster, applied 38 migrations, started the host, verified account register/login.
- Wrote FEATURE_MATRIX, SERVER_AUDIT, TEST_RESULTS, KNOWN_ISSUES, ROADMAP, HANDOFF, README.
- Started `feat/server-rates` and `feat/diagnostics`.

## 2026-11-08 (later) — branch `dev`
- **Configurable rates and rules** (`Aion:Rates:*`, `Aion:Rules:*`, `Aion:Native:MaxPlayers`): official/accelerated profile (accelerated = 5× XP, 3× Kinah, 3× drops), per-rate overrides, quest XP rate, PvP on/off, monster respawn seconds, player cap. Startup log shows the effective values. `tools/server_env.py` flags; optional `/etc/aion/game.env` survives updates. Party size stays 4 (the client is told the size in a guessed field).
- **Diagnostics**: staff-only `GET /api/admin/diagnostics` (uptime, CPU, memory, sessions, DB reachability/latency, login success/fail/refused, error counts by source, unanswered opcodes) and a card on the dashboard Server page. `docs/DIAGNOSTICS.md` has log greps.
- **Test fixes**: NCGuard sink certs now carry key identifiers (Python 3.13); stale auth checks updated; login-window tests no longer need tkinter.

## 2026-11-08 (later) — party loot, heals/buffs merged into `dev` (48ea407)
- **Party loot** (`Aion:Rules:PartyLoot` = roundrobin (default) | freeforall | killer): drops from party kills are shared with members in range (same rule as XP share, ~100 m). Killer's share stays on the ground; others' go straight to their bags.
- **Monster Kinah** (OURS table `tools/data/kinah-drops.json`): scaled by rank (normal ×1, elite ×3, hero ×10) and the Kinah rate, split evenly in the party.
- **Fallback loot** (`Aion:Rules:FallbackLoot`, default on): monsters with no loot table can drop level-appropriate equipment from `items.json` (equipment only — the item data has no consumables/materials).
- **Class skill effects** (OURS table `tools/data/skill-effects.json`): Cleric/Chanter heals and heal-over-time, Defiance and Wish of Concentration buffs, Debilitating Mark / Bittercold Wind debuffs on monsters, Chain of Torment DoT, resurrection support. Heals reach allies' sessions in the shared world. The client shows HP bars move but no buff icons or heal numbers (those messages haven't been recorded). Divine Aura left out (unknown effect).

## 2026-11-08 (later) — Windows hosting merged into `dev` (c016dfa)
- `deploy/windows/`: README (WSL2 Ubuntu 24.04 path recommended, native path alternative), `setup-wsl.ps1` (WSL + Ubuntu, mirrored networking, systemd, optional Tailscale-only firewall rules and startup task, `-WhatIf`), `renew-cert.ps1` (`tailscale cert` → server, weekly), `wsl-install.sh`.
- Nightly verified backups: `deploy/linux/backup.sh` + systemd timer (04:30, keeps 14), `restore.sh` with mandatory test-restore first.
- Fixes: install/update scripts lacked the exec bit; `update.sh` pruning would have deleted nightly backups; Node 22 needed for the web build on Ubuntu 24.04.
- Not run on real Windows yet.

## 2026-11-08 (later) — Playerbots merged into `dev`
- `//bot add <character>` brings one of your own other characters in as a bot: visible to everyone as a normal player, joins your party, follows on the navmesh, assists with a per-class rotation (`tools/data/bot-rotations.json`, OURS, 8 classes). Commands: add/remove/list/follow/stay/assist/passive/role. Config `Aion:Bots:*` (off by default; `PlayersMayUse`, `MaxPerAccount` 3, `MaxTotal` 20). Bots save as normal characters. 32 checks.
- Not yet: bot healing/buffing, self-defence, tank threat, looting, following into instances.

## 2026-11-09 — progression merged; pushed to the fork
- Skills unlock to level 100 (OURS spread of the client's dev-menu class kits over 26–100), +10% damage/heal per skill level, fallback damage rows for skills without data, MP max by level and class, combat power computed and sent in the existing appear-message field. 17 checks. See PROGRESSION.md.
- `dev` pushed to `BrandonGWoW/A2Core` as branch `friends-server`.

## 2026-11-09 — Market and market bot (`//ah`)
- **Auction house in chat**: `//ah search|list|more|buy|sell|mine|cancel|price` for every player with a stored character. Listed items wait in escrow (enchants kept), buyers pay Kinah from the bag, sellers are paid by mail minus `SaleTaxPercent` (5%), unsold items return after `ListingHours` (48). Per world unless `CrossWorld`.
- **Seller bot** ("Market Trader", like `mod-ah-bot`): keeps `TargetListings` (250) listings of crafting materials and crafted items (equipment optional, level range, include/exclude, fixed prices) at 3× their vendor value ±20%.
- **Buyer bot**: buys player listings priced up to 2× vendor value after 10 minutes, by chance, a few per cycle, at most 1,000,000 Kinah a day. It always pays less than the seller bot's lowest price and NPC merchants' prices, so nothing can be resold to it at a profit.
- Config `Aion:Market:*` (all on by default), migration `040_market.sql`, `docs/MARKET.md`. 78 checks (`tests/Aion.Market.Checks`, `tools/check_market.py`).
- Not yet: the client's own Trade Post window (its browse protocol is not captured).
- **Reference prices** (`Aion:Market:PriceFile`, CSV or JSON): the bots follow a price table (e.g. official NA East Siel prices) instead of vendor values; the buyer pays at most 80% of the reference, so overpriced listings stay unsold. No licensed live feed exists, so the table is filled by hand (`deploy/market-prices.example.csv`).

## 2026-11-09 — dungeons opened to match global
- **Entry levels** (`Aion:Dungeons:LevelGates` = global (default) | client): the client's placeholder level 99 on every Expedition hard mode, the Transcendence entries, Ascension Trial Insane/Hell/Doom and two daily dungeons becomes global's entry level (`GlobalLevel` 45, daily dungeons `GlobalDailyLevel` 30). Item levels and quest steps unchanged.
- **Korea-only content** (`Aion:Dungeons:KoreaOnly`, default off): Dramata's Nest, Cradle of Nihility, Hall of Illusion, Azure Breath Isle, Fallen Daeva Citadel, Sunken Temple, Mirror Heart, Fafnite Gully and the raids Corroded Decontamination Facility and Chalice of Muspel are closed, as on global. On, they open at `KoreaOnlyLevel` (45).
- **`/dungeon`** player command: lists the dungeons with difficulties and entry levels; `/dungeon <name> [difficulty]` enters (party shares the copy). Portals still pick Easy (the client's expedition window is unrecorded), so this is how players reach Normal, Hard, Transcendence stages, trial tiers and Ludra. `Aion:Dungeons:Command` turns it off.
- **Hard-mode revives** (`Aion:Dungeons:HardModeRevives`, default 3 as on global): a notice shows revives left; after the last one the revive puts the player outside.
- Not done: the 4-minute boss frenzy (dungeon objectives thread), Ariel Fragments (no such item in client 2.0.6.0), `Dungeon_Daily_0001` (its only event waits for a Prepare phase the dungeon never has, so it can't start).

## 2026-11-09 — Class rotations for dungeon bots (branch `claude/project-thread-8plppl`)
- **Hand-made rotations for all 8 classes** (`tools/bot_rotations.py` plans, from public Aion 2 skill pages and class guides): interrupts first, gap closers, combo chains (Mocking Blade → Overhead Slam, Shield Smite → Annihilate/Judgment, Impactful Crush → Dark Crush ...), big cooldowns on elites and bosses, area skills on packs of 2-3+, debuff/dot upkeep, MP-restoring fillers last. Skills past level 25 are now used too. Heal bots keep 40% MP for heals.
- **New rotation conditions**: `target_casting`, `target_stunned`, `target_slowed`, `target_elite`, `target_boss`, `target_on_self`, `target_effect_missing`, `target_has`, `target_distance_above`, `enemies_at_least`, `after`, `self_mp_below`; entries can be area casts (`area: self|target`); classes have a `position` (melee damage dealers fight from the monster's back) and per-role ranges (the Cleric fights from 18 m, 15 m as a healer).
- **Monster control on the server** (skill-effects.json, OURS amounts): stuns hold ordinary monsters and interrupt a special being wound up (then 3x-duration stun immunity); bosses are only interrupted, and every 3rd stun breaks their stagger (held 5 s). Templar Poach taunts; Templar attacks add extra threat. Snare Shot, Ice Chain and Winter's Shackles slow; Bittercold Wind now roots for 3 s.
- Not done: Elementalist spirits, kiting; our Defiance and Cleric/Chanter heals still differ from the official kits (docs/PLAYERBOTS.md "Not done yet").
- Round 2: bots step out of a boss's area special and hold outside until it lands; a monster on a non-tank bot is brought to the tank; interrupt skills are kept for boss casts; the heal Cleric keeps its HoT on the tank and heals favour the tank; Heart Gore and Drill Dart follow critical hits. Review fixes: area casts now apply their debuffs/stuns/threat to every monster hit, gap closers reach at most 20 m, behind-positioning no longer holds back casts (dps only), stun state is cleared on death and reset.
- Round 3: the server now rolls blocks (weapon Block stat, half damage) and evasions (5-12% by class) on monster hits, and a Dodge evades every hit for 0.5 s; the victim of a monster's special dodges just before it lands. The 8 block/evasion/Dodge proc skills (Ankle Slice, Rush Strike, Debilitating Smash, Shield Rush, Whirlwind Slice, Infiltrate, Heat Wave Blow, Tremor Crush) are back in the plans (`after_block`, `after_evasion`, `after_dodge`). Every class's own Dodge (xx000100) now counts as a dodge. Review notes: a bot held just outside a boss area keeps casting; block uses all equipped gear's Block (as the stats window); the shared-map rolls use Random.Shared; a bot dodges a special aimed at it even while stepping out of another area.
- Checks: 93 playerbot native checks (34 new, in-world), 16 rotation unit tests; all Python tests pass.

## 2026-11-09 — Daevanion boards
- **Daevanion boards** (`GameSession.Daevanion.cs`, `docs/DAEVANION.md`): each class's boards from the client's own tables (Brawler/Fighter included), nodes opened next to opened ones for Daevanion points, closed for Kinah, board reset, open all and open route; stats, skill levels and passives applied; per-character state in `character_settings`; point items (`GetDaevanionPoint`, `Fix_DaevanionPoint`). Config `Aion:Daevanion:*` (PointRate, ExtraPoints, FreeReset, MaxBoardLevel, DisabledBoards, LevelPoints). 20 checks + 3 Python tests.
- The board data (`tools/data/daevanion.json`) is a placeholder until `tools/daevanion_data.py` runs on a client export (needs the archive key from the running game).

## 2026-11-09 — Brawler (branch `claude/project-thread-zvu8x9`)
- **Brawler class** (client class Fighter): kit rebuilt from the client's own SkillAcquireData (levels 1-25) plus the developer kit past 25 (OURS), Light/Dark profiles (OURS creation IDs 45/46, CANDIDATE pc_id 45-48), the Brawler's Fist as starter weapon, gauntlets for Brawlers only. Bots get the Brawler with its hand-made plan in a follow-up. Made by class change (`//ClassChange Fighter`); the creation screen can't offer it. Gauntlets scale with gear (OURS) since the client has only level-1 fists. Needs a live test of the character model: docs/friends-server/BRAWLER.md.
