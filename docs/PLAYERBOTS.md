# Playerbots

Playerbots are characters the server plays. Other players see them as ordinary players: they appear, run, join
the party and fight what their leader fights, using their class's skills. The idea comes from AzerothCore's
mod-playerbots. The design is adapted here, and no code is shared with it. The official game has no playerbots,
so everything in this document is **OURS** unless a line says otherwise.

This first version has alt bots only: a player summons one of their own other characters. Random world bots,
bot accounts, healing and buffing come later (see [Not done yet](#not-done-yet)).

## Using them

Turn them on in the host configuration (see [Configuration](#configuration)), then type these in chat:

| Command | What it does |
|---|---|
| `//bot add <character>` | Summons one of your other characters on this server as a bot. With development logins, which have no stored characters, it makes a new unsaved character instead: `//bot add <name> [class]`. |
| `//bot remove <name>` / `//bot remove all` | Dismisses bots. They save and leave the world. |
| `//bot list` | Lists your bots with their state, strategy and role. |
| `//bot follow` / `//bot stay` `[name]` | Switches the movement strategy (default: follow). |
| `//bot assist` / `//bot passive` `[name]` | Turns fighting with you on or off (default: assist). |
| `//bot role <tank\|dps\|heal> [name]` | Picks which rotation entries apply. Until heal effects exist, every role uses the damage entries. |

The name argument defaults to `all`. `//bot` is for staff characters, and for every player when
`Aion:Bots:PlayersMayUse=true`. Its replies come back as chat replies, like the other `//` commands
(`GmCommands`).

A bot leaves the world when its leader logs out, goes back to the character screen or disconnects. It saves its
position first, through the leader's still-open store.

## Design

### A bot is a session without a socket

`GameSession` already runs without a network connection: the native checks log in, move and cast with several
sessions in one `SharedWorld` and no sockets. A bot is exactly that:

- **The session.** `PlayerBots.AddAsync` builds `new GameSession(data, owner.Options, rng) { Bot = true }`. The
  options are the leader's own: the same shared world, terrain, navmesh, rates and rules. With stored logins its
  `Store` is the leader's account store, wrapped in the bot's own `WorldTurn` (`TurnStore.Wrap`). The bot never
  disposes that store. The leader's connection owns it.
- **The null transport.** The bot loop feeds the session the same decrypted client request bodies a client sends
  (`BotRequests`): `0x3613` game login, then `0x361b` create (development logins only), `0x361f` select (the
  wire id shifted left 16 bits, as clients send it), `0x3622` load complete, `0x3700`/`0x3701`/`0x3718` moves,
  and `0x383a` + `0x3800` + `0x383c` casts. Everything the session would send to a client is dropped, except
  `0x3621` map loads. The bot answers those with `0x3622` the way a client does, so map events, teleports and
  revives work unchanged.

  The session dispatches on raw bodies, so the bot speaks raw bodies too. Calling the handlers directly with parsed
  objects would mean a second dispatch path, and the movement relay and the cast broadcast are built from the body.
  The layouts are the ones the checks already drive the session with. The `0x3718` stop is the observed 28-byte
  form, and its tail (u64) is CANDIDATE.
- **The tick.** `PlayerBots.RunAsync` ticks every bot every 50 ms, the same rate as a game connection's loop
  (`NativeGameListener`). Each bot step runs under its own `WorldTurn`, held exclusively (world work), so it takes
  turns with player connections like any other session. Storage calls run outside the turn (`TurnStore`). Each
  step:
  1. runs `session.TickAsync(now)` (regeneration, buffs, NPC AI, respawns, the outbox, checkpoints);
  2. moves the bot next to its leader when the leader is on another map, or more than `TeleportBeyond` away on the
     same one. This is done with the session's own `Teleport` and a `0x3622`. A leader inside a private
     instance copy (`InstanceKey != 0`) is not followed;
  3. feeds the requests `BotBrain.Think(now)` returns.
- **Concurrency.** Each bot has a `Busy` semaphore. The loop and `OwnerLeftAsync` always take it before the world
  lock, never inside it. Chat commands run on the leader's loop (`InvokeAsync`), which already holds the world
  lock, so they never wait on `Busy`. `//bot remove` only marks a bot `Leaving`, and the bot loop does the leaving.

### How other clients see a bot

Nothing about a bot is sent on a path made for bots. Its world entry goes through `EnterSharedWorld` →
`MapInstance.Join` → `UpdateVisibility`, so others get `0x3645` CharAppear. Its moves go through
`HandleAsync` → `CheckMovement` → `MovementRelay` → `map.Broadcast`, which gives `0x371c` and the other relays,
and the server's speed check applies to bots too. Its casts go through `CastReplies` → `SharedCastPacket` →
`map.Broadcast`, which gives `0x3802` and `0x3804` and the NPC HP. Kills, loot rolls, experience and quest
counts happen as they do for a player. NPCs see bots as players (`NpcVictims`), so a monster can hit and kill a
bot. A dead bot is revived by the session's own `ReviveDelayMs` timer and then runs or teleports back.

### Persistence

A bot is a normal stored character of the summoning account. It loads with the normal selection (`SelectAsync`),
so it has its own level, gear, skills, quests and achievements. It saves through the normal checkpoint: every
30 s while it moves, and forced when it leaves. The listener calls `PlayerBots.OwnerLeftAsync(session)` before it
disposes the leader's store, so a disconnect still saves the bots.

A bot cannot be the leader's own character or a character already in the world, so the same character is never
played twice. Characters of other accounts cannot be summoned.

### Party

`GameSession.JoinPartyAsBot(leader)` adds the bot to the leader's party without the invitation round trip. The
leader asked for the bot by command, so there is no question to answer. It sends the same messages an accepted
invitation sends: `PartyInfo` (Create) to a leader who had no party, `PartyAddMember` to the members, and
`PartyInfo` (Join) to the bot. The party limit stays 4, so the leader plus 3 bots. A bot over the limit still
follows and fights. It just is not in the party. Leaving the world leaves the party through `LeaveSocial`, as a
disconnect does.

### The brain: strategies and actions

mod-playerbots splits a bot into *strategies* (which triggers are active), *triggers* (conditions) and *actions*.
`BotBrain` keeps that split in a small form:

| Strategy | Trigger | Action |
|---|---|---|
| **follow** (default) | Leader more than `FollowFar` (450 cm) away, or still moving to its slot | Run to its slot, a point `FollowDistance` (250 cm) from the leader. Bot 0 stands behind the leader and the others fan out at ±60° steps. |
| **stay** | Always | Stop and hold position. Still fights when assist is on. |
| **assist** (default) | The leader cast at a living hostile NPC in the last 8 s (`GameSession.AttackTarget`), or the bot's own target still lives, within 3000 cm of the leader | Run until within the class's `range`, stop, and cast the first rotation entry that can be cast now. |
| **passive** | Never | Does not fight. |
| teleport (always on) | Leader on another map, or beyond `TeleportBeyond` | Teleport next to the leader. |

Movement:

- The bot runs at 600 cm/s, the official run speed (CANDIDATE, see `GameSession.RunThreshold`). The speed check
  allows 1280 cm/s.
- It sends a move update every 200 ms: one `0x3700` start, then `0x3701` updates, then a `0x3718` stop.
- It walks the map's navmesh path (`NavMesh.FindPath` over the same mesh the NPC AI uses). The height comes from
  the navmesh floor, else the terrain grid, else a straight line.
- The direction (u16) and heading (f32) carry the yaw in degrees (CANDIDATE).

Casting:

- A bot casts at most once per `global_cooldown_ms` (1000 ms; skills have no cast times in the data).
- It sends the client's bracket around each cast. The cast's location is the target's position (CANDIDATE: the
  session reads it only for self-centred area skills).
- The session's own cast handler decides everything that matters: learned, weapon, MP/SP/HP cost, cooldown.
- The brain checks the same things before it sends a cast, so it never spams casts the server refuses. If the
  server refuses a cast anyway (`0x3801` result ≠ 0), that skill rests for 10 s.

## Rotation data

The rotations live in `tools/data/bot-rotations.json`. `tools/bot_rotations.py` builds them from
`tools/data/class-skills.json`, and they are embedded in `Aion.World` as `bot-rotations.json`. The format:

```json
{
 "marker": "OURS",
 "global_cooldown_ms": 1000,
 "classes": {
  "Gladiator": {
   "weapon_type": "Greatsword",
   "range": 300,
   "roles": ["tank", "dps"],
   "rotation": [
    {"skill": 11100000, "name": "Ruinous Blow", "level": 14, "when": {"target": "enemy"}},
    {"skill": 11020000, "name": "Keen Strike",  "level": 1,  "when": {"target": "enemy"}}
   ]
  }
 }
}
```

- `rotation` is a priority list. The bot casts the first entry that is learned at its level, matches its role,
  meets its `when` conditions, is off cooldown and is affordable.
- `level` is the class unlock level from class-skills.json. `roles` (optional) limits an entry to tank, dps or
  heal bots.
- `when` conditions. A missing condition holds.
  - `target`: `enemy` (the fight's target), or `self`, `ally` or `leader` for heal and buff entries.
  - `target_hp_below` and `target_hp_above`: percentages of the target's HP.
  - `self_hp_below` and `self_mp_above`: percentages of the bot's own HP and MP.
  - `in_combat`: true or false.
- `range` is how close the bot gets before it casts. Melee classes use 250-300 cm and ranged classes 1800 cm.
  The client's per-skill ranges are not in class-skills.json.

The generated lists hold only Active skills with Damage rows and the class's weapon. They are ordered with long
cooldowns first, then shorter ones, then the cooldown-free fillers, with MP fillers before the free one. Dodge
and the self buffs are left out.

**Heal and buff entries plug in without code changes to the format.** Heal and buff effects are being built on
another branch. Once they land:

1. Add entries such as
   `{"skill": 17100000, "level": 10, "roles": ["heal"], "when": {"target": "ally", "target_hp_below": 60}}` to
   `EXTRA` in `tools/bot_rotations.py`.
2. Teach `BotBrain.Pick` to resolve `ally`, `self` and `leader` targets (from `GameSession.PartyMembers()` and
   `Resource`), then add them to `BotCondition.SupportedTargets`.

Until then, entries with those targets parse but are skipped.

Rebuild with `python tools/bot_rotations.py`. `python tools/bot_rotations.py --check` and
`tests/test_bot_rotations.py` check that every skill exists and belongs to that class, that the unlock levels
match, that the conditions are known, and that the tracked file is current.

## Configuration

These settings are in `appsettings` or the environment (`Aion__Bots__Enabled=true`). All are OURS.

| Key | Default | Meaning |
|---|---|---|
| `Aion:Bots:Enabled` | `false` | Playerbots on. Off: no bot loop, and `//bot` answers that they are off. |
| `Aion:Bots:PlayersMayUse` | `false` | Every player may use `//bot`. Off: staff characters only (`BuilderCommands.AllowedAsync`). |
| `Aion:Bots:MaxPerAccount` | `3` | Bots one account may have in the world at once. Development logins count per leader. |
| `Aion:Bots:MaxTotal` | `20` | Bots on the host at once. |
| `Aion:Bots:JoinParty` | `true` | Bots join their leader's party, up to the party limit of 4. |
| `Aion:Bots:TeleportBeyond` | `4000` | A bot farther than this from its leader (cm), or on another map, teleports to it. Minimum 1000. |

Bots do not count against `Aion:Native:MaxPlayers`, which counts connections, and they hold no socket slot. Each
bot costs one exclusive world turn per 50 ms tick. That is fine for a friends server. A large host would batch
the bots of a map into one map turn.

## Code

| File | What it holds |
|---|---|
| `server/Aion.World/PlayerBots.cs` | `BotOptions` (config), `PlayerBot` (one bot), `PlayerBots` (commands, caps, the loop, login, leaving, the null transport) |
| `server/Aion.World/BotBrain.cs` | `BotRequests` (client request bodies), `BotBrain` (strategies, movement, rotation choice) |
| `server/Aion.World/BotRotations.cs` | The rotation data model and the embedded loader |
| `server/Aion.World/GameSession.Bots.cs` | Reads a bot needs (`AttackTarget`, `Knows`, `CooldownUntil`, `WorldPosition`, `Options`) and `JoinPartyAsBot` |
| `server/Aion.World/GmCommands.cs` | `//bot` and its permission check |
| `server/Aion.Host/Native/PlayerBotsService.cs` | The hosted bot loop. `Program.cs` binds `Aion:Bots`, and `NativeListeners.cs` dismisses a leader's bots on disconnect. |
| `tools/bot_rotations.py`, `tools/data/bot-rotations.json` | Rotation builder, validator and data |
| `tests/Aion.Native.Checks/Program.cs` ("Playerbots") | The checks, plus `tests/test_bot_rotations.py` |

## Not done yet

- **Heal and buff entries.** The format is ready (see above). They need the effects branch.
- **Self-defence.** A bot only fights its leader's target or its own. A monster that attacks only the bot is
  ignored until the leader engages it.
- **Tank behaviour.** No threat or taunt logic yet. A tank bot uses the damage rotation like a dps bot.
- **Instances.** A leader inside a private instance copy (layers, solo dungeons) is not followed. Party dungeon
  copies need the bot to enter the copy with the party.
- **Looting and quest sharing.** None. Kills by bots roll loot to the bot's own session as for any player.
- **Random bots** (mod-playerbots' `randombots`). These would be bot accounts with generated characters that
  wander and level on their own. They need an account and character factory, plus a "grind/quest" strategy set.
- **Bot accounts.** Summoning another account's characters, or server-owned ones, needs a store opened without a
  client ticket. Today a bot always uses its leader's account store.
- **Console command.** `s bot ...` through `0x362e` is not wired. Only the `//bot` chat command is.
