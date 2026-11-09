# Playerbots

Playerbots are characters the server plays. Other players see them as ordinary players: they appear, run, join
the party and fight what their leader fights, using their class's skills. The idea comes from AzerothCore's
mod-playerbots. The design is adapted here, and no code is shared with it. The official game has no playerbots,
so everything in this document is **OURS** unless a line says otherwise.

Bots are alt bots: a player summons one of their own other characters. They heal, resurrect and buff their group
(Cleric, Chanter), defend themselves and their group, tank (threat), pick up their loot and follow their leader into
party dungeons. Random world bots and bot accounts come later (see [Not done yet](#not-done-yet)).

## Using them

Turn them on in the host configuration (see [Configuration](#configuration)), then type these in chat:

| Command | What it does |
|---|---|
| `//bot add <character>` | Summons one of your other characters on this server as a bot. With development logins, which have no stored characters, it makes a new unsaved character instead: `//bot add <name> [class]`. |
| `//bot remove <name>` / `//bot remove all` | Dismisses bots. They save and leave the world. |
| `//bot list` | Lists your bots with their state, strategy and role. |
| `//bot follow` / `//bot stay` `[name]` | Switches the movement strategy (default: follow). |
| `//bot assist` / `//bot passive` `[name]` | Turns fighting with you on or off (default: assist). |
| `//bot role <tank\|dps\|heal> [name]` | Picks which rotation entries apply and how the bot picks its fight (see [Roles](#roles)). A new bot takes its class's first role: heal for Clerics and Chanters, tank for Templars, dps for the others. |

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
  2. follows its leader into or out of an instance copy (see [Instances](#instances)), or moves the bot next to its
     leader when the leader is on another map, or more than `TeleportBeyond` away on the same one (in the same copy,
     or both outside one). This is done with the session's own `Teleport` and a `0x3622`;
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
| **support** (always on) | A heal, resurrection or buff entry of the rotation holds for a group member (see [Rotation data](#rotation-data)) | Run to within 80% of the skill-effects `heal_range` (2500) of that member, stop, and cast at it. |
| **assist** (default) | The leader cast at a living hostile NPC in the last 8 s (`GameSession.AttackTarget`), or the bot's own target still lives, or a monster is attacking the group (self-defence), within 3000 cm of the leader | Run until within the class's `range`, stop, and cast the first rotation entry that can be cast now. |
| **loot** (with assist) | No fight, and the bot's own drops lie within 2500 cm | Walk to within 300 cm and pick them up with the client's `0x5620` request. |
| **passive** | Never | Does not fight or loot. It still heals and buffs. |
| teleport (always on) | Leader on another map, or beyond `TeleportBeyond` | Teleport next to the leader. |
| instance (always on) | Leader in a party dungeon copy the bot is not in, or the bot in a copy the leader left | Enter the leader's copy through the dungeon's own entry, or leave the copy (see [Instances](#instances)). |

The brain checks them in this order every tick: support entries (by their place in the rotation), then the fight,
then loot, then follow. The "group" is the bot, its leader and their party members on the same map and copy.

### Who a bot fights

1. A **tank** bot first takes monsters that attack someone else in the group, healers first (a heal bot, or a player
   Cleric), then the nearest.
2. Then every bot takes the leader's target, then its own target while it lives.
3. Then **self-defence**: a monster attacking the bot itself, then one attacking the leader, then one attacking another
   party member. A monster "attacks" a group member when its `NpcAi` brain is engaged with that member as its victim
   (the top of its hate table, or a map event's aggro). The leader does not have to have engaged it.

### Roles

| Role | Fight | Rotation entries |
|---|---|---|
| `heal` | Fights like dps when nobody needs healing. | Its heal entries (`roles: ["heal"]`) look after the whole group at a high HP threshold. |
| `dps` | As above. | Support classes keep their self-heals, self-buffs and an emergency ally heal (35%). |
| `tank` | Takes monsters off the others first. | Its hits count `tank_hate_multiplier` times (3, OURS) in the monster's hate table. |

**Threat.** `NpcAi` keeps a hate table per monster: each hit adds its damage under the attacker's key, and the monster
chases the top entry. A tank bot's session has `HateMultiplier` set from `tank_hate_multiplier` in
`bot-rotations.json`, and `GameSession.HitTarget` multiplies the hate a hit adds. The damage itself and every message
stay the same. There is no taunt skill and no hate from heals.

### Looting

Kills by bots go through the session's normal kill and loot path (`GameSession.Loot.cs`), so `Aion:Rules:PartyLoot`
decides who gets what:

- **killer**: everything lies on the ground for the killer. When a bot is the killer, the loot is its own.
- **roundrobin** and **freeforall**: the other members' parts go straight into their bags, as for players. The killer's
  part, which can be a bot's, lies on the ground.

A bot picks up what lies on the ground for it. After the fight it walks to its own drops (within 2500 cm of itself
and 3000 cm of its leader) and sends the client's pick-up request (`0x5620 ItemLooting_RQ`, auto loot off). The
session checks the range, grants the items to the bot's character (its bags, its account store) and removes the
drops. Each drop is asked for once: a refused pick-up (full bag) is left to vanish after `DropLifetimeMs`. Bots never
pick up their leader's drops, because those belong to the leader's session. Loot does not pass from the bot to the
leader. The bot is the player's own character, so its loot is theirs anyway.

### Instances

Private copies (`InstanceKey != 0`) are entered through the session's own entry paths only:

- **Party dungeons** (expeditions, field events, and every dungeon whose `MapEntrance` allows more than one member).
  A bot in its leader's party teleports to where the leader entered from (`GameSession.InstanceReturn`). It then uses
  that map's entry for the leader's dungeon, as a player does: the nearest portal object (`0x3636 InteractEnvObj_RQ`,
  waiting out the portal's channel time) or the entry NPC (`0x4006 NpcConversation_RQ`). The session's
  `EnterPartyDungeon` puts a party member into the copy a member is already in, and applies the same entry rules as
  for a player (`MapEntrance` level, item level and quest step, a full copy). Inside, the bot follows, fights and
  loots as usual. When the leader leaves the copy, the bot sends the client's leave request (`0x4004`).
- **Not followed:** solo copies (instance layers entered by a trigger box, sealed one-member dungeons), dungeons that
  have no portal or entry NPC on the leader's entry map (a staff `EnterDungeon`, raids entered through client windows
  that are not recorded), and bots that are not in the leader's party. The bot waits where it is, and `//bot list`
  shows why. A bot that gets into a copy other than its leader's (a full copy, entry refused) leaves it and waits
  30 s before it tries again.

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
  meets its `when` conditions, is off cooldown and is affordable. A friendly entry also needs a group member that
  meets its conditions.
- `level` is the class unlock level from class-skills.json. `roles` (optional) limits an entry to tank, dps or
  heal bots.
- `when` conditions. A missing condition holds.
  - `target`: `enemy` (the fight's target), `self` (the bot), `leader`, `ally` (the living group member with the lowest
    HP share that meets the conditions; the bot itself is one) or `dead_ally` (a dead group member, for resurrection).
  - `target_hp_below` and `target_hp_above`: percentages of the target's HP.
  - `self_hp_below` and `self_mp_above`: percentages of the bot's own HP and MP.
  - `in_combat`: true or false. The group is "in combat" when the bot has a fight, a monster attacks a group member, or
    a group member hit a monster in the last 8 s. A heal's cast bracket alone does not count.
  - `buff_missing`: true when the target must not already carry this skill's buff or heal over time.
- `tank_hate_multiplier` (top level): the factor a tank bot's hits count in a monster's hate table.
- `range` is how close the bot gets before it casts. Melee classes use 250-300 cm and ranged classes 1800 cm.
  The client's per-skill ranges are not in class-skills.json.

The generated lists have two parts:

1. **Support entries**, made from `tools/data/skill-effects.json` (`SUPPORT` in the builder). Every Active skill of
   the class that is usable with its weapon and has a heal, hot, resurrect or buff effect gets entries. They are
   ordered heal, then heal over time, then resurrection, then buffs (mod-playerbots' priority heal > rez > buff), and
   always come before the damage entries.

   | Effect (its skill-effects target) | Entry |
   |---|---|
   | heal (friendly) | heal role: `ally` below 70%. Other roles: `ally` below 35% (an emergency heal). |
   | heal (self) | `self` below 50%, every role. |
   | hot (friendly) | heal role: `ally` below 90% with `buff_missing`. Other roles: `self` below 70% with `buff_missing`. |
   | resurrect (dead_friendly) | `dead_ally`, `in_combat: false`, every role. |
   | buff (self / friendly) | `self` / `ally` with `buff_missing`, every role. A buff shorter than its cooldown gets `in_combat: true` (a combat buff such as Defiance). A longer one is kept up all the time. |

   The class data has no resurrection skill yet, so no generated list has a resurrection entry. One appears as
   soon as a `resurrect` entry for a class skill is added to skill-effects.json.
2. **Damage entries**: Active skills with Damage rows and the class's weapon whose skill-effects entry, if any, lands
   on an enemy. They are ordered with long cooldowns first, then shorter ones, then the cooldown-free fillers, with
   MP fillers before the free one. Dodge is left out.

Hand-made entries go to `EXTRA` in `tools/bot_rotations.py`.

Rebuild with `python tools/bot_rotations.py`. `python tools/bot_rotations.py --check` and
`tests/test_bot_rotations.py` check that every skill exists and belongs to that class, that the unlock levels
match, that the conditions and roles are known, and that the tracked file is current. They also check each entry's
target against the skill's skill-effects.json entry. A friendly target needs a heal, hot or buff effect landing on
`self`/`friendly` (`self` also takes self-only skills). `dead_ally` needs a `resurrect` effect on `dead_friendly`.
`buff_missing` needs a buff or hot. An `enemy` entry must not be a friendly skill and needs Damage rows or an enemy
effect.

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
| `server/Aion.World/PlayerBots.cs` | `BotOptions` (config), `PlayerBot` (one bot), `PlayerBots` (commands, caps, the loop, login, following into and out of copies, leaving, the null transport) |
| `server/Aion.World/BotBrain.cs` | `BotRequests` (client request bodies), `BotBrain` (strategies, support targets, fight choice, threat, looting, movement, rotation choice) |
| `server/Aion.World/BotRotations.cs` | The rotation data model and the embedded loader |
| `server/Aion.World/GameSession.Bots.cs` | Reads a bot needs (`AttackTarget`, `Knows`, `CooldownUntil`, `WorldPosition`, `HasSkillBuff`, `LootableDrops`, `InstanceReturn`, `Options`), `BotRole`, `HateMultiplier` and `JoinPartyAsBot` |
| `server/Aion.World/GmCommands.cs` | `//bot` and its permission check |
| `server/Aion.Host/Native/PlayerBotsService.cs` | The hosted bot loop. `Program.cs` binds `Aion:Bots`, and `NativeListeners.cs` dismisses a leader's bots on disconnect. |
| `tools/bot_rotations.py`, `tools/data/bot-rotations.json` | Rotation builder, validator and data |
| `tests/Aion.Native.Checks/Program.cs` ("Playerbots") | The checks, plus `tests/test_bot_rotations.py` |

## Not done yet

- **Taunts and threat from heals.** Tanking is the hate multiplier and the target choice only. No skill is used as a
  taunt, and heals add no hate.
- **Resurrection skills.** The brain and the builder handle them, but no class skill has a `resurrect` effect in
  skill-effects.json yet (the class data carries none). The checks use a table entry of their own.
- **Solo instances and unrecorded entries.** See [Instances](#instances).
- **Quest sharing.** None.
- **Random bots** (mod-playerbots' `randombots`). These would be bot accounts with generated characters that
  wander and level on their own. They need an account and character factory, plus a "grind/quest" strategy set.
- **Bot accounts.** Summoning another account's characters, or server-owned ones, needs a store opened without a
  client ticket. Today a bot always uses its leader's account store.
- **Console command.** `s bot ...` through `0x362e` is not wired. Only the `//bot` chat command is.
