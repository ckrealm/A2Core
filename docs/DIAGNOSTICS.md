# Diagnostics

Where to look when the server misbehaves: the staff dashboard, the diagnostics API and the host log.

## The staff dashboard and API

Both need a staff account (GameMaster or Admin role, `tools/postgres_dev.py role USERNAME admin`); everyone else gets
401 (not signed in) or 403. Sign in with `POST /api/accounts/login` and send the token as `Authorization: Bearer <token>`.

| What | Where |
| --- | --- |
| Players in the world, game connections, CPU, memory, managed heap, threads, uptime, game loop load | Dashboard **Server** page gauges; `GET /api/admin/server` (needs the game port) |
| Everything below | Dashboard **Server** page, **Diagnostics** card; `GET /api/admin/diagnostics` |

```bash
TOKEN=$(curl -s -H 'Content-Type: application/json' -d '{"username":"me","password":"..."}' https://HOST:8780/api/accounts/login | jq -r .token)
curl -s -H "Authorization: Bearer $TOKEN" https://HOST:8780/api/admin/diagnostics | jq
```

`GET /api/admin/diagnostics` answers (all counters since the host process started; a restart clears them):

| Field | Meaning |
| --- | --- |
| `startedAt`, `uptimeSeconds` | When the host started. |
| `process.cpuPercent`, `cpuCores`, `cpuWindowSeconds` | Process CPU as a share of all cores (`cores`), and in cores used, over the time since the previous diagnostics call (at least 1 s; the first call averages since the start). |
| `process.workingSetMb`, `gcHeapMb`, `gcCommittedMb`, `gcCollections`, `threads` | Resident memory, managed heap in use, memory the GC holds, collections per generation, OS threads. |
| `sessions.login`, `sessions.game`, `sessions.characters` | Open login-port connections, open game-port connections, characters in the world. |
| `databases[]` | `SELECT 1` on the `Aion` and `Auth` connection strings (3 s limit): `reachable`, `latencyMs`, else `error` (exception type and SQL state). Only server and database names are shown. |
| `logins.web` | Account-site sign-ins (`POST /api/accounts/login`, also the launcher and dashboard): `succeeded` / `failed`. |
| `logins.nativeLogin`, `logins.nativeGame` | Launch-ticket checks on the login port and the game port: `succeeded`, `failed` (bad, expired or replayed ticket), `refused` (valid, but banned or maintenance). Development logins are not counted. |
| `errors[]` | Per log category (`source`): `errors` and `critical` entries, and `warningsWithException` (the native listeners log unexpected connection errors and failed saves as warnings with the exception); `lastType` is the last exception type, `lastAt` its time. No message text is kept. |
| `unansweredOpcodes[]` | Game requests that got no reply (the same ones the log names once per connection, see below): `opcode` (hex), `count` (every time), `connections` (how many connections sent it). Most frequent first; names are in `tools/data/opcode-names.json`. |

`GET /health/live` answers `{"status":"alive"}` without a sign-in (for a load balancer or a watchdog).

## The host log

The host writes to the console; under systemd that is the journal:

```bash
journalctl -u aion-host --since '1 hour ago' > host.log    # or a file the host's output was redirected to
```

Each entry is two lines: the level and category (`info: Aion.Host.Native.NativeGameListener[0]`), then the message.
Levels are `info`, `warn`, `fail` (Error) and `crit`. Use `grep -B1` to see the category of a message, `-A1` to see
the message of a level line. Account passwords, tickets and keys are never logged.

### Sign-in failures

```bash
grep -E 'Request finished .* POST .*/api/accounts/login - 401' host.log   # wrong password (account site, launcher, dashboard)
grep -E 'Request finished .* POST .*/api/accounts/login - 429' host.log   # too many attempts from one address (15 a minute)
grep -E 'Native login closed: (rejected|required|replay_rejected)' host.log   # login port: bad or missing ticket
grep -E 'Native (login|game login) refused:' host.log                         # banned or maintenance
grep -E 'Native game closed: (rejected|required|replay_rejected)' host.log    # game port: bad, missing or reused ticket
```

### Unanswered client requests

The first time a connection sends a request the server does not answer, it is logged; requests still being researched
are logged with their first bytes.

```bash
grep -oE 'Native game unanswered opcode 0x[0-9a-f]{4}' host.log | sort | uniq -c | sort -rn   # per connection, so: how many sessions
grep -E 'Native game (quest/NPC|social/object) request' host.log                            # research requests with their body
```

Look the opcode up in `tools/data/opcode-names.json` (e.g. `jq '."0x9100"' tools/data/opcode-names.json`).

### NPC, map event and dungeon problems

```bash
grep -E 'Map event: ' host.log                                   # all scripted-event and dungeon notes
grep -E 'Map event: (dungeon |NpcConversation: )' host.log       # dungeon entry refused, map not served, no entry NPC/teleport
grep -E 'Map event: map event .* (failed|: )' host.log           # a map event that failed or used an unsupported directive
grep -E 'Map event: (SetAggroNpc|ChangeRelationshipEntity|PCTeleport|map event route)' host.log   # NPC/route/teleport targets not found
grep -E 'refused by the store|persistence errors' host.log       # a request or save the database refused (TerrainLoad/NavMeshLoad: NPC ground or path data missing)
grep -E 'connection ended by an unexpected error|disconnect: ' host.log   # a session ended by an exception, or leaving the world/saving failed
grep -A1 -E '^(fail|crit):' host.log                             # every Error and Critical entry (the diagnostics `errors[]` counts these)
```

Once a minute while anyone plays, `World load:` lines give players, CPU and how busy the world and map turns were.
