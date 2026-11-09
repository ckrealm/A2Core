# Rates, time gates and alt perks (friends server)

Everything here is OURS (a server owner's choice, nothing observed on the official server) and is set in
`/etc/aion/game.env` (or with `wsl-install.sh --rates-only ...` / `tools/server_env.py`). The host logs what it uses at
startup: `journalctl -u aion-host | grep "World rates"`.

## Defaults now

| Setting | Key | Default | Was |
|---|---|---|---|
| Rates profile | `Aion__Rates__Profile` | `friends` | `official` |
| Kill experience | `Aion__Rates__Experience` | 10 | 1 (5 with `accelerated`) |
| Quest experience | `Aion__Rates__QuestExperience` | follows experience (10) | 1 |
| Kinah amount | `Aion__Rates__Kinah` | 10 | 1 (3) |
| Kinah drop chance | `Aion__Rates__KinahChance` | x2 (normal monsters 70% → 100%) | x1 |
| Item drops (loot rolls per kill) | `Aion__Rates__Drop` | 3 | 1 (3) |
| Gathering mastery experience | `Aion__Rates__GatherExperience` | 5 (≈ 2 gathers a level) | 1 (8 gathers a level) |
| Crafting mastery experience | `Aion__Rates__CraftExperience` | 5 (≈ 2 crafts a level) | 1 (8 crafts a level) |
| Pet (creature board) experience | `Aion__Rates__PetExperience` | 5 | 1 |

`Aion__Rates__Profile=official` brings every rate back to 1; `accelerated` is the old 5/3/3. Any single key overrides the
profile. Rates are 0-100.

## Time gates (all off by default)

The audit (2026-11-09) went through every timer, reset and limit in `server/Aion.World`. What gates play by time:

| Gate | Key | Default | Off means |
|---|---|---|---|
| Dungeon daily entry limits | `Aion__Dungeons__EntryLimits` | off (already) | enter as often as you like |
| Shop purchase caps (daily, weekly, monthly, one-time) | `Aion__TimeGates__ShopLimits` | off | limited goods can be bought again; the shop reports nothing bought |
| Dungeon timers (expeditions, raids, seals, daily, boss challenge, field events) | `Aion__TimeGates__DungeonTimeLimits` | off | no countdown, a run never fails on time |
| Daily/weekly collection rotation | `Aion__TimeGates__CollectionRotation` | off | every collection of the season is open all season (and after it ends), with all its lists |
| Gather node cooldown (per player) | `Aion__TimeGates__GatherRespawnSeconds` | 10 s | was 60 s; 0 = gather the same node again at once |

Not gates, or not modelled, so left as they are:

- Energy, entry tickets, dungeon cooldowns, death limits, the weekly currency cap and the battle pass weekly exp cap: the
  server never applied them (the client data has no ticket or energy cost, every DungeonCoolTime and DeathLimit is 0).
- Daily and weekly missions resetting, and the once-a-day attendance check-in: these hand out rewards each day, so
  removing the reset would mean one-time rewards rather than more of them.
- Battle pass and event boards keep their season dates (overlapping passes would show at once).
- Skill and item cooldowns (potions, return scroll), arena match timers, monster respawn (`Aion__Rules__RespawnSeconds`).

## Professions, pets and alts

| Setting | Key | Default |
|---|---|---|
| Crafts never fail (a failed craft used to eat the components) | `Aion__Unlocks__CraftAlwaysSucceeds` | on |
| Souls a pet costs to acquire and to level up | `Aion__Unlocks__PetSoulCost` | x0.5 (rounded up, at least 1): 5 → 3 to acquire, 25 → 13 and 75 → 38 to level |
| Every skin for every character | `Aion__Unlocks__AllSkins` | off |
| Every pet/mount at its top level, every creature board at its top level (slots open) | `Aion__Unlocks__AllPets` | off |

The two unlocks apply to every character, new and existing, at its next world entry (character select), once per
character: a setting (`unlocks_all_skins`, `unlocks_all_pets`) remembers it, and turning the switch off later takes
nothing back. That gives 1,463 skins and 207 pets. The board slots open empty; stat rolls still cost crystals.

## Not tested in the client yet

All of this is covered by server checks (Aion.Native.Checks, the PostgreSQL repository checks, the deploy tests), not by
a live client. Things to watch on the first login with them on:

- The skin and pet windows with every skin and pet owned (big lists the client has never been sent).
- Collections with every daily/weekly book open at once (the official server sends one of each).
- Shop limited goods: the client may keep its own count within a session and grey the item out after the cap.
- Pet soul costs: the client may show the official cost or refuse locally before asking the server.

If one misbehaves, switch that key back (`true`/`false`) and restart aion-host.
