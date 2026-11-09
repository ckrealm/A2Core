# Hosting the server on Linux (Ubuntu 26.04 LTS)

One machine runs everything: the .NET host (account site, platform bootstrap, login, game,
chat, NCGuard sink) and its own PostgreSQL cluster. Measured needs: about 2-3 GB of data
(terrain 0.4 GB, navmesh 1.1 GB, catalogue 0.3 GB, database), 2-3 GB RAM with all maps loaded.
4 cores / 8 GB / 80 GB is comfortable for tens of players.

The server needs a public **IPv4** address: the login redirect and the policy's LoginServerIP
carry the address as an IPv4 literal.

## Ports

| Port | Service | Who connects |
|---|---|---|
| 443 | NCGuard sink (TLS, the client's NC hostnames point here via the player's hosts file) | game client |
| 8087 | platform bootstrap (GamePlatform.ini ConfigurationServerUrl) | game client |
| 3700 | login | game client |
| 13328 | game | game client |
| 8091 | chat (HTTP + STOMP WebSocket) | game client |
| 8780 | account site and API (HTTPS) | launcher, browser |
| 80 | only for Let's Encrypt certificate requests (`--domain`) | Let's Encrypt |

Open them in the provider's firewall as well; install.sh opens them in ufw when ufw is active.
Some providers reserve 443 for their own panel: check that it is free.

## 1. On the development PC: pack the server data

```powershell
python tools/pack_server_data.py aion-server-data.tar.gz
```

This collects the bootstrap fixtures, the policy, the NCGuard fixture, terrain, navmesh and the
item/skill catalogue from `research/local` (about 0.6 GB compressed). They are captures and
extractions from the game: copy them only to your own server.

Copy the archive to the server and clone the repository there (see "Getting the code onto the
server" below):

```bash
scp aion-server-data.tar.gz user@server:~
```

## 2. On the server: install

```bash
cd ~/aion
sudo deploy/linux/install.sh --public-address 203.0.113.5 --data ~/aion-server-data.tar.gz \
    --domain aion.example.com --email you@example.com
```

Certificate choices for the account site (port 8780), exactly one:

- `--domain NAME --email ADDR`: Let's Encrypt (DNS A record of NAME must point at the server;
  port 80 open). Renewals copy the new files and restart the host automatically.
- `--self-signed`: a certificate for the IP address. Browsers warn; the launcher must pin it.
- `--certificate cert.pem --key key.pem`: your own files.
- `--proxy`: your own TLS proxy on this machine forwards to `127.0.0.1:8780` (the host then
  listens on loopback for the account site and trusts X-Forwarded-For from loopback). The proxy
  must use another public port: 443 belongs to the NCGuard sink.

What install.sh does:

1. Installs `dotnet-sdk-10.0`, `postgresql`, `python3`, `openssl`, `nodejs`, `npm` (and `certbot` with `--domain`).
   The web build needs Node.js ^20.19 or >=22.12: an installed Node that new (e.g. NodeSource's, which Ubuntu 24.04
   needs, as its own is 18; `deploy/windows/wsl-install.sh` sets it up) is kept and `nodejs`/`npm` are not installed.
2. Creates the `aion` service account and `/var/lib/aion` (state, data, database), `/etc/aion`
   (configuration, certificate), `/opt/aion` (published host, database tools).
3. Unpacks the data archive into `/var/lib/aion/data`.
4. Builds the account page and staff dashboard (React, `web/`) and publishes the host (`dotnet publish -c Release`) to `/opt/aion/host`.
5. First run: creates the database cluster (`tools/postgres_dev.py setup` with
   `AION_PG_HOME=/var/lib/aion/postgres`: 127.0.0.1:55432, generated passwords in
   `credentials.json`), applies migrations, imports the catalogue, installs starter profiles,
   class skills and the equipment rules (`tools/equipment_rules.py`). Later runs apply new migrations only.
6. Writes `/etc/aion/aion.env` (`tools/server_env.py`; mode 640, contains database passwords).
7. Installs and starts `aion-postgres.service` and `aion-host.service`, and enables the nightly `aion-backup.timer`
   (`backup.sh` and `restore.sh` in `/opt/aion/bin`; see "Backups").

## Dashboard icons and 3D preview

The staff dashboard shows item icons and a 3D character preview from the wardrobe export. It is
extracted game data, so the host serves it only to signed-in staff (a short-lived cookie the
dashboard requests after sign-in). To add it:

```powershell
python tools/pack_server_data.py --wardrobe-only aion-wardrobe.tar   # about 2.5 GB
scp aion-wardrobe.tar root@<server>:/root/
```

```bash
sudo tar -xf ~/aion-wardrobe.tar -C /var/lib/aion/data --no-same-owner && sudo chown -R aion:aion /var/lib/aion/data/wardrobe
sudo deploy/linux/update.sh      # rewrites aion.env with Aion__WardrobePath and restarts
```

