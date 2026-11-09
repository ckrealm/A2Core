# Aion:Friends — quality-of-life switches

These switches come from the player-complaint research (`/mnt/project-files/a2core/research/PLAYER_COMPLAINTS.md`). They
answer common complaints about the official game but keep its progression. **All of them are on by default** when the host
reads its configuration. The library default (`GameSessionOptions.Friends`) is `FriendsOptions.Official`, so the existing
checks still test official behaviour.

| Key | Default | Official | What it does |
|---|---|---|---|
| `Aion:Friends:FreePremiumPass` | `true` | `false` | Anyone can claim the paid battle-pass track's level rewards. A purchase request succeeds without charging anything, and the pass list says the character is subscribed (`_is_subscribed`, CANDIDATE: the field name comes from the usmap, and how the client reacts hasn't been recorded). |
| `Aion:Friends:BothFactions` | `true` | `false` | An account may hold Elyos and Asmodian characters. Each race's world (1361 Elyos, 2361 Asmodian) lists only its own characters and has its own lobby slots (migration `041_friends_lobby_slot_per_race.sql`). When both races exist, the server list opens on the world picked last. |
| `Aion:Friends:FlightMultiplier` | `2.0` | `1.0` | Multiplies the flight gauge maximum (250,000 officially, which is 25 s of flight). Drain and refill per second stay official, so a flight lasts 50 s, and an empty gauge takes twice as long to refill. The range is 1 to 10. |

In `/etc/aion/game.env`, use the environment-variable form, for example `Aion__Friends__FlightMultiplier=3`. Restart the host after a change.

Migration 041 relaxes the lobby-slot index to one slot per account, world and race. When `BothFactions` is off, the server
still refuses the other race at creation, so one race per account still holds.
