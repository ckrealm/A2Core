# Server, launcher and security audit (A2Core @ 2b5f72c)

> Source read 2026-11-08. Startup recipe partially verified; see TEST_RESULTS.md.


Repo: `/home/user/work/A2Core` (not modified). Paths below are relative to it.
Use case: a private, noncommercial server for 5-10 friends on Windows 11, possibly over Tailscale.

---

## 1. Startup and runtime

### Configuration surface
There are **no `appsettings*.json` files**. Everything comes from ASP.NET configuration, which in practice means environment variables using `__` as the separator. `tools/server_env.py` generates them. The host never calls `GetEnvironmentVariable` itself.

| Key (env form) | Read at | Default / notes |
|---|---|---|
| `Aion__Network__BindAddress`, `Aion__Network__PublicAddress` | Program.cs:21, NetworkOptions.cs:14-30 | both `127.0.0.1`. PublicAddress must be IPv4. A non-loopback bind needs a non-loopback PublicAddress |
| `ConnectionStrings__Aion`, `ConnectionStrings__Auth` | Program.cs:24-25 | without both, there are no accounts (`accountsReady`, :38) |
| `Aion__ChatPort` | :53 | unset = no chat |
| `Aion__ChatServerKey` | :60 | `1204` |
| `Aion__ChatDevelopmentLogins` | :67 | dev only. Accepts any character name |
| `Aion__Native__*` (LoginPort, GamePort, Logins=`accounts`/`development`, MaxConnections, TerrainDirectory, NavMeshDirectory, PreloadMaps__N, TraceDirectory, IntroCharacter, DevelopmentGmNames__N...) | :78, Native/NativeListeners.cs:15-53 | Logins `accounts` throws without both DB strings (:81-82) |
| `Aion__Platform__*` (Port, FixtureDirectory, PolicyFile, PolicyPlaintextFile, GeolocationFile, LaunchAuthorizationFile, RequestLog, CustomData__X) | :193, Platform/PlatformBootstrapEndpoints.cs:18-39 | |
| `Aion__NcGuard__*` (Port, FixtureFile, DataDirectory, Hosts, BodyCap, RewriteHosts, DurationSeconds, StopFile, Capture) | :197, NcGuard/NcGuardSink.cs:12-26 | **Capture defaults to true** (:23). server_env.py sets it to false (:110) |
| `Aion__LocalPort` | :207 | 8780 |
| `Aion__Https__CertificateFile`, `__KeyFile`, `__Proxy` | :208-216 | a non-loopback bind **requires** a cert or Proxy (:209-210) |
| `Aion__Web__StaffPath` | :266 | private dashboard path. Obscurity only |
| `Aion__WardrobePath`, `Aion__Wardrobe__StaffOnly` | :289-290 | |
| `Aion__Web__Downloads` | :306 | launcher exe downloads |
| `Aion__Accounts__Registration` | :309 | **the code defaults to true. server_env.py writes `false`** (:89) |
| `Aion__BridgeSecret` | :319 | ≥64 chars enables the dev client-handoff endpoints. Not set in production |
| `Aion__Backups__Directory`, `__Tool`, `__PgHome` | Authentication/StaffToolsEndpoints.cs:244-262 | dashboard backups |
| `ASPNETCORE_ENVIRONMENT` | server_env.py:84 | Production |

Shell and Python environment variables used by the scripts:
- `AION_PG_HOME` and `AION_PG_BIN`: tools/postgres_dev.py:22, :30
- `AION_CHAT_PORT` and `AION_UPDATE_NOW`: update.sh:11-13
- `AION_LAUNCHER_NO_UPDATE`: launcher
- `AION_TEST_*` and `AION_POSTGRES_TESTS`: tests only

### Ports and services (tools/server_env.py:57, deploy/linux/README.md "Ports")
| Port | Service | Proto | Code |
|---|---|---|---|
| 8780 | account site and API, staff dashboard | HTTPS (Kestrel) | Program.cs:221-224 |
| 8087 | platform bootstrap (`GamePlatform.ini ConfigurationServerUrl`) | plain HTTP | Program.cs:226-227, PlatformBootstrapEndpoints.cs:366 |
| 3700 | native login | TCP | NativeListeners.cs:195 |
| 13328 | native game | TCP | NativeListeners.cs:258 |
| 8091 | chat (Lime HTTP and STOMP WebSocket) | plain HTTP | Program.cs:226, Chat/LimeChatEndpoints.cs:17-24 |
| 443 | NCGuard sink | TLS (self-generated certs) | NcGuardSink.cs:107-112 |