## Rates and rules

Defaults are made for a small friends server (see docs/friends-server/RATES.md): the `friends` rates profile (experience
and quest experience 5, Kinah 10, Kinah drop chance x2, drops 4, gathering/crafting/pet experience 10), no time gates
(`Aion__TimeGates__*`: no shop purchase caps, no dungeon timers, every collection open, gather nodes back after 10 s, revive
after 5 s; dungeon entry limits stay off), every pet maxed and every wing for every character, crafts always succeed and
pets cost half the souls (`Aion__Unlocks__*`), the `/boost` alt boost (`Aion__Boost__*`), PvP on, monsters respawn
30 s after a kill, no player cap beyond 200 connections per port. `Aion__Rates__Profile=official` gives all rates 1. To change them, put the keys you want in `/etc/aion/game.env`
(install.sh and update.sh rewrite `aion.env` but never touch this file; the service reads it last) and restart:

```bash
sudo tee /etc/aion/game.env >/dev/null <<'ENV'
# friends (default), official (all 1) or accelerated (experience 5, Kinah 3, drops 3); an explicit rate overrides the profile's
Aion__Rates__Profile=friends
Aion__Rates__Experience=10
# every character also gets every skin at its next world entry (pets and wings are on by default)
Aion__Unlocks__AllSkins=true
# refuse logins beyond 10 players (0 or unset: no cap)
Aion__Native__MaxPlayers=10
# no duels, no arena queue, no player damage
Aion__Rules__Pvp=false
# monster respawn after a kill, 5-3600
Aion__Rules__RespawnSeconds=15
# a party member's kill: freeforall, roundrobin (default) or killer
Aion__Rules__PartyLoot=roundrobin
ENV
sudo chown root:aion /etc/aion/game.env && sudo chmod 640 /etc/aion/game.env
sudo systemctl restart aion-host
journalctl -u aion-host | grep "World rates"   # the effective rates, logged at startup
```

The other keys are `Aion__Rates__Kinah`, `Aion__Rates__Drop`, `Aion__Rates__QuestExperience` (default: the experience
rate), `Aion__Rates__KinahChance`, `Aion__Rates__GatherExperience`, `Aion__Rates__CraftExperience`, `Aion__Rates__PetExperience`;
the time gates `Aion__TimeGates__ShopLimits`, `__DungeonTimeLimits`, `__CollectionRotation` (true/false, default false) and
`__GatherRespawnSeconds` (0-3600, default 10); `Aion__Unlocks__CraftAlwaysSucceeds` (default true) and `__PetSoulCost` (0-10,
default 0.5). Rates are 0-100; an invalid value stops the host at startup with the reason. Scheduled events from the dashboard
multiply on top (each event rate is held to 0.1-10, the product is not, and loot is capped at 100 table rolls per kill).
Kinah rate applies to Kinah in monster drops, quest rewards, Kinah chests, reward boxes, dungeon, boss and cube rewards, achievements and
collections; not to selling to merchants, trades, mail or staff grants. `tools/server_env.py` takes the same settings
as options (`--rates-profile`, `--exp-rate`, `--quest-exp-rate`, `--kinah-rate`, `--kinah-chance`, `--drop-rate`,
`--gather-exp-rate`, `--craft-exp-rate`, `--pet-exp-rate`, `--shop-limits`, `--dungeon-timers`, `--collection-rotation`,
`--gather-respawn-seconds`, `--all-skins`, `--all-pets`, `--craft-always-succeeds`, `--pet-soul-cost`, `--max-players`,
`--respawn-seconds`, `--pvp on|off`, `--party-loot`, `--fallback-loot on|off`) for a hand-made environment file. Comments go on their own lines (systemd reads
`#` after a value as part of it). The party size is not configurable: it stays 4. The server sends it to the client
(PartyInfo LimitMember, a CANDIDATE field), and nothing recorded shows how the client's party window copes with another size.

Party loot and monster Kinah are our own rules (nothing observed). `Aion__Rules__PartyLoot` shares a party member's kill
among the members on the same map within 100 m (the members who share its experience): `roundrobin` gives the items to
them in turn, `freeforall` gives each item to one of them at random, `killer` keeps everything with the killer. The
killer's part lies on the ground to pick up; the others' parts go straight to their bags (a part their bag cannot
take, because they died meanwhile or the bag is full, lies on the ground at the kill site for them instead). Every monster also pays Kinah
(tools/data/kinah-drops.json, by level and rank, times the Kinah rate `Aion__Rates__Kinah`), split evenly among the
same members (all to the killer with `killer`). `Aion__Rules__FallbackLoot=false` turns off the generic loot that monsters
without a reward table in the client's data (about three in four) roll instead (tools/data/fallback-loot.json:
equipment of the monster's level from tools/data/items.json, rebuilt by `tools/fallback_loot.py`).

