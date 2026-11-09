# Changelog

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

## 2026-11-09 — one reset clock
- **`Aion:Resets`** (`Hour` 5, `WeeklyDay` Wednesday, `TimeZone` empty = host zone): daily/weekly missions, attendance, the battle pass week, shop purchase limits (daily/weekly/monthly), the period collection book and dungeon entry counts all reset on it. Before, they used UTC midnight with Monday weeks, 07:00 UTC, 24-hour steps from the season start, or 09:00 host time. `Aion:Dungeons:ResetHour` now defaults to the shared hour and still overrides it if set. Daily and weekly missions also start over for players who are online at the reset. Flags `--reset-hour`, `--reset-day`, `--reset-timezone` in `wsl-install.sh` and `tools/server_env.py`. Counts in the current period move one time when this is first deployed. See RESETS.md, which also covers checking WSL's time zone. 16 checks.

## 2026-11-09 — alts: account warehouse and player mail
- **Account warehouse** (`Aion:Alts:SharedWarehouse`, on): the Storage NPC's warehouse (container 2, `kStorage`) is shared by the account's characters on the world; old per-character warehouses join it on load. Moves are locked per account and revision-checked, and the account's other online characters are refreshed, so two sessions of one account cannot duplicate items. Soul-bound items stay out.
- **Player mail** (`Aion:Alts:PlayerMail`, on; `MailFee` 0, `MailDays` 365): `//mail <name> <subject> | <text>`, `//sendkinah <name> <amount>`, `//senditem <name> <bag slot> [count]`. The client has no send request, so these are chat commands; mail arrives in the real mailbox and items keep their upgrades.
- Migration 040, `docs/friends-server/ALTS.md`, checks in Aion.Native.Checks and Aion.Persistence.Checks.

## 2026-11-09 — Market and market bot (`//ah`)
- **Auction house in chat**: `//ah search|list|more|buy|sell|mine|cancel|price` for every player with a stored character. Listed items wait in escrow (enchants kept), buyers pay Kinah from the bag, sellers are paid by mail minus `SaleTaxPercent` (5%), unsold items return after `ListingHours` (48). Per world unless `CrossWorld`.
- **Seller bot** ("Market Trader", like `mod-ah-bot`): keeps `TargetListings` (250) listings of crafting materials and crafted items (equipment optional, level range, include/exclude, fixed prices) at 3× their vendor value ±20%.
- **Buyer bot**: buys player listings priced up to 2× vendor value after 10 minutes, by chance, a few per cycle, at most 1,000,000 Kinah a day. It always pays less than the seller bot's lowest price and NPC merchants' prices, so nothing can be resold to it at a profit.
- Config `Aion:Market:*` (all on by default), migration `040_market.sql`, `docs/MARKET.md`. 78 checks (`tests/Aion.Market.Checks`, `tools/check_market.py`).
- Not yet: the client's own Trade Post window (its browse protocol is not captured).
- **Reference prices** (`Aion:Market:PriceFile`, CSV or JSON): the bots follow a price table (e.g. official NA East Siel prices) instead of vendor values; the buyer pays at most 80% of the reference, so overpriced listings stay unsold. No licensed live feed exists, so the table is filled by hand (`deploy/market-prices.example.csv`).