The task's "platform bootstrap 443" is not quite right: **the bootstrap is 8087 and 443 is the NCGuard sink**.

Other runtime behaviour:
- All listeners bind to `Aion:Network:BindAddress`.
- A loopback-only host also refuses non-loopback peers on the native ports (NativeListeners.cs:81).
- Health checks are `/health/live`, and `/health/ready`, which always returns 503 (Program.cs:329-333).

**Finding (moderate):** the account API, the staff API and static files are mapped globally, with no port filter.
- The only port filters are the bootstrap branch (8087), the NCGuard middleware (443) and the chat endpoint filter (Program.cs:179, :249; PlatformBootstrapEndpoints.cs:366; LimeChatEndpoints.cs:22).
- As a result, `/api/accounts/login` and the staff path are also reachable over **plain HTTP on 8091**.
- Over Tailscale the link is WireGuard-encrypted, so the impact is small. Still, do not expose 8091 publicly.

### What startup needs
1. **.NET 10 SDK.**
2. **PostgreSQL.** The project manages its own cluster on 127.0.0.1:**55432** (tools/postgres_dev.py:23). It does not use the distro cluster on 5432.
3. **A TLS cert and key (PEM)** for 8780 on any non-loopback bind (Program.cs:209).
4. **The server data bundle** `aion-server-data.tar.gz`. Its layout is defined in tools/pack_server_data.py:1-13 and :52-80:
   - `bootstrap/keys/` holds `app-key.json` (a captured 256-byte RSA ciphertext), `local-test-key.json` (the 16-char AES key used to decrypt and re-encrypt the client policy), plus maintenance, session and authn candidate captures.
   - `bootstrap/policy/app-policy.json`
   - `ncguard/fixture.json`
   - `terrain/`, `navmesh/`
   - `game-catalog-v2/{manifest.json,entries.jsonl}`

### What happens if parts of the bundle are missing
- **install.sh** aborts if `bootstrap/keys/app-key.json` is absent (install.sh:60).
- **server_env.py** aborts if the key, policy or NCGuard fixture is absent (server_env.py:77-82).
- **Platform bootstrap:** without fixtures, every bootstrap request answers 503 `{"ready":false}` (PlatformBootstrapEndpoints.cs:330, :378). The client cannot get its key or policy, so nothing works.
- **Public host without the AES key:** startup throws (:103-104).
- **NCGuard:** if a `FixtureFile` is configured but missing, the host crashes at startup (NcGuardSink.cs:59). With no fixture configured, every request gets a 404 (:165-166).
- **Terrain and navmesh:** missing directories are tolerated. Loads return null and NPCs lose ground height and pathing (Aion.World/Terrain.cs:66-88, NavMesh.cs:234-256).
- **Catalogue:** without it, `class_skills.py install` and `equipment_rules.py` fail ("Choose an imported catalogue", class_skills.py:216-218), and item grants fail with `no_catalogue` (Program.cs:140).
- The game tables in `tools/data/*.json` (34 MB) are **embedded into the build** (Aion.World.csproj:17-41).

**Contradiction to note:**
- tools/pack_server_data.py:11-12 and deploy/linux/README.md say the bundle holds "captures and extractions from the game: copy the archive only to your own server; it is not for redistribution".
- The top-level README says it is published on the Releases page.
- It contains captured official-service responses, a captured RSA blob, the policy AES key and client-derived data. NOTICE.md says this game data is not covered by the AGPL.

### Linux only or Windows?
- The **host is cross-platform.** Program.cs:213-216 has Windows/Schannel-specific code paths, and NcGuardSink.cs:95-98 does too.
- tools/postgres_dev.py supports Windows by unpacking `postgresql-18.6-1-windows-x64-binaries.zip` (EnterpriseDB) placed in `AION_PG_HOME` (:28-34, :82-101). You must download that zip yourself.
- deploy/linux/README.md "Not covered yet" says: "Only tested on Windows so far: the Linux scripts and units have not run on a real Ubuntu host yet."
- **Windows native works but is undocumented:** there is no service wrapper and no install script. The dev runners (`tools/run_game_chain.py`, `run_account_server.py`) are Windows-oriented. The server_env.py and postgres_dev.py flow works on Windows too.
- **One Windows conflict:** the NCGuard sink must own TCP 443 on the bind address, and so does anything else on the host that listens on 443 (IIS, for example).

