# Test results

Only things that actually ran are listed. Logs are in `../logs/`.

## 2026-11-08 — Phase 1 baseline (upstream A2Core `2b5f72c`, unmodified)

Environment: cloud Linux container, Ubuntu 24.04, .NET SDK 10.0.114 (Ubuntu package), PostgreSQL 16.15, Python 3.13, Node 22.
Not tested on Brandon's Windows 11 PC yet.

| Step | Result |
|---|---|
| `dotnet build server/Aion.Host` (includes web build via npm) | **Pass**: 0 warnings, 0 errors, 22 s |
| `tools/postgres_dev.py setup` (own cluster, 38 migrations) | **Pass** (must run as non-root user) |
| Host startup, DB only | **Pass**: account site on :8780 serves the sign-in page |
| Host startup with native ports | **Pass**: login :3700, game :13328, chat :8091 accept TCP connections |
| `POST /api/accounts/register` | **Pass**: account created |
| `POST /api/accounts/login` correct password | **Pass**: session token returned |
| `POST /api/accounts/login` wrong password | **Pass**: rejected |
| Catalog / starter data import | **Not run**: needs the server data bundle (see KNOWN_ISSUES) |
| Platform bootstrap, NCGuard sink | **Not run**: need the data bundle |
| Game client connection | **Not run**: needs Brandon's PC and the 2.0.6.0 client |

### .NET check programs

| Program | Result |
|---|---|
| Aion.Native.Checks | **Pass**: 807 checks (framing, login, game sessions, NPC AI, shared world incl. two-player world, social, scaling, GM commands, quests) |
| Aion.Server.Checks | **Pass**: 14 |
| Aion.Chat.Checks | **Pass**: 68 |
| Aion.Launcher.Checks | **Pass**: 35 |
| Aion.Persistence.Checks (via `tools/check_persistence.py`, disposable DB) | **Pass**: 134 |
| Aion.Authentication.Checks (via `tools/check_authentication.py`) | **Partial**: 89 DB checks pass, then the host check fails on a stale page-title assertion |

### Python tests

`python -m unittest discover -s tests`: 477 run, 24 skipped (need raw client extractions), **4 errors**:
- 2 × `test_account_login`: the test reaches an interactive `input()` prompt (Windows sign-in window path).
- 2 × `test_dotnet_ncguard_host`: Python 3.13 rejects the test's generated certificate ("Missing Authority Key Identifier").

None of these are gameplay failures. Unit/check passes are not in-game verification.

## 2026-11-08 — branch `dev` (8482a0a: rates + diagnostics + test fixes)

| Step | Result |
|---|---|
| `dotnet build server/Aion.Host` | **Pass**: 0 warnings, 0 errors |
| Aion.Native.Checks | **Pass**: all sections, incl. 24 new rate/rule checks (presets, overrides, invalid values, event clamp, Kinah scaling, loot cap, player cap, 2× kill XP and 5 s respawn through a real session, PvP off) |
| Aion.Server.Checks | **Pass**: 22 (8 new diagnostics checks) |
| Persistence (disposable DB) | **Pass**: 134 |
| Authentication (disposable DB + live host) | **Pass**: 89 DB + 125 HTTP checks (previously stopped at a stale check) |
| Python unittest | **Pass**: 481 run, 0 errors, 24 skipped (need raw client extractions) |
| Live host, `Aion__Rates__Profile=accelerated`, `MaxPlayers=10` | **Pass**: startup log "experience x5, quest experience x5, Kinah x3, drops x3 … Players: at most 10" |
| Live `GET /api/admin/diagnostics` as admin | **Pass**: uptime, CPU, memory, DB reachable (~20 ms), login counts; anonymous request → 401 |

Still not tested: anything involving the game client.

## 2026-11-08 — `dev` 48ea407 (+ party loot, + class skill effects)
- Build: 0 warnings, 0 errors.
- Aion.Native.Checks: all pass, incl. 14 party-loot checks (round robin rotation, kinah split, out-of-range member, free-for-all, killer, fallback items valid, real elite kill) and 18 skill-effect checks (self/ally heal, refusal on enemies and other race, cooldown, HoT, buff/debuff apply+expire, DoT ticks, resurrection).
- Python: OK (24 skipped).
- Not verified in-game.

## 2026-11-09 — first real install on Brandon's PC (WSL2 Ubuntu 24.04.5, NAT networking)

| Step | Result |
|---|---|
| `deploy/windows/setup-wsl.ps1` (Windows 11 Pro 25H2, build 26200) | **Pass**: Ubuntu-24.04 with systemd; existing distros and Docker Desktop unaffected |
| Mirrored networking | **Fail on this PC**: internet goes through a Hyper-V external switch, which mirrored mode doesn't carry. Switched to NAT. |
| Data bundle v0.1.0 (`aion-server-data.tar.gz`, 337,376,756 B, sha256 d262fda4…6167, matches GitHub) | **Pass** |
| Install run 1 | **Fail**: `class_skills.py install`: catalogue lacks OURS unlock skill 11000001. Fixed in 0459a99. |
| Install run 2 (`wsl-install.sh --public-address <WSL IP> --self-signed --rates-profile accelerated --max-players 10`) | **Pass**: 9,261 items, 16,279 skills, 44 starter profiles; aion-postgres, aion-host, aion-backup.timer active; no errors in the journal |
| Startup log | "World rates: profile accelerated: experience x5, quest experience x5, Kinah x3, drops x3 … party loot roundrobin, fallback loot on. Players: at most 10. Dungeons: entry limits off" and "Playerbots on" |
| Ports | 443, 3700, 8087, 8091, 8780, 13328 listening |
| Windows → `https://<WSL IP>:8780/api/status` | **Pass**: HTTP 200 |
| Game client | Not yet |
