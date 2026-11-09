# A2Core friends server

A private, noncommercial Aion 2 server for Brandon and 5–10 invited friends, built on the open-source [A2Core](https://github.com/Addiboii/A2Core) emulator (AGPL-3.0, C#/.NET 10 + PostgreSQL). Not public, no registration for strangers, no shop, no donations.

## Status
Builds and starts; accounts work. **Not playable yet**: no client has connected. See HANDOFF.md.

## Requirements
- Server: .NET 10 SDK, PostgreSQL (16+ works), Node 20+ (web build), Python 3, the A2Core server data bundle.
- Players: Windows, a Steam account that owns Aion 2, the A2Core launcher (downloads the supported client build 2.0.6.0 with their own Steam account), Tailscale.

## Docs
| File | What |
|---|---|
| HANDOFF.md | Current status and next steps |
| ROADMAP.md | Ordered priorities |
| FEATURE_MATRIX.md | What the code implements, per system, with evidence |
| SERVER_AUDIT.md | Ports, config, security, launcher, backups, client version |
| TEST_RESULTS.md | What actually ran and passed |
| KNOWN_ISSUES.md | Blockers and bugs |
| CHANGELOG.md | Changes made |
| ENCHANT.md | Enchanting odds (`Aion:Enchant`): faster than official by default |

## Run locally (Linux / WSL2, verified 2026-11-08 without the data bundle)
```bash
sudo apt-get install -y dotnet-sdk-10.0 postgresql nodejs npm
git clone https://github.com/Addiboii/A2Core && cd A2Core
# as a non-root user:
export AION_PG_HOME=~/aion/postgres
python3 tools/postgres_dev.py setup          # cluster on 127.0.0.1:55432 + migrations
dotnet build server/Aion.Host
# env: ConnectionStrings__Aion / __Auth from ~/aion/postgres/credentials.json (aion_app / aion_auth),
# Aion__Native__LoginPort=3700 Aion__Native__GamePort=13328 Aion__ChatPort=8091
# Aion__Network__BindAddress=127.0.0.1 Aion__Accounts__Registration=true
dotnet server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll
```
Stop: Ctrl+C (host), `python3 tools/postgres_dev.py stop` (database).
The full hosted recipe (data bundle, catalog import, TLS, admin role) is in SERVER_AUDIT.md.
