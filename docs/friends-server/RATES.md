# Rates, time gates and alt perks (friends server)

Everything here is OURS (a server owner's choice, nothing observed on the official server) and is set in
`/etc/aion/game.env` (or with `wsl-install.sh --rates-only ...` / `tools/server_env.py`). The host logs what it uses at
startup: `journalctl -u aion-host | grep "World rates"`.

## Defaults now

| Setting | Key | Default | Was |
|---|---|---|---|
| Rates profile | `Aion__Rates__Profile` | `friends` | `official` |
| Kill experience | `Aion__Rates__Experience` | 5 | 1 (5 with `accelerated`) |
| Quest experience | `Aion__Rates__QuestExperience` | follows experience (5) | 1 |
| Kinah amount | `Aion__Rates__Kinah` | 10 | 1 (3) |
| Kinah drop chance | `Aion__Rates__KinahChance` | x2 (normal monsters 70% → 100%) | x1 |
| Item drops (loot rolls per kill) | `Aion__Rates__Drop` | 4 | 1 (3) |
| Gathering mastery experience | `Aion__Rates__GatherExperience` | 10 (a level per gather) | 1 (8 gathers a level) |
| Crafting mastery experience | `Aion__Rates__CraftExperience` | 10 (a level per craft) | 1 (8 crafts a level) |
| Pet (creature board) experience | `Aion__Rates__PetExperience` | 10 | 1 |

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
| Wait after dying (the server revives you at the respawn point) | `Aion__TimeGates__ReviveSeconds` | 5 s | was 60 s |
| Odyn (Od) energy | none | not enforced | the server never tracks or spends it: dungeon reward cubes and hunting pay without it, so it is in effect infinite |

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
| Every skin for every character | `Aion__Unlocks__AllSkins` | on |
| Every pet/mount at its top level (3), every creature board at its top level (slots open) | `Aion__Unlocks__AllPets` | on |
| Every wing of the character's race | `Aion__Unlocks__AllWings` | on |

The unlocks apply to every character, new and existing, at its next world entry (character select), once per
character: a setting (`unlocks_all_skins`, `unlocks_all_pets`) remembers it, and turning the switch off later takes
nothing back. That gives 1,463 skins, 207 pets and every wing of the race. The board slots open empty; stat rolls still cost crystals.

Feathers (Empyrean Traces, the 2,560 collectibles in the world) are not modelled by the server yet: it has no table or
message for them, so they cannot be given out until the client data and a capture show how they are stored and sent.

## Alt boost (`/boost`)

Type `/boost` in chat on any character to see the price and whether it is unlocked; `/boost confirm` buys it. It
unlocks once any character of the account has reached level 45 (`Aion__Boost__RequiredLevel`). The character it is used
on (below level 45) pays 10,000,000 Kinah (`Aion__Boost__Price`) and gets:

- level 45 (`Aion__Boost__Level`), with the usual level-up messages and skills;
- every released main story (Hero), regional (District) and exploration quest of its race up to recommended level 46
  (`Aion__Boost__QuestLevel`) marked done: the whole released MSQ, and all 82 sealed dungeons of its race open (their
  exploration quests are the gates); no quest rewards are paid;
- a gear set in its bag: the best level-45 item for each slot (class weapon, helmet, shoulders, torso, pants, gloves,
  boots, cape, necklace, two earrings, two rings, bracelet; a Templar gets a shield instead of the bracelet), about
  1,412 item level (`Aion__Boost__Gear`);
- the whole world map revealed.

If a save fails part way (the database drops out), the payment stays on record and `/boost confirm` finishes the boost
without charging again; a character is boosted once. Then go to character select and back in so the quest journal refreshes. `Aion__Boost__Enabled=false` turns it off. The
Kinah price is meant to be hard to earn even at 10x Kinah (our guess, not measured); change it freely.

## Not tested in the client yet

All of this is covered by server checks (Aion.Native.Checks, the PostgreSQL repository checks, the deploy tests), not by
a live client. Things to watch on the first login with them on:

- The skin and pet windows with every skin and pet owned (big lists the client has never been sent).
- Collections with every daily/weekly book open at once (the official server sends one of each).
- Shop limited goods: the client may keep its own count within a session and grey the item out after the cap.
- Pet soul costs: the client may show the official cost or refuse locally before asking the server.
- The boost: the client is told the new level live, but the quest journal and the main story NPCs refresh at the next
  character select; the gear set must be equipped by hand.
- The revive wait: the client may show its own countdown or respawn button; the server revives after 5 s either way.

If one misbehaves, switch that key back (`true`/`false`) and restart aion-host.
