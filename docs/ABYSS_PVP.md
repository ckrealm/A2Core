# Abyss PvP: artifact sieges, Spacetime Rifts, the battlefield

Organised faction PvP as global Aion 2 runs it (patch of 4 Nov 2026), sized for a small server: world bots take the places
5-10 friends cannot fill. Code: `server/Aion.World/AbyssWar.cs` (the schedules, world side), `GameSession.AbyssWar.cs` (the
player side), `PvpBots.cs` (the filler bots), `Aion.Host/Native/AbyssWarService.cs` (ticks it once a second under the world lock).
Checks: `tests/Aion.Native.Checks/AbyssChecks.cs` (33).

Values come from fan sources on the global service unless marked **OURS** (no source gives them) or **CANDIDATE** (read from the
client, not confirmed against live traffic).

## Artifact siege

- Thursday and Saturday at 21:00 for an hour (`Aion:Abyss:Siege:Days`, `Hour`, `Minute`, `DurationMinutes`), on the Lower and
  Middle layers of Reshanta (maps 20 and 22, `Layers`).
- Each layer has three artifacts (regions RR, STI, WSA). Their Dimensional Cores (`*_Artifact_OrcusGem_*` NPCs) are only up during
  a siege. The faction that lands the killing blow claims the artifact, and the top 30 contributors of that faction (by hate,
  `RewardedPlayers`) get `CapturePoints` Abyss Points (3,000, **OURS**).
- The Executors (`_AbBoss_` NPCs) come out after 30 minutes (`ExecutorsAfterMinutes`).
- Each artifact held gives the faction +2.5% Abyss Points (`BonusPercentPerArtifact`, up to three), on kills, battlefield and
  dungeon rewards.
- The holder may enter the artifact's Abyss Corridor (the portal object at the core, dungeons 503001-6 for Elyos and 504001-6 for
  Asmodians) for 5 minutes at a time (`CorridorMinutes`). Nobody may enter while that artifact is still contested.
- The client's artifact window gets `AbyssArtifactEventInfo_RS` (0xe307) and the siege notices 0xe300-0xe303 (**CANDIDATE**
  opcodes, usmap layouts).

## Spacetime Rifts

- Every 3 hours from 02:00 (`Aion:Abyss:Rifts:EveryHours`, `FirstHour`) a rift opens for 10 minutes (`OpenMinutes`) in Verteron
  (leads to Altgard) and in Altgard (leads to Verteron), at one of the five rift spots the client places on each field.
- Level 45 and up (`MinLevel`), 400 per portal (`MaxPerPortal`), an hour on the other side (`StayMinutes`), then the rift pulls you
  back to where you went in. Dying on the other side also sends you home.
- **Rift PvP** (RiftOptionUpdate 0x3652-0x3654): switched in your own territory, at most once per 70 minutes
  (`PvpCooldownMinutes`). Two players of different races with it on may fight in the home fields. `/rift on|off` does the same from
  chat. A kill in a home field gives `KillPoints` Abyss Points (100, **OURS**).

## Battlefield (Arena of Tactics)

- Open 11:00-14:00 and 19:00-21:00 (`Aion:Abyss:Battlefield:Windows`), level 45, 10 a side, 10 minutes, on Odyle Shard Cradle
  (dungeon 10, map 62).
- Push the Shard Catalyst into the enemy camp: it moves toward the camp of the team with more living players around it
  (`CatalystSpeed` 60 cm/s and `CatalystRadius` 800 cm, **OURS**). Reaching a camp wins. On time out the team that pushed it farther
  wins; a tie goes to 2 minutes of extra time where any push decides (`ExtraTimeMinutes`, **OURS**).
- 10 second respawns, unlimited lives. Stats are equalised: everyone hits at level 45 with skill level 1, against base defense, and
  HP counts as the class's base (`NormalizedLevel`). Weapons are not yet equalised.
- Rewards: 1,000 Abyss Points and 1 medal to join, 3,000 and 2 more to win, 3 of each a week, the week starting Wednesday 07:00
  (`JoinPoints`, `WinPoints`, `JoinMedals`, `WinMedals`, `WeeklyJoinRewards`, `WeeklyWinRewards`, `ResetDay`, `ResetHour`). The
  medal item (`MedalItemId` 930100014, Silver Medal of Merit) is **CANDIDATE**.
- Queue with `/abyss bf` (or the client's matching window: matching id 3, **CANDIDATE**). After 60 seconds (`FillAfterSeconds`)
  bots take the free places; the match waits up to 30 more seconds for them to log in, then everyone is asked to accept.

## Bots

`Aion:Abyss:Bots` (on by default) uses world content bots (docs/WORLDBOTS.md; they run even with `Aion:WorldBots:Enabled` off):

- `SiegePerFaction` (8) bots a race go to the Lower layer when a siege starts and fight over the cores.
- `RiftPerFaction` (3) bots a race go through each open rift with rift PvP on.
- The battlefield is filled up to 10 a side.
- At most `Max` (60) bots at once; idle ones wait at their race's home field and leave after `IdleMinutes` (10).

A PvP bot fights the nearest enemy player it may hit within 25 m, else its objective (its place around the catalyst, an unclaimed
core, its rift arrival point). Bots following a player (playerbots) now also defend against enemy players near their leader.

## Commands

| Command | Who | What |
|---|---|---|
| `/abyss` | everyone | the siege, artifacts held, your bonus, your rift, the battlefield |
| `/abyss bf` | everyone | queue for the battlefield |
| `/rift on`, `/rift off` | everyone | rift PvP (own territory, 70 minute cooldown) |
| `//abyss siege start [minutes]`, `//abyss siege end` | staff | start or end a siege now |
| `//abyss rift open [minutes]` | staff | open the rifts now |

## Config

```json
"Aion": {
  "Abyss": {
    "Enabled": true,
    "TimeZone": "",
    "Siege": { "Days": ["Thursday", "Saturday"], "Hour": 21, "DurationMinutes": 60 },
    "Rifts": { "EveryHours": 3, "FirstHour": 2, "OpenMinutes": 10 },
    "Battlefield": { "Windows": ["11:00-14:00", "19:00-21:00"], "TeamSize": 10, "FillAfterSeconds": 60 },
    "Bots": { "Enabled": true, "SiegePerFaction": 8, "RiftPerFaction": 3, "Max": 60 }
  }
}
```

`TimeZone` is a time zone id of the host (for example `America/New_York`); empty uses the host's own clock. Environment variables
work as for the rest of the config (`Aion__Abyss__Siege__Hour=20`).

## Known limits

- Artifact owners and the weekly battlefield counts are kept in memory: a restart resets them.
- The siege length, capture points, catalyst speed and radius, extra time and bot numbers are **OURS**.
- The siege opcodes, the battlefield matching id and the medal item id are **CANDIDATE**.
- Weapons are not equalised in the battlefield.
- Logging out during a rift visit saves the position in the enemy field.
