#!/usr/bin/env bash
# Installs or updates the server inside WSL2 Ubuntu 24.04 on a Windows PC, for players on a Tailscale tailnet.
# Run as root from a checkout of this repository in the WSL file system (not under /mnt/c):
#   sudo deploy/windows/wsl-install.sh --data data/aion-server-data.tar.gz \
#       --certificate /mnt/c/Users/<you>/AppData/Local/A2Core/tls/cert.pem --key /mnt/c/Users/<you>/AppData/Local/A2Core/tls/key.pem \
#       --max-players 10                      (rates: the friends profile unless --rates-profile says otherwise)
# Other runs: no --data (the data stays unpacked). Only the rates/rules (no rebuild):
#   sudo deploy/windows/wsl-install.sh --rates-only --exp-rate 15 --all-skins on --all-pets on
# It installs the packages (dotnet-sdk-10.0, postgresql, python3, openssl from the Ubuntu archive; Node.js 22 from
# NodeSource when the system's is older than the web build needs), writes the rates and rules you give into
# /etc/aion/game.env (other lines there are kept), then runs deploy/linux/install.sh with --public-address set to the
# Windows PC's Tailscale IPv4 address (100.64.0.0/10) and the certificate files (made on Windows by
# deploy/windows/renew-cert.ps1, `tailscale cert`), or --self-signed. Mirrored networking: the address is found on the
# mirrored interfaces. NAT networking: Tailscale's interface is not visible here, so give --public-address (on Windows:
# tailscale ip -4); deploy/windows/wsl-portproxy.ps1 forwards that address's ports to WSL. See deploy/windows/README.md.
set -euo pipefail

CONFIG="${AION_CONFIG_DIR:-/etc/aion}"
TS_IP="" DATA="" CERT="" KEY="" SELF_SIGNED=0 RATES_ONLY=0 SKIP_PACKAGES=0
declare -A GAME=()
usage() { sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; cat <<'EOF'
Options:
  --public-address 100.x.y.z   the Windows PC's Tailscale IPv4 address, which players connect to (Windows: tailscale ip -4);
                               required with NAT networking; mirrored: default the 100.64.0.0/10 address seen here
  --tailscale-ip 100.x.y.z     the same as --public-address (older name)
  --data FILE                  server data bundle (first install)
  --certificate FILE --key FILE   account-site certificate (tailscale cert); or --self-signed
  --rates-profile friends|official|accelerated (default friends), --exp-rate N, --quest-exp-rate N, --kinah-rate N,
  --kinah-chance N, --drop-rate N, --gather-exp-rate N, --craft-exp-rate N, --pet-exp-rate N (0-100)
  --max-players N (0: no cap), --respawn-seconds N (5-3600), --pvp on|off
  Time gates (default all off): --shop-limits on|off, --dungeon-timers on|off, --collection-rotation on|off,
  --dungeon-limits on|off, --gather-respawn-seconds N (0-3600, default 10), --revive-seconds N (1-600, default 5)
  Unlocks: --all-skins on|off, --all-pets on|off, --all-wings on|off (default on),
  --craft-always-succeeds on|off (default on), --pet-soul-cost N (0-10, default 0.5)
  Alt boost: --boost on|off (default on), --boost-level N (default 45), --boost-price N (Kinah, default 10000000)
  --rates-only                 write game.env and restart aion-host; no install
  --skip-packages              do not run apt-get
EOF
}

