#!/usr/bin/env bash
# Database backup with verification and retention (the nightly aion-backup.timer runs it; see deploy/linux/README.md).
#   sudo /opt/aion/bin/backup.sh                 # nightly-<UTC timestamp>.dump in /var/lib/aion/backups, newest 14 kept
#   sudo /opt/aion/bin/backup.sh --keep 30 --dir /mnt/d/aion-backups
#   sudo /opt/aion/bin/backup.sh --copy-dir /mnt/c/Users/<you>/A2Core-Backups --copy-keep 7   # plus an off-disk copy
# Steps: postgres_dev.py backup (pg_dump -Fc) to <name>.dump.partial, postgres_dev.py verify-backup (restores it into a
# disposable database, prints the row counts, drops it), then the rename to <name>.dump and the retention pass. A backup
# that fails verification is removed and nothing is pruned, so a broken night never costs a good backup.
# Retention only touches <prefix>-YYYYMMDD-HHMMSS.dump files: update.sh's before-update-*.dump and the dashboard's
# aion-*.dump are left alone. With a copy folder each verified backup is also copied there (compared byte for byte), with
# its own retention; a failed copy keeps the local backup and exits 1.
# Settings (flags win): AION_BACKUP_DIR, AION_BACKUP_KEEP, AION_BACKUP_PREFIX, AION_BACKUP_VERIFY=0,
# AION_BACKUP_COPY_DIR, AION_BACKUP_COPY_KEEP (default: the same as AION_BACKUP_KEEP), AION_PG_TIMEOUT (seconds for
# pg_dump and the verification restore, default 3600, 0 = no limit),
# AION_PG_HOME (/var/lib/aion/postgres), AION_PG_TOOL (/opt/aion/src/tools/postgres_dev.py), AION_RUN_AS (aion; empty runs
# the tool as the calling user). The unit reads them from /etc/aion/backup.env; after changing a folder there, run
# sudo /opt/aion/bin/backup-paths.sh (the sandboxed unit may write only to the folders it lists).
set -euo pipefail

DIR="${AION_BACKUP_DIR:-/var/lib/aion/backups}"
KEEP="${AION_BACKUP_KEEP:-14}"
PREFIX="${AION_BACKUP_PREFIX:-nightly}"
VERIFY="${AION_BACKUP_VERIFY:-1}"
TOOL="${AION_PG_TOOL:-/opt/aion/src/tools/postgres_dev.py}"
PYTHON="${AION_PYTHON:-python3}"
RUN_AS="${AION_RUN_AS-aion}"
export AION_PG_HOME="${AION_PG_HOME:-/var/lib/aion/postgres}"

COPY_DIR="${AION_BACKUP_COPY_DIR:-}"
COPY_KEEP="${AION_BACKUP_COPY_KEEP:-}"

usage() { sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'; }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --dir) DIR="$2"; shift 2 ;;
    --keep) KEEP="$2"; shift 2 ;;
    --copy-dir) COPY_DIR="$2"; shift 2 ;;
    --copy-keep) COPY_KEEP="$2"; shift 2 ;;
    --prefix) PREFIX="$2"; shift 2 ;;
    --no-verify) VERIFY=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option $1" >&2; exit 2 ;;
  esac
done
[[ "$KEEP" =~ ^[1-9][0-9]{0,3}$ ]] || { echo "--keep must be a whole number from 1 to 9999" >&2; exit 2; }
COPY_KEEP="${COPY_KEEP:-$KEEP}"
[[ "$COPY_KEEP" =~ ^[1-9][0-9]{0,3}$ ]] || { echo "--copy-keep must be a whole number from 1 to 9999" >&2; exit 2; }
[[ -z "$COPY_DIR" || "$COPY_DIR" == /* ]] || { echo "--copy-dir must be an absolute path" >&2; exit 2; }
[[ "$PREFIX" =~ ^[a-z][a-z0-9-]{0,30}$ ]] || { echo "--prefix must be lowercase letters, digits or -" >&2; exit 2; }
[[ -f "$TOOL" ]] || { echo "postgres_dev.py not found at $TOOL (set AION_PG_TOOL)" >&2; exit 1; }

# The cluster's credentials belong to the service account: root switches to it, the account itself runs directly.
tool() {
  if [[ -z "$RUN_AS" || "$(id -un)" == "$RUN_AS" ]]; then
    (cd / && "$PYTHON" "$TOOL" "$@")
  elif [[ $EUID -eq 0 ]]; then
    (cd / && runuser -u "$RUN_AS" -- env AION_PG_HOME="$AION_PG_HOME" ${AION_PG_BIN:+AION_PG_BIN="$AION_PG_BIN"} ${AION_PG_TIMEOUT:+AION_PG_TIMEOUT="$AION_PG_TIMEOUT"} "$PYTHON" "$TOOL" "$@")
  else
    echo "Run as root or as $RUN_AS." >&2; exit 2
  fi
}

if [[ $EUID -eq 0 && -n "$RUN_AS" ]] && id "$RUN_AS" &>/dev/null; then
  install -d -o "$RUN_AS" -g "$RUN_AS" -m 750 "$DIR"
else
  mkdir -p "$DIR"
fi

# One backup at a time (the timer and a manual run); a lock on this script needs no writable lock file.
exec 9<"$0"
flock -n 9 || { echo "Another backup is running." >&2; exit 1; }

NAME="$PREFIX-$(date -u +%Y%m%d-%H%M%S).dump"
FINAL="$DIR/$NAME"
PARTIAL="$FINAL.partial"
[[ ! -e "$FINAL" ]] || { echo "$FINAL already exists" >&2; exit 1; }
trap 'rm -f -- "$PARTIAL"' EXIT

echo "Backing up to $FINAL"
tool backup "$PARTIAL"
[[ -s "$PARTIAL" ]] || { echo "Backup is empty" >&2; exit 1; }
if [[ "$VERIFY" != 0 ]]; then
  echo "Verifying (restore into a disposable database):"
  tool verify-backup "$PARTIAL" || { echo "Verification failed: backup discarded, nothing pruned." >&2; exit 1; }
fi
mv -- "$PARTIAL" "$FINAL"
trap - EXIT
echo "Saved $FINAL ($(du -h "$FINAL" | cut -f1))"

# Retention: newest N of this prefix by the timestamp in the name.
prune() {
  local dir="$1" keep="$2" old
  local -a olds
  mapfile -t olds < <(find "$dir" -maxdepth 1 -type f -name "$PREFIX-*.dump" -printf '%f\n' \
    | grep -E "^$PREFIX-[0-9]{8}-[0-9]{6}\.dump$" | sort -r | tail -n +$((keep + 1)))
  for old in "${olds[@]}"; do
    rm -f -- "${dir:?}/$old"
    echo "Removed old backup $dir/$old"
  done
}
prune "$DIR" "$KEEP"

# Off-disk copy (e.g. the Windows drive under WSL): written as .partial, compared, then renamed; its own retention.
if [[ -n "$COPY_DIR" ]]; then
  COPY="$COPY_DIR/$NAME"
  if mkdir -p -- "$COPY_DIR" && cp -- "$FINAL" "$COPY.partial" && cmp -s -- "$FINAL" "$COPY.partial" && mv -- "$COPY.partial" "$COPY"; then
    echo "Copied to $COPY"
    prune "$COPY_DIR" "$COPY_KEEP"
  else
    rm -f -- "$COPY.partial"
    echo "Copy to $COPY_DIR failed; the backup $FINAL is kept, nothing pruned there." >&2
    exit 1
  fi
fi