### What deploy/linux/install.sh does, in order
1. Installs `dotnet-sdk-10.0`, `postgresql`, `python3`, `openssl`, `nodejs` and `npm` (:38-42).
2. Creates the `aion` user and `/var/lib/aion`, `/etc/aion` and `/opt/aion` (:49-53).
3. Untars the bundle into `/var/lib/aion/data` (:56-59).
4. Builds the web UI with `npm ci && npm run build` into `server/Aion.Host/wwwroot`, then `dotnet publish -c Release` into `/opt/aion/host` (:66-68).
5. Database steps as the aion user, with `AION_PG_HOME=/var/lib/aion/postgres` (:70-87):
   - `postgres_dev.py setup`, which runs initdb (scram-sha-256, loopback only, port 55432, no Unix socket), creates roles `aion_owner`/`aion_app`/`aion_auth` with random passwords in `credentials.json`, creates database `aion` and applies `database/migrations/*.sql`. Migrations are SHA-checked and recorded in `schema_migrations` (postgres_dev.py:80-158).
   - **First run only:** `import-catalog --directory $DATA/game-catalog-v2`, then `configure_starters.py`, then `class_skills.py install`, then `equipment_rules.py`.
   - Later runs only `migrate`.
6. Sets up TLS from one of four sources: your own files, self-signed (`openssl` with SAN=IP, 825 days), Let's Encrypt via certbot standalone on :80, or a proxy (:89-114).
7. Writes `/etc/aion/aion.env` via `server_env.py` (:119-120).
8. Installs and enables `aion-postgres.service` and `aion-host.service` (:122-127). The host unit runs with `CAP_NET_BIND_SERVICE` for 443 and `ProtectSystem=strict`.
9. Opens these ports in ufw: 443, 3700, 8087, 8091, 13328 and 8780 (:129-132).
10. Saves the install arguments to `/etc/aion/install-args`.

### Creating an account and making it admin
1. The README says to register at `https://<server>:8780`, then run:
   `sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role <username> admin`
   This calls `role_statement`, which inserts into `auth.account_roles` (postgres_dev.py:197-209). The roles are `gamemaster` and `admin`.
2. **Bootstrap gap:** server_env.py writes `Aion__Accounts__Registration=false` (:89), and install.sh does not pass `--open-registration`. Self-registration therefore returns 403 (AccountEndpoints.cs:26).
3. The only other way to create an account is `POST /api/admin/accounts`, which needs an existing admin (AccountEndpoints.cs:34-48).
4. To create the first account, do one of the following, then make it admin as in step 1 and remove the flag again:
   - set `Aion__Accounts__Registration=true` in `/etc/aion/aion.env` temporarily and restart, register, then set it back; or
   - re-run server_env.py with `--open-registration`.
5. Username and password rules: usernames match `[A-Za-z0-9_]{3,32}`. Passwords are 12-256 characters (PostgresAccountAuthentication.cs:26-28).

---

## 2. Security-relevant pieces

### NCGuard sink (server/Aion.Host/NcGuard/NcGuardSink.cs)
- It is a **local fixture server that impersonates NC's anti-cheat and telemetry backends**. The six hostnames are `bladnoch.ncsoft.com`, `lagavulin.ncsoft.com`, `atp-json.ncsoft.com`, `sdk.gpa.ncsoft.com`, `stun.gpa.ncsoft.com` and `nccr-api.global.nc.com` (:47-49).
- At startup it generates its own throwaway CA (`CN=AionResearch loopback CA`, valid 2 days) and one leaf certificate per host (valid 1 day) (:78-100). It picks the leaf by SNI (:110-111, :149-150).
- It **installs nothing into any trust store** (class comment :28: "no trust-store installation (clients accept its own certificates)").
- For each request it reads the body and returns the canned `status/content_type/body` from `fixture.json` for that host, method and path. Unmatched requests get `404 "loopback-mitm: not answered"` (:146-198). It makes **no upstream connection**.
- With `Capture=true` (the code default), it records full request headers and bodies to `DataDirectory` (:173-189). server_env.py turns this off for hosted servers (:109-110).
- `RewriteHosts` (Windows only, dev) appends `127.0.0.1 <host>` lines to the host machine's hosts file and removes them on stop (:116-125, :205-213).
- **What it does to anti-cheat:** it does not patch, inject into or modify the client's NCGuard module. However, it does **substitute NC's anti-cheat server endpoints with recorded answers**, so the client's anti-cheat reports never reach NCSoft and get whatever replies were captured.
- The fixture comes from a capture run, `research/local/ncguard-payload-fetch-20261014-072114/fixture.json` (pack_server_data.py:25). Its provenance and contents cannot be inspected here. **It may contain NC-served payloads.**
- Related research tooling: tools/loopback_tls_mitm.py ("Loopback TLS MITM sink for NCGuard's external HTTPS calls").