rate() {
  if ! [[ "$2" =~ ^[0-9]+([.][0-9]+)?$ ]] || ! awk -v v="$2" 'BEGIN { exit !(v >= 0 && v <= 100) }'; then
    echo "$1 must be a number from 0 to 100" >&2; exit 2
  fi
}
switch() {
  [[ "$2" == on || "$2" == off ]] || { echo "$1 is on or off" >&2; exit 2; }
  if [[ "$2" == on ]]; then GAME[$3]=true; else GAME[$3]=false; fi
}
while [[ $# -gt 0 ]]; do
  case "$1" in
    --public-address|--tailscale-ip) TS_IP="$2"; shift 2 ;;
    --data) DATA="$2"; shift 2 ;;
    --certificate) CERT="$2"; shift 2 ;;
    --key) KEY="$2"; shift 2 ;;
    --self-signed) SELF_SIGNED=1; shift ;;
    --rates-only) RATES_ONLY=1; shift ;;
    --skip-packages) SKIP_PACKAGES=1; shift ;;
    --rates-profile)
      [[ "$2" == friends || "$2" == official || "$2" == accelerated ]] || { echo "--rates-profile is friends, official or accelerated" >&2; exit 2; }
      GAME[Aion__Rates__Profile]="$2"; shift 2 ;;
    --kinah-chance) rate "$1" "$2"; GAME[Aion__Rates__KinahChance]="$2"; shift 2 ;;
    --gather-exp-rate) rate "$1" "$2"; GAME[Aion__Rates__GatherExperience]="$2"; shift 2 ;;
    --craft-exp-rate) rate "$1" "$2"; GAME[Aion__Rates__CraftExperience]="$2"; shift 2 ;;
    --pet-exp-rate) rate "$1" "$2"; GAME[Aion__Rates__PetExperience]="$2"; shift 2 ;;
    --shop-limits) switch "$1" "$2" Aion__TimeGates__ShopLimits; shift 2 ;;
    --dungeon-timers) switch "$1" "$2" Aion__TimeGates__DungeonTimeLimits; shift 2 ;;
    --collection-rotation) switch "$1" "$2" Aion__TimeGates__CollectionRotation; shift 2 ;;
    --dungeon-limits) switch "$1" "$2" Aion__Dungeons__EntryLimits; shift 2 ;;
    --gather-respawn-seconds)
      if ! [[ "$2" =~ ^[0-9]{1,4}$ ]] || ((10#$2 > 3600)); then echo "--gather-respawn-seconds must be 0-3600" >&2; exit 2; fi
      GAME[Aion__TimeGates__GatherRespawnSeconds]="$2"; shift 2 ;;
    --all-skins) switch "$1" "$2" Aion__Unlocks__AllSkins; shift 2 ;;
    --all-pets) switch "$1" "$2" Aion__Unlocks__AllPets; shift 2 ;;
    --all-wings) switch "$1" "$2" Aion__Unlocks__AllWings; shift 2 ;;
    --boost) switch "$1" "$2" Aion__Boost__Enabled; shift 2 ;;
    --revive-seconds)
      if ! [[ "$2" =~ ^[0-9]{1,3}$ ]] || ((10#$2 < 1 || 10#$2 > 600)); then echo "--revive-seconds must be 1-600" >&2; exit 2; fi
      GAME[Aion__TimeGates__ReviveSeconds]="$2"; shift 2 ;;
    --boost-level)
      if ! [[ "$2" =~ ^[0-9]{1,3}$ ]] || ((10#$2 < 2 || 10#$2 > 100)); then echo "--boost-level must be 2-100" >&2; exit 2; fi
      GAME[Aion__Boost__Level]="$2"; GAME[Aion__Boost__RequiredLevel]="$2"; shift 2 ;;
    --boost-price)
      if ! [[ "$2" =~ ^[0-9]{1,11}$ ]] || ((10#$2 > 10000000000)); then echo "--boost-price must be 0-10000000000 Kinah" >&2; exit 2; fi
      GAME[Aion__Boost__Price]="$2"; shift 2 ;;
    --craft-always-succeeds) switch "$1" "$2" Aion__Unlocks__CraftAlwaysSucceeds; shift 2 ;;
    --pet-soul-cost)
      if ! [[ "$2" =~ ^[0-9]+([.][0-9]+)?$ ]] || ! awk -v v="$2" 'BEGIN { exit !(v >= 0 && v <= 10) }'; then echo "--pet-soul-cost must be 0-10" >&2; exit 2; fi
      GAME[Aion__Unlocks__PetSoulCost]="$2"; shift 2 ;;
    --exp-rate) rate "$1" "$2"; GAME[Aion__Rates__Experience]="$2"; shift 2 ;;
    --quest-exp-rate) rate "$1" "$2"; GAME[Aion__Rates__QuestExperience]="$2"; shift 2 ;;
    --kinah-rate) rate "$1" "$2"; GAME[Aion__Rates__Kinah]="$2"; shift 2 ;;
    --drop-rate) rate "$1" "$2"; GAME[Aion__Rates__Drop]="$2"; shift 2 ;;
    --max-players)
      [[ "$2" =~ ^[0-9]{1,5}$ ]] || { echo "--max-players must be 0 or more" >&2; exit 2; }
      GAME[Aion__Native__MaxPlayers]="$2"; shift 2 ;;
    --respawn-seconds)
      if ! [[ "$2" =~ ^[0-9]{1,4}$ ]] || ((10#$2 < 5 || 10#$2 > 3600)); then echo "--respawn-seconds must be 5-3600" >&2; exit 2; fi
      GAME[Aion__Rules__RespawnSeconds]="$2"; shift 2 ;;
    --pvp)
      [[ "$2" == on || "$2" == off ]] || { echo "--pvp is on or off" >&2; exit 2; }
      if [[ "$2" == on ]]; then GAME[Aion__Rules__Pvp]=true; else GAME[Aion__Rules__Pvp]=false; fi; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
done
if [[ $RATES_ONLY -eq 0 ]]; then
  [[ -n "$CERT" || $SELF_SIGNED -eq 1 ]] || { echo "Give --certificate/--key (tailscale cert) or --self-signed" >&2; exit 2; }
  [[ -z "$CERT" || -n "$KEY" ]] || { echo "--certificate needs --key" >&2; exit 2; }
  [[ -z "$CERT" || $SELF_SIGNED -eq 0 ]] || { echo "--certificate and --self-signed exclude each other" >&2; exit 2; }
fi
[[ $EUID -eq 0 || -n "${AION_CONFIG_DIR:-}" ]] || { echo "Run as root (sudo)." >&2; exit 2; }

# KEY=value lines into game.env: a given key replaces its line, other lines (and comments) stay.
write_game_env() {
  [[ ${#GAME[@]} -gt 0 ]] || return 0
  local file="$CONFIG/game.env" key tmp
  mkdir -p "$CONFIG"
  tmp="$(mktemp "$CONFIG/.game.env.XXXXXX")"
  [[ -f "$file" ]] && cp -- "$file" "$tmp"
  for key in "${!GAME[@]}"; do
    if grep -q "^$key=" "$tmp"; then
      sed -i "s|^$key=.*|$key=${GAME[$key]}|" "$tmp"
    else
      printf '%s=%s\n' "$key" "${GAME[$key]}" >> "$tmp"
    fi
  done
  if [[ $EUID -eq 0 ]] && getent group aion &>/dev/null; then chown root:aion "$tmp"; fi
  chmod 640 "$tmp"
  mv -- "$tmp" "$file"
  echo "Rates and rules in $file:"
  grep -E '^Aion__(Rates|Rules|TimeGates|Unlocks|Boost|Dungeons|Native__MaxPlayers)' "$file" | sed 's/^/  /'
}

if [[ $RATES_ONLY -eq 1 ]]; then
  [[ ${#GAME[@]} -gt 0 ]] || { echo "--rates-only needs at least one rate or rule option" >&2; exit 2; }
  write_game_env
  if [[ -d /run/systemd/system ]] && systemctl is-active --quiet aion-host; then
    systemctl restart aion-host
    echo "aion-host restarted. Effective rates: journalctl -u aion-host | grep 'World rates'"
  fi
  exit 0
fi

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
grep -qi microsoft /proc/version 2>/dev/null || echo "Note: this does not look like WSL; deploy/linux/install.sh is the plain Linux installer." >&2
case "$REPO" in /mnt/*) echo "Clone the repository into the WSL file system (e.g. ~/a2core), not $REPO: builds on /mnt are slow and file modes break." >&2; exit 1 ;; esac
[[ "$(ps -p 1 -o comm= 2>/dev/null)" == systemd ]] || { echo "systemd is not running: set [boot] systemd=true in /etc/wsl.conf (setup-wsl.ps1 does), then 'wsl --shutdown' on Windows." >&2; exit 1; }
MODE=""
if command -v wslinfo &>/dev/null; then MODE="$(wslinfo --networking-mode 2>/dev/null || true)"; fi
case "$MODE" in
  ""|mirrored) ;;
  nat) echo "WSL networking: NAT. Players reach the server through deploy/windows/wsl-portproxy.ps1 on Windows (run it as administrator after this)." ;;
  *) echo "Warning: WSL networking mode is '$MODE'; this setup knows mirrored and nat (deploy/windows/README.md)." >&2 ;;
esac

# The Tailscale address: Tailscale runs on Windows; mirrored networking shows its interface here, NAT does not.
in_tailnet() { [[ "$1" =~ ^100\.([0-9]+)\.[0-9]+\.[0-9]+$ ]] && ((BASH_REMATCH[1] >= 64 && BASH_REMATCH[1] <= 127)); }
if [[ -z "$TS_IP" ]]; then
  [[ "$MODE" != nat ]] || { echo "NAT networking: pass --public-address <the Windows PC's Tailscale IPv4> (Windows: tailscale ip -4)." >&2; exit 2; }
  mapfile -t FOUND < <(ip -4 -o addr show 2>/dev/null | awk '{ split($4, a, "/"); print a[1] }' | while read -r a; do in_tailnet "$a" && echo "$a"; done)
  [[ ${#FOUND[@]} -eq 1 ]] || { echo "Found ${#FOUND[@]} Tailscale (100.64.0.0/10) addresses (${FOUND[*]:-none}): pass --public-address (Windows: tailscale ip -4)." >&2; exit 1; }
  TS_IP="${FOUND[0]}"
fi
if ! in_tailnet "$TS_IP"; then
  # OURS: a local-only test (no friends yet) may use the WSL address, reachable only from this PC.
  [[ "$TS_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ && ! "$TS_IP" =~ ^127\. ]] || { echo "$TS_IP is not a usable IPv4 address" >&2; exit 2; }
  echo "Warning: $TS_IP is not a Tailscale address (100.64.0.0/10); only clients on this PC can reach it (local test)." >&2
fi
echo "Players connect to $TS_IP"

node_ok() {
  command -v node &>/dev/null && command -v npm &>/dev/null && node -e '
    const [major, minor] = process.versions.node.split(".").map(Number);
    process.exit((major === 20 && minor >= 19) || (major === 22 && minor >= 12) || major > 22 ? 0 : 1)'
}
if [[ $SKIP_PACKAGES -eq 0 ]]; then
  apt-get update
  apt-get install -y dotnet-sdk-10.0 postgresql python3 openssl ca-certificates curl gnupg
  if ! node_ok; then
    # Ubuntu 24.04's nodejs (18) is too old for Vite 8. NodeSource's signed repository for Node.js 22 (its nodejs
    # package includes npm). install.sh then keeps it (it installs Ubuntu's nodejs/npm only without a new enough node).
    install -d -m 755 /etc/apt/keyrings
    curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key | gpg --dearmor --yes -o /etc/apt/keyrings/nodesource.gpg
    echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_22.x nodistro main" > /etc/apt/sources.list.d/nodesource.list
    if dpkg -s npm &>/dev/null; then apt-get remove -y npm; fi
    apt-get update
    apt-get install -y nodejs
  fi
fi
node_ok || { echo "Node.js ^20.19 or >=22.12 with npm is needed for the web build" >&2; exit 1; }

write_game_env

ARGS=(--public-address "$TS_IP")
[[ -z "$DATA" ]] || ARGS+=(--data "$DATA")
if [[ $SELF_SIGNED -eq 1 ]]; then ARGS+=(--self-signed); else ARGS+=(--certificate "$CERT" --key "$KEY"); fi
# Packages are handled above; install.sh saves its options for update.sh (which installs packages again, keeping node).
bash "$REPO/deploy/linux/install.sh" "${ARGS[@]}" --skip-packages
# The aion group exists now (first install): game.env like aion.env, root:aion 640.
[[ ! -f "$CONFIG/game.env" ]] || { chown root:aion "$CONFIG/game.env"; chmod 640 "$CONFIG/game.env"; }
echo "Account site: https://<this PC's Tailscale name>:8780 (the name on the certificate); friends' launcher.json: serverAddress $TS_IP"
[[ "$MODE" != nat ]] || echo "NAT networking: on Windows, as administrator: deploy\\windows\\wsl-portproxy.ps1 -Register"