## Playerbots

Off by default. With them on, a player types `//bot add <character>` in chat and one of their own other characters joins
them as a bot: it follows, joins the party and fights what they fight ([docs/PLAYERBOTS.md](../../docs/PLAYERBOTS.md)).

```bash
sudo tee -a /etc/aion/game.env >/dev/null <<'ENV'
Aion__Bots__Enabled=true
# every player may summon bots (false: staff characters only)
Aion__Bots__PlayersMayUse=true
Aion__Bots__MaxPerAccount=3
Aion__Bots__MaxTotal=20
ENV
sudo systemctl restart aion-host
journalctl -u aion-host | grep "Playerbots on"
```

## Market (`//ah`)

On by default: players list and buy items with `//ah` in chat, a seller bot keeps the market stocked and a buyer bot buys
fairly priced player listings ([docs/MARKET.md](../../docs/MARKET.md) has every key).

```bash
sudo tee -a /etc/aion/game.env >/dev/null <<'ENV'
Aion__Market__SaleTaxPercent=5
# Light and Dark share one market
Aion__Market__CrossWorld=true
Aion__Market__Seller__Equipment=true
Aion__Market__Buyer__BuyChancePercent=35
ENV
sudo systemctl restart aion-host
journalctl -u aion-host | grep "Market:"
```

Dungeon entry limits are our own rule too and are off unless `Aion__Dungeons__EntryLimits=true`. Then each character may
enter each expedition, awakening dungeon, daily dungeon and raid `Aion__Dungeons__DailyEntries` times a day (default 3;
0 no limit); the day starts at `Aion__Dungeons__ResetHour` (server local time, default 9). `Aion__Dungeons__Entries__<id or
name>=<n>` sets one dungeon's count (Dungeon.dat id or name, e.g. `Aion__Dungeons__Entries__KraoCave_Hard=1`; 0 lifts its
limit; it also limits a dungeon of another type). A refused entry simply does not move the player (as for the level and
item level rules). Going back into the party copy already counted is free. Staff are not limited; the builder command
`ResetDungeonEntries [character]` clears today's counts of yourself or a character in the world. The client's data has no
entry tickets or energy cost to apply, so there is none. `Aion__Dungeons__UntrackedObjectiveSeconds` (default 30) is how
long a dungeon objective the server cannot follow yet (waves, puzzles, scores, collected items) waits before it counts as
done; each one is logged. Some expedition and raid final bosses hit everyone within 5 m with their special attacks
(tools/data/boss-area-skills.json, our own table until the skills' target shapes are extracted). `tools/server_env.py`
options: `--dungeon-limits on|off`, `--dungeon-entries`, `--dungeon-entry DUNGEON=N` (repeatable), `--dungeon-reset-hour`,
`--untracked-objective-seconds`.

## Updating

Changes are made and tested on the development PC, committed and pushed to the private GitHub
repository. On the server:

```bash
cd ~/aion
sudo deploy/linux/update.sh
```

From the development PC, `tools/deploy.ps1` does this over SSH (ssh config host `aion`: the server's
address, `User root`, a dedicated key `~/.ssh/id_ed25519_aion` whose public half is in the server's
`authorized_keys`): `-Server` pushes the current branch and runs update.sh; `-Launcher` publishes
A2Core Launcher and copies the exe and its version file to `/var/lib/aion/downloads`; `-Status` shows the
services and the last log lines.

It backs up the database (`/var/lib/aion/backups/before-update-*.dump`, newest 10 kept), pulls, then repeats install.sh
with the options of the last install: rebuilds the host, applies new migrations, restarts.
New server data (re-extracted terrain etc.): copy a new archive and run install.sh with `--data`.

Restore a backup if an update goes wrong: `sudo /opt/aion/bin/restore.sh --yes /var/lib/aion/backups/<file>.dump`
(see "Backups").

## Backups

`aion-backup.timer` runs `/opt/aion/bin/backup.sh` (from `deploy/linux/backup.sh`) every night at 04:30 (plus up to
15 minutes; `Persistent`, so a night missed while the machine was off runs at the next boot). It writes
`/var/lib/aion/backups/nightly-<UTC time>.dump` with `postgres_dev.py backup` (pg_dump, custom format), checks it with
`postgres_dev.py verify-backup` (a restore into a disposable database that is dropped again) and then keeps the newest
14 `nightly-*` files. A backup that fails the check is deleted and nothing is pruned. update.sh's `before-update-*`
and the dashboard's `aion-*` backups in the same folder have their own retention (the dashboard's: none).