### Platform bootstrap (Platform/PlatformBootstrapEndpoints.cs)
It emulates NC's platform config and login-gateway SDK endpoints on 8087 (:379-396):
- `GET /MobileAppConfigSdk/app/key`: the captured RSA-wrapped key.
- `GET /MobileAppConfigSdk/app/policy`: the captured AES-128-CBC policy (key = IV = 16 ASCII chars, :269-276), decrypted with `local-test-key.json` and re-pointed by `PointAtHost` (:180-208). PointAtHost rewrites `api_gate`, `login_web` and `login_api` to `http://<PublicAddress>:8087[/c/<code>]`, `LoginServerIP` to `<addr>:3700` and `ChatServerURL` to `http://<addr>:8091`. It also applies `CustomData` overrides; server_env sets `SecondaryAuthCheckType=None` (:105).
- `POST /MobileAppConfigSdk/app/policy/maintenance`
- `POST /local-api-gate/sessions/v1.0/authn_token`: redeems the launch code for a game ticket (:382-389).
- `POST .../sessions/v1.0/login_sessions;token` (the Steam token route): returns a placeholder session. "the Steam ticket in the body is not read" (:390-392).
- `POST .../secondary_auth/v1.0/my/auth;start` and `;finish`, `GET .../geolocations/v1.0/me`, and `GET .../sessions/v1.0/<uuid>` (placeholder session).

Identity comes only from the A2Core launch code. It does not contact NC or Steam auth services.

### How the official client is redirected (launcher)
- **GamePlatform.ini:** written only into a private saved folder, `%LOCALAPPDATA%\AION2\Saved_A2CorePrivate\Config\Windows\GamePlatform.ini`. It sets `ConfigurationServerUrl="http://<server>:8087/c/<launch code>"` and points the NC updater at `127.0.0.1` (Aion.Launcher.Core/SystemFiles.cs:12-46). `Saved_Steam` is refused as a suffix (LauncherConfig.cs:44-45).
- **Hosts file:** an elevated helper (UAC) adds `<server IP> <host>` for the six NC hostnames between marker comments, then removes them once the private copy's processes exit (SystemFiles.cs:53-92, AionLauncher/Elevated.cs:213-230).
- **Firewall:** an elevated `netsh advfirewall` setup creates rules named "Private game server". For every `.exe` in the private copy, it blocks inbound and outbound traffic to every address except 127/8 and the server IP, including all IPv6 except ::1 (SystemFiles.cs:99-140, Elevated.cs:197-207).
- **Certificates:** the launcher installs none. The NCGuard sink and launcher code contain no `X509Store` or Root-store code (grep returned nothing). The account HTTPS uses the default `HttpClientHandler` with no pinning (AccountClient.cs:20-25). So a **self-signed 8780 cert will fail in the launcher** unless each friend trusts it, despite the README claim that "the launcher must pin it".
- **Steam download:**
  - It uses SteamKit2 with a **QR sign-in in the player's own Steam mobile app**, so no password reaches the launcher (SteamDownload.cs:62-94).
  - It requests the depot decryption key and the manifest request code for depot 3393111, manifest 1699273097574069541, then fetches chunks from Steam's CDN (:120-140). Players who do not own the game get AccessDenied.
  - The refresh token is kept DPAPI-encrypted until install finishes.
  - If the player's Steam install is already that build, it is hard-linked or copied instead (Workflow.cs:47-57, ClientBuild.cs:143-145 `CreateHardLink`).
