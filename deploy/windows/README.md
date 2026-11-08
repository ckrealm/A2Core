# Hosting the server on a Windows 11 PC for friends (Tailscale)

For a small private server (5-10 friends) on your own Windows 11 PC. Nothing is opened to the internet: your friends
reach the PC over [Tailscale](https://tailscale.com), and the server announces the PC's Tailscale address.

Two ways to run it:

- **A. WSL2 + Ubuntu 24.04 (recommended).** The supported Linux installer (`deploy/linux/install.sh`) runs unchanged
  inside WSL: systemd services, nightly backups, `update.sh`, the same layout as a Linux server
  (`deploy/linux/README.md`). Scripts here: `setup-wsl.ps1` (Windows side), `wsl-install.sh` (inside Ubuntu),
  `renew-cert.ps1` (Tailscale certificate).
- **B. Native Windows** (the host and `tools/postgres_dev.py`'s Windows mode, as on a development PC). No services,
  no backup timer, no update script: everything by hand. Only if WSL is not an option.

Every command below was checked against the repository's scripts. Statements marked **UNVERIFIED** could not be tried
on a real Windows 11 + Tailscale machine when this was written; please report what you see.

## What runs where

| Port (TCP) | Service | Who connects |
|---|---|---|
| 443 | NCGuard sink (the launcher points the client's NC hostnames here via the hosts file) | game client |
| 8087 | platform bootstrap | game client |
| 3700 | login | game client |
| 13328 | game | game client |
| 8091 | chat (HTTP + WebSocket) | game client |
| 8780 | account site and API (HTTPS) | launcher, browser |

Port 443 must be free on Windows too (mirrored networking shares ports with Windows): stop IIS, VMware's shared VM
service, or anything else listening there (`Get-NetTCPConnection -LocalPort 443 -State Listen`).

The server must be given an **IPv4** address (`tools/server_env.py` and the launcher's `serverAddress` accept only
IPv4): that is your PC's Tailscale address, `100.x.y.z` (Windows: `tailscale ip -4`). The account site's name is your
PC's MagicDNS name, `<machine>.<tailnet>.ts.net` (Windows: `tailscale status --json`, field `Self.DNSName`).

Needs: Windows 11 22H2 or later (build 22621+, for mirrored networking), virtualisation on in the firmware, about
20 GB free disk (server data 2-3 GB, .NET SDK, Node, build output), and 3 GB RAM for the server while it runs. WSL
gives Linux half the PC's memory by default; on an 8 GB PC add `memory=6GB` under `[wsl2]` in `.wslconfig`.

---

## A. WSL2 Ubuntu 24.04 (recommended)

### A1. Tailscale

1. Install Tailscale for Windows and sign in.
2. In the Tailscale admin console, **DNS** page: turn on **MagicDNS** and **HTTPS Certificates**. (Certificates for
   `ts.net` names are issued by Let's Encrypt and so appear in the public Certificate Transparency logs: your machine and
   tailnet names become public, nothing else.)

### A2. Windows side: `setup-wsl.ps1`

Get the repository onto Windows (git clone, or download `deploy/windows/setup-wsl.ps1` and `renew-cert.ps1` alone).
In **PowerShell as administrator**, as the Windows user who will own the server:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
deploy\windows\setup-wsl.ps1 -WhatIf          # shows what it would change
deploy\windows\setup-wsl.ps1                  # does it; run again after the restart it may ask for
```

It is idempotent and touches nothing but what it lists:

1. Checks the Windows build (22621 or later).
2. Enables WSL (`wsl --install --no-distribution`) if missing, then asks you to restart Windows and run it again.
3. Installs `Ubuntu-24.04` if missing (`wsl --install -d Ubuntu-24.04`; you create the Linux user, then `exit`).
4. Merges into `%UserProfile%\.wslconfig` (backup `.wslconfig.bak-<time>` first; other settings kept):

   ```ini
   [wsl2]
   networkingMode=mirrored

   [experimental]
   hostAddressLoopback=true
   ```

   `mirrored` puts WSL on Windows' own network interfaces, including Tailscale's, so friends reach the server on the
   PC's Tailscale address. `hostAddressLoopback` lets Windows programs (your own game client) reach a WSL server on
   one of the PC's own addresses. **UNVERIFIED**: Tailscale's interface showing up inside WSL in mirrored mode, and
   your own client reaching `100.x.y.z` through `hostAddressLoopback`. If `ip -4 addr` in Ubuntu shows no `100.x`
   address, pass `--tailscale-ip` to `wsl-install.sh` (the server binds 0.0.0.0, so the address is only what it
   announces).
5. Sets `[boot] systemd=true` in Ubuntu's `/etc/wsl.conf` (backup first). The installer needs systemd for its
   services; Ubuntu 24.04 from the Store usually has it already.

Optional switches (also idempotent):

- `-ConfigureFirewall`: inbound TCP 443, 3700, 8087, 8091, 8780, 13328 **only from 100.64.0.0/10** (Tailscale's
  address range): a Windows Defender Firewall rule `A2Core-Tailscale-TCP` (also limited to the `Tailscale` adapter when
  one has that name) and a Hyper-V firewall rule `A2Core-WSL-Tailscale-TCP` for WSL (mirrored WSL sits behind the
  Hyper-V firewall, which blocks inbound by default). Nothing is opened to your LAN or the internet, and no rule is
  needed on your router. **UNVERIFIED**: the Hyper-V rule's parameters (`New-NetFirewallHyperVRule -LocalPorts
  -RemoteAddresses -VMCreatorId {40E0AC32-46A5-438A-A0B2-2B479E8F2E90}`); the script checks the parameter exists and
  warns instead of adding an unscoped rule. Check with `Get-NetFirewallHyperVRule -Name A2Core-WSL-Tailscale-TCP`.
- `-RegisterStartupTask`: a scheduled task **A2Core WSL keepalive** that runs `wsl.exe -d Ubuntu-24.04 -u root --exec
  /bin/sleep infinity` at your logon (hidden), so the distribution, its systemd and the server keep running without an
  open terminal. No time limit, restarts on failure, runs on battery. Add `-AtStartup` to start it at boot before
  anyone logs on (S4U logon). **UNVERIFIED**: WSL started from a non-interactive (S4U) task; if the server does not
  come up after a reboot without logon, register it again without `-AtStartup`.

Then apply the settings: `wsl --shutdown` (stops all WSL distributions), open Ubuntu-24.04 again, and check
`ps -p 1 -o comm=` prints `systemd`.

Keep the PC awake while friends play: Settings > System > Power > Screen and sleep > "Make my device sleep after"
Never (plugged in), or `powercfg /change standby-timeout-ac 0`.

### A3. The certificate: `renew-cert.ps1`

The launcher talks to the account site (port 8780) with .NET's default `HttpClient` and no certificate pinning
(`launcher/Aion.Launcher.Core/AccountClient.cs`): it trusts what Windows trusts. A certificate from a public CA
therefore works on every friend's PC with nothing to install, and `tailscale cert` gives exactly that (Let's Encrypt,
for the `ts.net` name). A self-signed certificate (`--self-signed`) would fail in the launcher unless every friend
imports it into Windows' trusted roots. Let's Encrypt through `install.sh --domain` does not apply: it needs port 80
reachable from the internet and a name you control.

In PowerShell (your normal user, not elevated):

```powershell
deploy\windows\renew-cert.ps1             # tailscale cert for this PC's MagicDNS name
deploy\windows\renew-cert.ps1 -Register   # once: weekly renewal task "A2Core certificate renewal" (Mondays 05:00)
```

It runs `tailscale cert --cert-file ... --key-file ... <machine>.<tailnet>.ts.net` into
`%LocalAppData%\A2Core\tls\` (`cert.pem`, `key.pem`; readable by your Windows user only) and prints the WSL paths of
the two files (`/mnt/c/Users/<you>/AppData/Local/A2Core/tls/...`) for the next step.

**Renewal.** The certificate lasts 90 days. `tailscale cert` hands back the stored certificate while it is still fresh
and fetches a new one when it is due, so the weekly task keeps it current. When the task gets a certificate that
differs from the server's `/etc/aion/tls/cert.pem`, it copies cert and key there (`root:aion`, mode 640, as
`install.sh` does) and restarts `aion-host` (the host loads the certificate at startup; players online are disconnected
for about a minute). `update.sh` also copies the files again, because `install.sh --certificate/--key` copies from the
paths saved in `/etc/aion/install-args`. **UNVERIFIED**: `tailscale cert` from a scheduled task as a standard user,
and its exact renewal threshold.

### A4. Ubuntu side: repository, data, `wsl-install.sh`

Open **Ubuntu-24.04**. Clone the repository into the Linux file system (not `/mnt/c`: builds there are slow and file
modes break; `wsl-install.sh` refuses it), and put the server data bundle (`aion-server-data.tar.gz` from the Releases
page) in `data/`:

```bash
sudo apt-get update && sudo apt-get install -y git
git clone https://github.com/<owner>/<repo>.git ~/a2core
cp /mnt/c/Users/<you>/Downloads/aion-server-data.tar.gz ~/a2core/data/
cd ~/a2core
sudo deploy/windows/wsl-install.sh --data data/aion-server-data.tar.gz \
    --certificate /mnt/c/Users/<you>/AppData/Local/A2Core/tls/cert.pem \
    --key /mnt/c/Users/<you>/AppData/Local/A2Core/tls/key.pem \
    --rates-profile accelerated --max-players 10
```

What `wsl-install.sh` does:

1. Checks it runs under systemd, from outside `/mnt`, and (with `wslinfo`) that networking is mirrored.
2. Finds the Tailscale address: the one IPv4 address in 100.64.0.0/10 on this machine's interfaces, or
   `--tailscale-ip 100.x.y.z`.
3. Installs `dotnet-sdk-10.0`, `postgresql` (16 on 24.04), `python3`, `openssl` from the Ubuntu archive
   (`dotnet-sdk-10.0` is in `noble-updates`). The web build (Vite 8) needs Node.js ^20.19 or >=22.12 and Ubuntu 24.04's
   `nodejs` is 18, so it adds NodeSource's signed Node.js 22 repository (`deb.nodesource.com/node_22.x nodistro`) and
   installs `nodejs` from it (removing Ubuntu's `npm` package if present: it conflicts with NodeSource's `nodejs`, which
   includes npm). `install.sh` now keeps a new enough Node and only installs Ubuntu's `nodejs npm` otherwise, so
   `update.sh` does not undo this. **UNVERIFIED** here: the NodeSource step (its server was not reachable from the
   test machine); the rest of the package list was checked against Ubuntu 24.04's archive.
4. Writes the rates and rules you pass into `/etc/aion/game.env` (see "Rates and rules").
5. Runs `deploy/linux/install.sh --public-address <100.x.y.z> --certificate <cert> --key <key> [--data <bundle>]
   --skip-packages`. That is the normal Linux install (`deploy/linux/README.md`): service account `aion`, the
   database cluster on 127.0.0.1:55432, the host in `/opt/aion/host`, configuration in `/etc/aion`, data and state in
   `/var/lib/aion`, services `aion-postgres`, `aion-host`, and the nightly `aion-backup.timer`.

Use `--self-signed` instead of `--certificate/--key` only for a quick local test (see A3). Later runs: no `--data`.

### A5. First admin account

Registration on the account site is closed by default (`Aion__Accounts__Registration=false` in `aion.env`). Open it
for a minute, register yourself, make that account admin, close it again:

```bash
echo 'Aion__Accounts__Registration=true' | sudo tee -a /etc/aion/game.env
sudo systemctl restart aion-host
# Browser: https://<machine>.<tailnet>.ts.net:8780 -> register your account
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role <username> admin
sudo sed -i '/^Aion__Accounts__Registration=/d' /etc/aion/game.env
sudo systemctl restart aion-host
```

(`game.env` is read after `aion.env`, so its value wins while the line is there.) The staff dashboard's address is
printed at the end of the install (`grep StaffPath /etc/aion/aion.env`); from there you create your friends' accounts
(New account) and grant gamemaster roles.

### A6. How friends connect

1. **Tailscale.** Either *share this machine* with each friend (admin console > Machines > your PC > Share; they accept
   with their own Tailscale account and see only this PC), or *invite* them as users of your tailnet (then they see
   all your devices unless you restrict it with access controls, and your plan's user limit applies). Sharing is the
   tighter choice. They install Tailscale and sign in. **UNVERIFIED**: that MagicDNS resolves your `ts.net` name on a
   shared-in machine; if `https://<machine>.<tailnet>.ts.net:8780` does not open for a friend, a hosts-file line
   `100.x.y.z <machine>.<tailnet>.ts.net` on their PC fixes it (the certificate stays valid: it is the name that counts).
2. **Launcher.** Build the launcher for your server (top-level `README.md`, "Build the launcher") with
   `launcher/launcher.json`:

   ```json
   {
     "name": "Friends server",
     "accountUrl": "https://<machine>.<tailnet>.ts.net:8780",
     "serverAddress": "100.x.y.z",
     "bootstrapPort": 8087,
     "savedDirSuffix": "A2CorePrivate"
   }
   ```

   `serverAddress` must be the IPv4 Tailscale address (the launcher rejects names and IPv6); `accountUrl` must be
   `https://` with the certificate's name. A `launcher.json` next to the exe overrides the embedded one. Put the exe
   where signed-in players can download it from the account page:

   ```bash
   sudo install -o aion -g aion -m 644 "/mnt/c/<path>/A2Core Launcher.exe" /var/lib/aion/downloads/
   ```

3. They sign in with the account you made, and play. The launcher writes the hosts entries (NC hostnames to
   `serverAddress`) and its firewall rules through its own Windows prompts; it must not be run as administrator.

### A7. Rates and rules

`wsl-install.sh` writes these options into `/etc/aion/game.env`, replacing only the keys you give (`deploy/linux/README.md`
"Rates and rules" has their meaning): `--rates-profile official|accelerated`, `--exp-rate`, `--quest-exp-rate`,
`--kinah-rate`, `--drop-rate` (0-100), `--max-players N` (0: no cap), `--respawn-seconds` (5-3600), `--pvp on|off`.
To change them later without reinstalling:

```bash
sudo deploy/windows/wsl-install.sh --rates-only --rates-profile official --max-players 8
journalctl -u aion-host | grep "World rates" | tail -1   # what the host uses
```

### A8. Start, stop, status, update

The services start with the distribution (and the keepalive task keeps the distribution running).

```bash
systemctl status aion-host aion-postgres
sudo systemctl stop aion-host           # stop the server (the database keeps running)
sudo systemctl start aion-host
sudo systemctl restart aion-host
journalctl -u aion-host -f              # live log
cd ~/a2core && sudo deploy/linux/update.sh   # backup, git pull, rebuild, restart (players get a 1-minute warning)
```

From Windows: `wsl -d Ubuntu-24.04 -u root -- systemctl status aion-host`. Stopping everything: `wsl --terminate
Ubuntu-24.04` (or `wsl --shutdown` for all distributions); with the keepalive task, `Stop-ScheduledTask -TaskName
'A2Core WSL keepalive'` first, or it keeps (re)starting it.

### A9. Backups

`install.sh` installs `deploy/linux/backup.sh` and `restore.sh` to `/opt/aion/bin` and enables `aion-backup.timer`:
every night at 04:30 (up to 15 minutes later; a night missed while the PC was off runs at the next start),
`backup.sh` makes `/var/lib/aion/backups/nightly-<UTC time>.dump` with `postgres_dev.py backup` (pg_dump custom
format), checks it with `postgres_dev.py verify-backup` (restores it into a disposable database and prints the row
counts), and keeps the newest 14 nightly files. A backup that fails the check is discarded and nothing is pruned.
`update.sh`'s `before-update-*.dump` (newest 10) and the dashboard's `aion-*.dump` are not touched by it.

```bash
systemctl list-timers aion-backup.timer        # next run
sudo systemctl start aion-backup               # back up now
journalctl -u aion-backup                      # results
echo 'AION_BACKUP_KEEP=30' | sudo tee /etc/aion/backup.env   # keep 30 instead of 14
```

The backups live inside the WSL disk image. Keep copies outside it, for example a Windows scheduled task (daily,
05:30) running:

```powershell
wsl.exe -d Ubuntu-24.04 -u root --exec /bin/sh -c "mkdir -p /mnt/d/a2core-backups && cp -u /var/lib/aion/backups/*.dump /mnt/d/a2core-backups/"
```

(nothing prunes that folder: delete old copies yourself). **UNVERIFIED** on Windows; the copy command itself is plain.

**Restore test** (do this once after the first nightly backup, and now and then): restores the file into a
disposable database, prints the counts, drops it; the live database is not touched.

```bash
sudo /opt/aion/bin/restore.sh --check /var/lib/aion/backups/nightly-<time>.dump
```

**Restore** (replaces the live database):

```bash
sudo /opt/aion/bin/restore.sh /var/lib/aion/backups/nightly-<time>.dump         # check only, then refuses
sudo /opt/aion/bin/restore.sh --yes /var/lib/aion/backups/nightly-<time>.dump   # replace
```

With `--yes` it checks the file as above, writes a safety backup of the live database
(`before-restore-<time>.dump`), stops `aion-host`, restores into a new database, and only when that succeeded renames
the live database to `aion_pre_restore_<time>` and the new one to `aion`, then starts `aion-host` again. The old
database stays until you drop it: `sudo /opt/aion/bin/restore.sh --list-old`, then
`sudo /opt/aion/bin/restore.sh --yes --drop-old aion_pre_restore_<time>`.

### A10. Uninstall

Copy any backups you want to keep out of `/var/lib/aion/backups` first. In Ubuntu:

```bash
sudo systemctl disable --now aion-backup.timer aion-host aion-postgres
sudo rm /etc/systemd/system/aion-backup.timer /etc/systemd/system/aion-backup.service \
        /etc/systemd/system/aion-host.service /etc/systemd/system/aion-postgres.service
sudo systemctl daemon-reload
sudo rm -rf /opt/aion /etc/aion /var/lib/aion     # program, configuration, database, data, backups
sudo userdel aion
```

On Windows (PowerShell as administrator):

```powershell
Unregister-ScheduledTask -TaskName 'A2Core WSL keepalive' -Confirm:$false
Unregister-ScheduledTask -TaskName 'A2Core certificate renewal' -Confirm:$false
Remove-NetFirewallRule -Name A2Core-Tailscale-TCP
Remove-NetFirewallHyperVRule -Name A2Core-WSL-Tailscale-TCP
Remove-Item -Recurse "$env:LOCALAPPDATA\A2Core"
```

Restore your previous `.wslconfig` from `%UserProfile%\.wslconfig.bak-<time>` if you want NAT networking back. Only if
nothing else lives in it: `wsl --unregister Ubuntu-24.04` deletes the whole distribution and everything in it.

---

## B. Native Windows (alternative)

The host and the database run directly on Windows, as on a development PC. **UNVERIFIED as a hosting setup**: these
are the same tools the Linux installer uses, run by hand; there is no service, no timer and no update script. Needs
the .NET 10 SDK, Python 3.11+ (`hashlib.file_digest`), Node.js 22.12+ and Git for Windows.

```powershell
# Database: tools/postgres_dev.py's Windows mode extracts the EDB binaries zip found in AION_PG_HOME.
$env:AION_PG_HOME = "C:\A2Core\postgres"
New-Item -ItemType Directory -Force C:\A2Core\postgres, C:\A2Core\data, C:\A2Core\state | Out-Null
Invoke-WebRequest https://get.enterprisedb.com/postgresql/postgresql-18.6-1-windows-x64-binaries.zip -OutFile C:\A2Core\postgres\postgresql-18.6-1-windows-x64-binaries.zip
tar -xzf data\aion-server-data.tar.gz -C C:\A2Core\data
python tools\postgres_dev.py setup                 # cluster on 127.0.0.1:55432, credentials.json, migrations
python tools\postgres_dev.py import-catalog --directory C:\A2Core\data\game-catalog-v2
python tools\configure_starters.py
python tools\class_skills.py install
python tools\equipment_rules.py
# Host: web build into wwwroot, then publish.
cd web; npm ci --no-audit --no-fund; npm run build; cd ..
dotnet publish server\Aion.Host\Aion.Host.csproj -c Release -o C:\A2Core\host --nologo
# Environment file (same as on Linux), with the Tailscale address and tailscale cert files (renew-cert.ps1 -OutDir).
python tools\server_env.py --public-address 100.x.y.z --data C:\A2Core\data --state C:\A2Core\state `
    --certificate C:\A2Core\tls\cert.pem --key C:\A2Core\tls\key.pem --rates-profile accelerated --max-players 10 > C:\A2Core\aion.env
```

`configure_starters.py`, `class_skills.py` and `equipment_rules.py` are the steps `install.sh` runs on a first
install; they find the database through `AION_PG_HOME` like `postgres_dev.py`. Start (after every reboot):

```powershell
$env:AION_PG_HOME = "C:\A2Core\postgres"
python tools\postgres_dev.py start
Get-Content C:\A2Core\aion.env | Where-Object { $_ -match '^[A-Za-z_][A-Za-z0-9_]*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2; [Environment]::SetEnvironmentVariable($name, $value, 'Process') }
Set-Location C:\A2Core\host; dotnet Aion.Host.dll
```

Stop: Ctrl+C in the host's window, then `python tools\postgres_dev.py stop`. Firewall: `-ConfigureFirewall` of
`setup-wsl.ps1` adds the Windows rule this needs (the Hyper-V rule is harmless here); the first admin, launcher,
Tailscale and certificate steps are as in A3, A5 and A6 (registration: add `Aion__Accounts__Registration=true` to
`aion.env` and restart the host). Backups: `python tools\postgres_dev.py backup C:\A2Core\backups\<name>.dump` and
`python tools\postgres_dev.py verify-backup <file>`; there is no retention or schedule on this path.

## What was checked

- `bash -n` and `shellcheck` on `wsl-install.sh`, `deploy/linux/backup.sh`, `restore.sh`; PowerShell 7 parses both
  `.ps1` files; `setup-wsl.ps1`'s `.wslconfig` merge and the `/etc/wsl.conf` edit were tried on sample files.
- `backup.sh` (backup, verify, retention) and `restore.sh` (`--check`, refusal without `--yes`, `--yes` swap,
  `--list-old`, `--drop-old`) ran against a real cluster made by `postgres_dev.py setup` with Ubuntu 24.04's
  PostgreSQL 16 (all migrations apply on 16); grants and ownership survived the swap.
- `tests/test_windows_deploy.py`: scripts exist and parse, backup retention and failure handling against a stub
  `postgres_dev.py`, restore refusal, `game.env` merging, the systemd units' paths.
- .NET loads a `tailscale cert`-style certificate (ECDSA P-256 key, `EC PRIVATE KEY` PEM) with
  `X509Certificate2.CreateFromPemFile`, which the host uses.
- Not run: anything on Windows itself (WSL, Tailscale, firewall, scheduled tasks), the NodeSource step, a full
  `install.sh` inside WSL.