```bash
systemctl list-timers aion-backup.timer
sudo systemctl start aion-backup && journalctl -u aion-backup -n 20     # back up now
echo 'AION_BACKUP_KEEP=30' | sudo tee /etc/aion/backup.env             # settings: AION_BACKUP_KEEP, AION_BACKUP_VERIFY=0
echo 'AION_PG_TIMEOUT=7200' | sudo tee -a /etc/aion/backup.env        # pg_dump/verify limit in s (default 3600, 0 = none)
sudo systemctl disable --now aion-backup.timer                          # no nightly backups
sudo /opt/aion/bin/backup.sh --keep 30                                  # by hand (as root or aion)
```

**Other folders and an off-machine copy.** The unit is sandboxed (`ProtectSystem=strict`): it may write only to
`/var/lib/aion/backups` and the folders `backup-paths.sh` lists for it. To back up elsewhere (`AION_BACKUP_DIR`), or to
copy every verified backup to a second place such as another disk or a network mount (`AION_BACKUP_COPY_DIR`, with its
own retention `AION_BACKUP_COPY_KEEP`, default the same as `AION_BACKUP_KEEP`), set them in `/etc/aion/backup.env` and
run `backup-paths.sh` once; it creates the folders and writes the drop-in
`/etc/systemd/system/aion-backup.service.d/10-backup-paths.conf` (install.sh, and so update.sh, runs it too):

```bash
echo 'AION_BACKUP_COPY_DIR=/mnt/backup-disk/a2core' | sudo tee -a /etc/aion/backup.env
echo 'AION_BACKUP_COPY_KEEP=30' | sudo tee -a /etc/aion/backup.env
sudo /opt/aion/bin/backup-paths.sh
sudo systemctl start aion-backup && journalctl -u aion-backup -n 20     # "Copied to ..." at the end
```

The copy is written as `.partial`, compared byte for byte with the local backup, then renamed; only
`<prefix>-<time>.dump` files are pruned there. A failed copy (say the disk is not mounted) keeps the local backup,
prunes nothing in the copy folder and fails the unit (`systemctl --failed`, `journalctl -u aion-backup`). Folders under
`/home`, `/root` or `/run/user` are refused (the unit cannot see them: `ProtectHome`).

Restore:

```bash
sudo /opt/aion/bin/restore.sh --check /var/lib/aion/backups/<file>.dump   # restore test; the live database is untouched
sudo /opt/aion/bin/restore.sh --yes /var/lib/aion/backups/<file>.dump     # replace the live database
sudo /opt/aion/bin/restore.sh --list-old                                  # databases kept by earlier restores
sudo /opt/aion/bin/restore.sh --yes --drop-old aion_pre_restore_<time>
```

Every run first restores the file into a disposable database; without `--yes` (and without `--check`) it then
refuses. `--yes` writes a safety backup of the live database (`before-restore-<time>.dump`), stops aion-host,
restores into a new database and only then renames the live one to `aion_pre_restore_<time>` (kept) and the new one to
`aion`, and starts aion-host again.

## Getting the code onto the server

The repository is private: give the server a read-only deploy key.

```bash
ssh-keygen -t ed25519 -N '' -f ~/.ssh/aion_deploy
cat ~/.ssh/aion_deploy.pub   # GitHub: repository Settings > Deploy keys > Add (read-only)
printf 'Host github.com
  IdentityFile ~/.ssh/aion_deploy
' >> ~/.ssh/config
git clone git@github.com:<owner>/<repo>.git ~/aion
```

## Operating

```bash
systemctl status aion-host aion-postgres
journalctl -u aion-host -f
# Staff roles (after the account has registered on the account site):
sudo runuser -u aion -- env AION_PG_HOME=/var/lib/aion/postgres python3 /opt/aion/src/tools/postgres_dev.py role USERNAME admin
```

The account site is `https://<domain or address>:8780`; players register there, and the
launcher signs in there.

## How clients reach this server

Each launch gets a one-use launch authorization (`POST /api/client-launches`). The launcher
writes it into the bootstrap URL, `ConfigurationServerUrl="http://<address>:8087/c/<authorization>"`;
the host then serves that client a policy whose api_gate keeps the code, and the client's
authn_token request redeems it for its own game ticket. A client started without a code redeems
the newest launch requested from its own IP address (players sharing one address should use codes).

CANDIDATE: that the client keeps the path of ConfigurationServerUrl (the api_gate path is
observed to be kept). The address pairing above covers the case where it does not.

Players also need hosts-file lines pointing the six NC hostnames at this server's address
(`research/client-installer.md`); the installer/launcher sets those up.

## Not covered yet

- The player installer/launcher (research/client-installer.md).
- Only tested on Windows so far: the Linux scripts and units have not run on a real Ubuntu
  host yet. First install: watch `journalctl -u aion-host` and report any failure.