- **Client binary patching or memory modification: none found.**
  - A grep for `WriteProcessMemory`, `VirtualProtect`, `OpenProcess`, `CreateRemoteThread` and `inject` across launcher/ found nothing.
  - Files are hash-verified *against* the unmodified depot (Workflow.cs:162-172).
  - The game starts unelevated with Steam's own launch args. Only `-saveddirsuffix` is replaced, and an allowlist rejects anything else (Steam.cs:198-212).
- **Nothing passes flags that disable anti-cheat.** NCGuard still runs in the client (launcher/README.md:59-62 mentions `bb64.dll`). Its server side is answered by the sink.

### Keys and other credentials
- **The client archive (pak) AES key is not shipped** (README "Game data"). However, tools/key_constants.py and archive_keys.py are tooling that scans client binaries for candidate AES-256 keys and validates them against pak index hashes. You only need these to re-extract data, not to run the server.
- **The bundle does ship** the policy AES key (`local-test-key.json`) and a captured RSA blob.

### Passwords, sessions and tickets
- **Password hashing:** ASP.NET Core Identity `PasswordHasher` (v3 format, PBKDF2) with **210,000 iterations** (PostgresAccountAuthentication.cs:16-17). Unknown users get a dummy hash compare.
- **Lockout:** 5 failures lock the account for 5 minutes (:~104-110).
- **Rate limits:** account-entry endpoints allow 15 requests/min per IP. Admin endpoints allow 120/min (Program.cs:184-190).
- **Tokens:** 32 random bytes as hex. Only SHA-256 hashes are stored in the database (:29-32).
- **Session lifetimes:** 8 h, or 30 days with "keep me signed in" (:65).
- **Launch code:** 5-minute expiry. Each redemption extends it by 12 h, still bounded by the session (PostgresClientHandoff.cs:9-49).
- **Game ticket:** up to 12 h. The login mints a single-use grant per connection.
- **IP pairing:** a client started without a code redeems the newest launch from its IP (PlatformBootstrapEndpoints.cs:284-326). Friends behind one NAT or one Tailscale exit could collide; the launcher always sends a code.
- The launcher stores session tokens with DPAPI. It never stores passwords.

### Hard-coded credentials and secrets
- **None found in the production paths.**
- Database passwords are generated with `secrets.token_urlsafe(36)` into `credentials.json` (postgres_dev.py:102-110).
- `/etc/aion/aion.env` holds the DB passwords, with mode 640 root:aion.
- The only literal password is in tests (tests/Aion.Authentication.Checks/Program.cs:14).
- Migration 024 inserts a disabled system tombstone account (`...dead`, provider `system`). It cannot log in.
- The chat `/internal/*` endpoints are loopback-only (LimeChatEndpoints.cs:62-80).
- **Unsafe dev switches** (`Aion__Native__Logins=development`, `Aion__ChatDevelopmentLogins=true`) accept unauthenticated clients. Never set them on a friends server.

---

## 3. Backups
- **update.sh** runs `postgres_dev.py backup` (a `pg_dump -Fc`) to `/var/lib/aion/backups/before-update-<ts>.dump` before pulling, and keeps the newest 10 (update.sh:21-28).
- **Staff dashboard** (admin role): the "Server" page runs the same tool and saves `aion-<ts>.dump` into `Aion__Backups__Directory`. Admins can list and download the files (StaffToolsEndpoints.cs:240-298).
- **`postgres_dev.py verify-backup <file>`** restores into a throwaway database, prints counts, then drops it (:173-186).
- **Restore** (deploy/linux/README.md "Updating"): `systemctl stop aion-host`, then `pg_restore --clean --if-exists -h 127.0.0.1 -p 55432 -U aion_admin -d aion <file>`. The password is in `credentials.json`.
- **No scheduled backups** are set up (no cron or timer), and nothing goes off-box. Also back up `/var/lib/aion/postgres/credentials.json` and `/etc/aion`.

---

