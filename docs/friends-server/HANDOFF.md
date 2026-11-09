# Handoff — current status

_Updated 2026-11-08._

## Where things are
- Upstream A2Core is a serious, working codebase (~25k lines of server C#, 38 migrations, ~1,100 automated checks). It **builds cleanly and starts**; accounts register and log in. That's verified.
- It's **not playable yet** here: no game client has connected, and the server data bundle (catalogue, starter data, terrain, bootstrap keys) isn't loaded.
- Code reading says multiplayer basics exist (shared world, two players see each other, NPC AI, 1,429 quests, shops, crafting, legions), but healing/buffs, high-level skills, party loot and dungeon limits are missing or thin. Details: FEATURE_MATRIX.md.

## Waiting on Brandon
1. Decision on the anti-cheat sink (KNOWN_ISSUES #2).
2. A fork of A2Core on his GitHub account so changes can be pushed.
3. Running the Windows-side setup on his PC (data bundle, client download via the launcher).

## Done since Phase 1 (branch `dev`, local until a fork exists)
- Configurable rates (official/accelerated), player cap, PvP toggle, respawn time — verified live.
- Diagnostics API + dashboard card — verified live.
- Whole test suite green (481 Python, all .NET check programs).

## Next
ROADMAP.md items 3–6, then the PvE core (heals/buffs first, since bots and party play depend on them).
