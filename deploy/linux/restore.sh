#!/usr/bin/env bash
# Restores a database backup (a .dump from backup.sh, update.sh or the dashboard). Installed to /opt/aion/bin.
#   sudo /opt/aion/bin/restore.sh --check /var/lib/aion/backups/nightly-20261108-043000.dump   # restore test only
#   sudo /opt/aion/bin/restore.sh --yes   /var/lib/aion/backups/nightly-20261108-043000.dump   # replace the live database
#   sudo /opt/aion/bin/restore.sh --list-old            # databases kept by earlier restores
#   sudo /opt/aion/bin/restore.sh --yes --drop-old aion_pre_restore_20261108_120000
# Every run first restores the file into a disposable database and checks it (postgres_dev.py verify-backup); without
# --yes nothing else happens. With --yes: a safety backup of the live database (before-restore-*.dump), aion-host stops,
# the file is restored into a new database aion_incoming_<time>, and only when that succeeds the live database is renamed
# aion_pre_restore_<time> (kept, not dropped) and the new one becomes aion. aion-host starts again if it was running.
# Settings: AION_PG_HOME, AION_PG_TOOL, AION_RUN_AS, AION_BACKUP_DIR as for backup.sh.
set -euo pipefail

TOOL="${AION_PG_TOOL:-/opt/aion/src/tools/postgres_dev.py}"
PYTHON="${AION_PYTHON:-python3}"
RUN_AS="${AION_RUN_AS-aion}"
BACKUP_DIR="${AION_BACKUP_DIR:-/var/lib/aion/backups}"
export AION_PG_HOME="${AION_PG_HOME:-/var/lib/aion/postgres}"

YES=0 CHECK=0 MODE=restore FILE="" OLD=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) YES=1; shift ;;
    --check) CHECK=1; shift ;;
    --list-old) MODE=list; shift ;;
    --drop-old) MODE=drop; OLD="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "Unknown option $1" >&2; exit 2 ;;
    *) [[ -z "$FILE" ]] || { echo "One backup file only" >&2; exit 2; }; FILE="$1"; shift ;;
  esac
done
[[ $YES -eq 0 || $CHECK -eq 0 ]] || { echo "--check and --yes exclude each other" >&2; exit 2; }
[[ -f "$TOOL" ]] || { echo "postgres_dev.py not found at $TOOL (set AION_PG_TOOL)" >&2; exit 1; }

as_service() {
  if [[ -z "$RUN_AS" || "$(id -un)" == "$RUN_AS" ]]; then
    (cd / && "$@")
  elif [[ $EUID -eq 0 ]]; then
    (cd / && runuser -u "$RUN_AS" -- env AION_PG_HOME="$AION_PG_HOME" ${AION_PG_BIN:+AION_PG_BIN="$AION_PG_BIN"} "$@")
  else
    echo "Run as root (sudo)." >&2; exit 2
  fi
}

# SQL and pg_restore through postgres_dev.py's own credentials and binaries (no timeout on the restore itself).
db() {
  as_service "$PYTHON" - "$TOOL" "$@" <<'PY'
import json, os, subprocess, sys
from pathlib import Path
tool, command, *rest = sys.argv[1:]
sys.path.insert(0, str(Path(tool).resolve().parent))
import postgres_dev as pg

def env():
    credentials = json.loads((pg.HOME / 'credentials.json').read_text())
    return dict(os.environ, PGHOST='127.0.0.1', PGPORT=str(pg.PORT), PGUSER='aion_admin',
                PGPASSWORD=credentials['aion_admin'], PGCONNECT_TIMEOUT='5')

def binary(name):
    return str(pg.BIN / (name + ('.exe' if pg.WINDOWS else '')))

if command == 'sql':
    print(pg.sql(rest[0], database='postgres').stdout, end='')
elif command == 'restore':
    name, path = rest
    subprocess.run([binary('pg_restore'), '--exit-on-error', '--dbname', name, path], env=env(), check=True)
    print(pg.sql("SELECT 'migrations='||count(*) FROM public.schema_migrations;", database=name).stdout.strip())
PY
}

case $MODE in
  list)
    db sql "SELECT datname FROM pg_database WHERE datname ~ '^aion_(pre_restore|incoming)_[0-9]{8}_[0-9]{6}$' ORDER BY 1;"
    exit 0 ;;
  drop)
    [[ "$OLD" =~ ^aion_pre_restore_[0-9]{8}_[0-9]{6}$ ]] || { echo "--drop-old takes an aion_pre_restore_<date>_<time> name (see --list-old)" >&2; exit 2; }
    [[ $YES -eq 1 ]] || { echo "Dropping $OLD is permanent: add --yes." >&2; exit 2; }
    db sql "DROP DATABASE $OLD;"
    echo "Dropped $OLD."
    exit 0 ;;
esac

[[ -n "$FILE" ]] || { echo "Usage: restore.sh [--check|--yes] FILE.dump (--help)" >&2; exit 2; }
[[ -f "$FILE" ]] || { echo "No such file: $FILE" >&2; exit 1; }
FILE="$(realpath "$FILE")"

echo "Checking $FILE (restore into a disposable database):"
as_service "$PYTHON" "$TOOL" verify-backup "$FILE"
if [[ $CHECK -eq 1 ]]; then
  echo "Restore test passed. The live database was not touched."
  exit 0
fi
if [[ $YES -eq 0 ]]; then
  echo "The backup restores cleanly. Refusing to replace the live database without --yes." >&2
  exit 2
fi
[[ $EUID -eq 0 ]] || { echo "--yes stops and starts aion-host: run as root (sudo)." >&2; exit 2; }

STAMP="$(date -u +%Y%m%d_%H%M%S)"
INCOMING="aion_incoming_$STAMP" PREVIOUS="aion_pre_restore_$STAMP"
SAFETY="$BACKUP_DIR/before-restore-${STAMP/_/-}.dump"
echo "Safety backup of the live database: $SAFETY"
as_service "$PYTHON" "$TOOL" backup "$SAFETY"

WAS_RUNNING=0
if command -v systemctl &>/dev/null && systemctl is-active --quiet aion-host 2>/dev/null; then WAS_RUNNING=1; fi
restart_host() { [[ $WAS_RUNNING -eq 0 ]] || systemctl start aion-host; }
trap restart_host EXIT
[[ $WAS_RUNNING -eq 0 ]] || { echo "Stopping aion-host"; systemctl stop aion-host; }

db sql "CREATE DATABASE $INCOMING OWNER aion_owner;"
if ! db restore "$INCOMING" "$FILE"; then
  db sql "DROP DATABASE $INCOMING;" || true
  echo "Restore failed; the live database is unchanged." >&2
  exit 1
fi
# Swap: no session may stay on aion (the host is stopped; anything else is disconnected).
db sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='aion' AND pid<>pg_backend_pid();
ALTER DATABASE aion RENAME TO $PREVIOUS;
ALTER DATABASE $INCOMING RENAME TO aion;
REVOKE ALL ON DATABASE aion FROM PUBLIC;
GRANT CONNECT ON DATABASE aion TO aion_app,aion_auth;" >/dev/null
echo "Restored. The previous database is kept as $PREVIOUS (drop it later: restore.sh --yes --drop-old $PREVIOUS)."
