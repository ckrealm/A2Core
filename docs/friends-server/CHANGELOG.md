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

## 2026-11-09 — fun features (`Aion:Fun`, docs/friends-server/FUN.md)
- **Field bosses**: the 13 open-world field bosses respawn after a random 60–90 minutes (59 named elites: 10–15 minutes) instead of every 30 s. Server-wide notices when a boss is defeated and when it is back. Everyone who fought it rolls its loot.
- **Lucky drops**: equipment drops may come one grade up (Common/Rare → Epic, Epic → Unique) at 2% from normal monsters, 8% from elites and 25% from named elites and bosses. Looted gear may arrive enchanted (25% +1..+3, 5% +4..+6, 1% +7..max).
- **`/buff`** (food, drink and scroll buffs for 30 minutes) and **`/hub`** (teleports to the race's towns and the Abyss) for every player.
- Duels give back the HP and MP they cost (never a free heal).
- All OURS values, all configurable, all on by default. 28 checks.
- Player chat commands work with a single slash (/buff, /hub, /report, /help, /bot, /ah, /boost); GM commands keep //.
