# Roadmap (ordered)

Each step is small and testable. "Done" means built, tested, and, where it touches the client, verified in-game.

## Now
1. ~~Configurable rates and server knobs~~ — done on `dev` (see CHANGELOG).
2. ~~Diagnostics + stale tests~~ — done on `dev`.
3. ~~Get a fork~~ — done: `BrandonGWoW/A2Core`, branch `friends-server`.
4. **Windows 11 host setup** on Brandon's PC (WSL2 Ubuntu 24.04 recommended, since upstream's scripts target Ubuntu), data bundle, first admin account, Tailscale.
5. **First client connection** with the 2.0.6.0 client: login → character create → enter world → move → kill a mob → log out → log back in (persistence).
6. **Two-player test** over Tailscale: see each other, party, kill together, trade.

## Next (PvE core)
7. ~~Heal/buff/debuff skill effects~~ — done on `dev` (OURS values; client shows no buff icons yet) (needed by Cleric/Chanter and later by bots).
8. ~~Class skill unlocks past level 25; skill-level damage scaling~~ — done on `dev` (OURS).
9. ~~Party loot distribution~~ — done on `dev`. Party loot distribution (round robin / free-for-all / leader), kinah drops, wider loot tables, all scaled by the rate config.
10. Dungeon entry limits / cooldowns (configurable, default off), track common objectives instead of auto-completing.
11. ~~Stats: MP curve, combat power~~ done (OURS); review damage/defense formula against observed data.
12. Party size and persistence; party/legion chat.
13. Automated nightly backups with retention + a tested restore procedure.

## Later
14. **Playerbots** — first slice done on `dev`; healer/tank/defence/loot in progress. (Brandon's request, AzerothCore mod-playerbots style): bots driven as in-process game sessions (no network), following/assisting a player, with per-class rotations from the skill tables. Depends on 7 and 8.
15. Boss mechanics: AoE and telegraphed monster skills.
16. Admin: rate editing in the dashboard, announcements, NPC/item config.
17. Hosting hardware decision from measured CPU/RAM with 5–10 players.
