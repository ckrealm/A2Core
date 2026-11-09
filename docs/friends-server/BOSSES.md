# Boss phases and dungeon waves

What changed for the Season 1 dungeons (the six Expeditions in Easy, Normal and Hard, the Transcendence dungeons and the
raid Abyssal Forge: Ludra), and where each piece comes from. "Client" means the game's own data; "OURS" means our own
choice, because the official server code that runs bosses has never been seen.

## Objectives

In the Season 1 maps the only objective the server could not follow was `ClearWave` (18 in the Ludra raid, one in each
Ferocious Horn Den mode). It used to finish by itself after `Aion:Dungeons:UntrackedObjectiveSeconds` (30 s), whether or
not anyone fought. Now:

- **Ludra's waves are the client's own.** A script's `StartWave ReforgedAbyss_1PartyWave_03` brings the spawn entries
  `ReforgedAbyss_1PartyWave_03_1`, `_2` (and `_3`) of the raid map. They wait out of sight from the map load (before,
  about 300 wave monsters stood in the raid from the start). The parts come one at a time: the next when the current one
  is dead, or 25 s after it came (`GameSession.WavePartMs`, OURS). Each monster runs at the nearest player. The wave is
  clear when every part came and died, and the script's `ClearWave` objective counts then. `EndWave` (a failed stage)
  removes the wave; the script starts it again from its first part.
- **Ferocious Horn Den's totem wave is ours.** Its spawn data names no wave monsters, so `tools/data/boss-phases.json`
  has a hand-made one: three of the den's Lycans come around the totem every 15 s while it lives (six times at most).
  The wave is clear when the totem and the Lycans are dead.
- Waves on other maps (field events, Beritra, sealed dungeons) name spawns we can't match yet. They keep the timer and the
  log line, as before.
- The raid's `ResetRebirthLimit` and `SetAbnormalVolumeState` are no longer logged as unsupported. The client data has no
  revive limit or volume effect for them, so there is nothing to apply.
- The `HpRemaining` condition (some scripts branch on a boss's HP) now reads the NPC's HP. Before, it was always false.

## Boss phases (68 bosses)

Every boss gets a script, built by `tools/boss_phases.py` (rules in its docstring) into `tools/data/boss-phases.json`.

| Piece | What it does | Source |
|---|---|---|
| **Slam** | The boss stops and winds up for the skill's cast time, with a warning in the middle of the screen. When the wind-up ends, every player within the radius takes the hit, the boss's target too. Walk out to avoid it. | The slam is the boss's own longest-cast skill, for example Krao Cave's 8 s skill 1800140, which used to hit only the tank. The radius (5–9 m) and the interval are OURS. A boss without a slow skill winds up its basic attack for 3 s at 3x damage. |
| **Adds** | At 70% HP (final bosses), 60% (Lagta, Kashapa) and 75% and 50% (Durbati), the boss calls 2–4 monsters. They come from that dungeon's own most common monster and run at the boss's target. They disappear if the group wipes or the boss dies, and they never respawn. | OURS thresholds and counts; the monsters are the client's. |
| **Fury / enrage** | 40%: 15% more damage and faster slams. 20% (Normal and Hard): 30% more damage, cooldowns 20% shorter. | OURS. |
| **Frenzy** | Hard modes only: 4 minutes into the fight the boss hits 3x as hard and attacks twice as fast. In practice it's a soft wipe timer. | Global's hard-mode rule (Oct 21, 2026). |
| **Mid bosses** | Named bosses with six or more skills before the final boss get a slam every 30 s and 20% more damage below half HP. | OURS. |

Easy is gentler: 2 adds, slams every 30 s, no enrage and no frenzy. The Orcus Gem in Ludra is left to the client's own
script, which already starts its extractor waves at 90%, 60% and 30% HP.

A wipe resets the boss: it walks home, heals, its adds vanish and the phases start over.

### Bots and player defences

A slam being wound up shows in `NpcAi.WindingArea` and `NpcAi.SpecialDueAt`, like the area specials. Bots outside the
boss's target step out of the radius, and the target dodges just before it lands. Slams go through the same guard as
other monster hits, so a block halves one and a Dodge's i-frames evade it. Interrupt skills don't break a slam. A hold or
stun does, and the boss tries the slam again 4 s later.

Adds are named `<monster>_Summon`, so the scripts' spawner checks, wave restarts and kill objectives never count them.
They spawn at full strength. Once the short-handed dungeon scaling (PR #7) is merged, they should be scaled like the rest
of the copy.

## Settings: `Aion:Bosses`

| Setting | Default | Meaning |
|---|---|---|
| `Phases` | `true` | `false` turns every script off: bosses fight as before. |
| `Frenzy` | `true` | The hard modes' frenzy. |
| `FrenzySeconds` | `0` | Overrides every frenzy time (0: the script's, 240 s). |
| `DamageScale` | `1.0` | Multiplies the phases' and slams' damage (0.1–10). Lower it for a casual group. |

Environment form: `Aion__Bosses__DamageScale=0.8`. The startup log prints the values (`Bosses: ...`).

## Not done

- The real skill shapes (cones, lines, target counts) are in Skill.dat but not extracted yet. Slams are circles around
  the boss.
- The client shows a slam's cast animation and our warning, but no ground marker. Ground markers would need the client's
  telegraph effect messages, which aren't recorded yet.
- The two-group split in Ludra needs 10-player raid groups (the party-size thread).
- The 3-revive death limit and Ariel Fragments on hard modes are in the hard-modes thread.
