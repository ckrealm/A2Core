# Known issues and blockers

## Blockers

1. **Server data bundle not obtained.** `aion-server-data.tar.gz` (item/skill catalogue, starter profiles, terrain, navmeshes, platform bootstrap keys, NCGuard fixture) is published on the upstream Releases page, which this cloud environment cannot reach. Without it there are no items/skills in the DB, no character creation data, and the client bootstrap returns 503. The bundle's own notes say "not for redistribution". It is derived from the game client. Next step: download it on Brandon's PC.
2. **Anti-cheat sink — decided 2026-11-09: Brandon accepted using it for this private server.** For the official client to play on this server, A2Core's NCGuard sink answers the client's anti-cheat check-ins locally, from a captured fixture, under six NC hostnames that the launcher points at the server via temporary hosts entries. It does not patch the client or its anti-cheat, and it never touches NC's real servers. But it is the server side of anti-cheat that is being emulated, which sits close to the "no anti-cheat bypass" line in the handoff.
3. **Client version lock.** Only Steam depot 3393111, manifest 1699273097574069541 (Global 2.0.6.0, ~88 GB) works. The launcher downloads that exact build with the player's own Steam account. If Steam stops serving that manifest, new players can't get a compatible client. Newer clients will not connect.
4. ~~No fork~~ — resolved: work is on `BrandonGWoW/A2Core`, branch `friends-server`.
5. **Client source.** Brandon has Aion 2 on PURPLE, but the launcher only downloads the pinned build through Steam (app 3393110), and it needs the game in the Steam account's library. Aion 2 appears to be free-to-play on Steam, so adding it to the library should be enough (to confirm). A PURPLE install auto-updates past 2.0.6.0 and can't be used.

## Gameplay gaps (from source; see FEATURE_MATRIX.md)

- ~~Heal/buff/debuff skills do nothing~~ fixed on `dev` with OURS values. Remaining: no buff icons/heal numbers on the client; values are guesses until skill-effect data is extracted from the client (`tools/class_skills.py` docstring says how).
- ~~Skills stop at 25 / no skill-level scaling~~ improved on `dev`: OURS unlocks 26–100 from the client's dev-menu kits, +10%/skill level, fallback damage rows (see PROGRESSION.md). Real cooldowns/effects of the new skills are unknown.
- ~~Loot to killer only / no kinah~~ fixed on `dev` (party loot rules, OURS kinah table, fallback equipment loot).
- Dungeons: no entry limits/cooldowns; untracked objectives auto-complete after a delay; boss skills are single-target.
- Parties are in-memory and capped at 4; party/legion chat rooms can't be joined.
- ~~Flat MP / no combat power~~ improved: OURS MP curve, OURS combat power sent in the existing field. Damage formula still a guess.
- Many social/guild/mail/shop/loot packet layouts are CANDIDATE (guessed) and untested against a client.

## Operations

- Account site requires HTTPS on a non-loopback bind; the launcher doesn't pin certs, so a self-signed cert will fail for friends. A Tailscale `ts.net` cert avoids this.
- Hosted config closes registration; the first admin needs registration temporarily on, then `postgres_dev.py role <user> admin`.
- Backups: only a `pg_dump` before each `update.sh` (newest 10). No schedule, no off-machine copy.
- The supported deploy scripts are Ubuntu-only and upstream says they were never run on a real Ubuntu host.

## Test-suite issues

- `tools/check_authentication.py:114` expects the page title "… Account" but the built page says "Sign in".
- `tests/test_dotnet_ncguard_host.py` fails on Python 3.13 (test certificate lacks AKI).
- `tests/test_account_login.py` hits an interactive prompt on Linux.
