# Small-group dungeon scaling (`Aion:DungeonScaling`)

Everything here is **OURS**: the official game has no small-group scaling. The goal is modest help for a group
that is genuinely short-handed, without making a full group's run easier.

## What it does

Party dungeons and raids are balanced for a party of 4 (`Party.Limit`, our CANDIDATE party size). When a copy of
one is created with fewer members, its monsters get weaker:

| Members (players + bots) | Monster HP | Monster damage |
|---|---|---|
| 4 or more | 100% | 100% |
| 3 | 90% | 93% |
| 2 | 80% | 86% |
| 1 (solo) | 70% | 80% (79% floored at 80%) |

- **Playerbots count as members.** One player with three bots is a full group and gets no scaling.
- **Counted once, when the copy is created** (the first member's map load inside). The count is the larger of the
  first member's party (bots included, even those not inside yet) and the players already in the copy. Members who
  join later get the same copy at the same scale. A group that grows after entering keeps the easier monsters for that
  run. A group that shrinks keeps the harder ones.
- **What changes:** each monster's maximum HP (it also appears with that HP, so health bars read correctly) and its
  hits (the NPC damage scale, so ordinary attacks, skills and boss area attacks all shrink). Friendly NPCs (escorts,
  allies) are left alone. Dungeon monsters do not respawn. Monsters a map event wakes later come from the same spawn
  list, so they are scaled too.
- **Where:** only copies of the Dungeon.dat types in `Types` (`Party,Raid` by default). Open-world maps, story
  instance layers, solo dungeons (Seal, Quest, Awaken, AbyssArtifact...), PvP arenas and battlefields, and field
  events are never scaled.
- The host log gets a map-event note per scaled copy, for example
  `KraoCave_Easy: 1 member(s), monsters at 70% HP and 80% damage (Aion:DungeonScaling)`.
- `Enabled=false` changes nothing. Monsters are exactly as before.

## Configuration

`appsettings.json` (or environment variables `Aion__DungeonScaling__<Key>`):

```json
"Aion": {
  "DungeonScaling": {
    "Enabled": true,
    "GroupSize": 4,
    "HpPercentPerMissing": 10,
    "DamagePercentPerMissing": 7,
    "MinHpPercent": 70,
    "MinDamagePercent": 80,
    "Types": "Party,Raid"
  }
}
```

| Key | Default | Range | Meaning |
|---|---|---|---|
| `Enabled` | `true` | | Off: no scaling at all. |
| `GroupSize` | `4` | 1-40 | The group size the dungeons are balanced for. |
| `HpPercentPerMissing` | `10` | 0-50 | Monster HP lost per missing member. |
| `DamagePercentPerMissing` | `7` | 0-50 | Monster damage lost per missing member. |
| `MinHpPercent` | `70` | 10-100 | Monster HP never goes below this. |
| `MinDamagePercent` | `80` | 10-100 | Monster damage never goes below this. |
| `Types` | `Party,Raid` | | Dungeon.dat types scaled, comma-separated. |

A value out of range stops the host at startup with a message naming the key. The startup log line
(`World rates: ... Dungeons: ...; small-group scaling ...`) shows the settings in use.

`tools/server_env.py` (install/update) writes these when given: `--dungeon-scaling on|off`, `--scaling-hp-percent`,
`--scaling-damage-percent`, `--scaling-min-hp`, `--scaling-min-damage`. Without them the host defaults above apply.

## Code

- `server/Aion.World/DungeonScaling.cs`: the options record (`For(members)`, `Scales(room)`, `Validated()`) and
  `GameSession.ApplyDungeonScaling`, called in `LoadComplete` once the map's targets are set.
- `DungeonCopy.Scale` keeps the counted group and multipliers for the run.
- Checks: `tests/Aion.Native.Checks` (dungeon section: solo, off, a solo dungeon type, a party of 2 and 4 in a shared
  world).