## 4. Client compatibility
- **Exactly one build is supported:** Steam app 3393110, depot **3393111**, manifest **1699273097574069541**, 1218 files, 87,549,550,890 bytes. `AION2.exe` SHA-256 is `d93b050d…1ba` (launcher/supported-build.json).
- The type mappings are `AION2-5.3.2.0-2.0.6.0.usmap`, which NOTICE.md describes as community mappings for Global **2.0.6.0**.
- **With a newer Steam client:**
  - The launcher sees that the Steam install does not match the manifest or sizes.
  - It then downloads the old manifest from Steam (about 88 GB per player) into `%LOCALAPPDATA%\A2CorePrivate\AION2`.
  - That works only while Steam still issues a manifest request code for it. The README marks this "CANDIDATE".
- **Steam launch config changes:** the launcher refuses to start ("needs an update") if Steam's launch entry or arguments change (Steam.cs:204-209, Workflow.cs:186-187).
- The server has no explicit version check. Opcodes, packet layouts, the captured policy and the NCGuard fixture are all tied to that build, so a newer client would fail.

---

## 5. Local Linux dev recipe (PostgreSQL 16, no systemd)

Derived from install.sh and server_env.py. Not executed.

```bash
cd /home/user/work/A2Core
mkdir -p ~/aion/data ~/aion/state ~/aion/tls
tar -xzf /path/aion-server-data.tar.gz -C ~/aion/data           # bundle from Releases
export AION_PG_HOME=~/aion/postgres AION_PG_BIN=/usr/lib/postgresql/16/bin
python3 tools/postgres_dev.py setup                              # own cluster 127.0.0.1:55432 + migrations
python3 tools/postgres_dev.py import-catalog --directory ~/aion/data/game-catalog-v2
python3 tools/configure_starters.py
python3 tools/class_skills.py install
python3 tools/equipment_rules.py
(cd web && npm ci && npm run build)                              # account page/dashboard into wwwroot
dotnet publish server/Aion.Host -c Release -o ~/aion/host
IP=<LAN or Tailscale 100.x IPv4>
openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=$IP" -addext "subjectAltName=IP:$IP" \
  -keyout ~/aion/tls/key.pem -out ~/aion/tls/cert.pem
python3 tools/server_env.py --public-address $IP --data ~/aion/data --state ~/aion/state \
  --certificate ~/aion/tls/cert.pem --key ~/aion/tls/key.pem --open-registration --max-connections 20 > ~/aion/aion.env
chmod 600 ~/aion/aion.env
sudo setcap cap_net_bind_service=+ep "$(readlink -f "$(command -v dotnet)")"   # NCGuard sink binds 443 (or run as root)
# The values contain ';', so do not `source` the file; export each line literally:
( while IFS= read -r l; do export "$l"; done < ~/aion/aion.env; cd ~/aion/host && exec dotnet Aion.Host.dll )
# register at https://$IP:8780, then:
python3 tools/postgres_dev.py role <username> admin
# then drop --open-registration (regenerate aion.env with --staff-path <current> to keep the dashboard URL)
```

The staff dashboard URL is the `Aion__Web__StaffPath` value in aion.env.

For a fully loopback test with no other machines, set these by hand instead of using server_env.py, which rejects loopback addresses:
- `Aion__Network__BindAddress=127.0.0.1` and `Aion__Network__PublicAddress=127.0.0.1`
- HTTPS is then optional (Program.cs:209).

---

## 6. Notes for the owner's constraints (factual)
- **Leaked code:** none apparent. The code is original under AGPL. Third-party code: CUE4Parse (Apache-2.0); SteamKit2 and QRCoder as NuGet packages. NOTICE.md says other projects were only compared and nothing was copied.
- **Client assets:** the `tools/data/*.json` tables (embedded in the built host) and the data bundle are client-derived and captured data. The bundle's own docs say not to redistribute it. Keep the built host and the bundle private to your server, and do not hand them to friends. Friends only need the launcher exe and their own Steam copy.
- **Authentication:** nothing in the code contacts or bypasses NC or Steam login for the official service. The private client's NC hostnames are redirected only on the player's PC, only while it runs, and firewall rules keep the private copy off NC servers. Steam ownership is still enforced by the depot key request.
- **Anti-cheat:** the client's NCGuard is not patched or disabled. Its backend traffic is answered locally by the sink with recorded responses. Whether that is acceptable under your "no bypassing anti-cheat protections of official services" rule is your call:
  - It affects only the private copy, which is firewalled from NC.
  - It does neutralise the anti-cheat's server reporting for that copy.
  - It uses a captured fixture whose contents are not in the repo.
