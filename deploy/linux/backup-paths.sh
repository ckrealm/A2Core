#!/usr/bin/env bash
# Lets the sandboxed aion-backup.service write to the backup folders named in /etc/aion/backup.env.
#   sudo /opt/aion/bin/backup-paths.sh     # after changing AION_BACKUP_DIR or AION_BACKUP_COPY_DIR (install.sh runs it too)
# The unit runs with ProtectSystem=strict: everything outside its ReadWritePaths is read-only to it, so a custom
# AION_BACKUP_DIR (e.g. /mnt/d/aion-backups) or an AION_BACKUP_COPY_DIR (e.g. /mnt/c/Users/<you>/A2Core-Backups on WSL)
# must be listed. This writes the drop-in aion-backup.service.d/10-backup-paths.conf with both folders, creates them
# (owned by the service account) and reloads systemd. Without either setting the drop-in is removed (the unit's own
# /var/lib/aion/backups is enough).
# Settings for tests: AION_BACKUP_ENV (/etc/aion/backup.env), AION_SYSTEMD_DIR (/etc/systemd/system), AION_RUN_AS (aion;
# empty: folders are not chowned), AION_NO_RELOAD=1 (no systemctl daemon-reload).
set -euo pipefail

ENV_FILE="${AION_BACKUP_ENV:-/etc/aion/backup.env}"
UNIT_DIR="${AION_SYSTEMD_DIR:-/etc/systemd/system}/aion-backup.service.d"
DROP_IN="$UNIT_DIR/10-backup-paths.conf"
RUN_AS="${AION_RUN_AS-aion}"
DEFAULT_DIR=/var/lib/aion/backups

# The value of KEY in the environment file (systemd EnvironmentFile syntax: KEY=VALUE, optional surrounding quotes);
# the last assignment wins, as in systemd.
setting() {
  local key="$1" line value=""
  [[ -f "$ENV_FILE" ]] || return 0
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line#"${line%%[![:space:]]*}"}"
    [[ "$line" == "$key="* ]] || continue
    value="${line#"$key="}"
    value="${value%"${value##*[![:space:]]}"}"
    if [[ "$value" =~ ^\"(.*)\"$ || "$value" =~ ^\'(.*)\'$ ]]; then value="${BASH_REMATCH[1]}"; fi
  done < "$ENV_FILE"
  printf '%s' "$value"
}

PATHS=()
for key in AION_BACKUP_DIR AION_BACKUP_COPY_DIR; do
  dir="$(setting "$key")"
  [[ -z "$dir" || "$dir" == "$DEFAULT_DIR" ]] && continue
  # Absolute, and nothing systemd would read as a specifier, quote or separator.
  if [[ "$dir" != /* || ! "$dir" =~ ^[A-Za-z0-9_./@+\ -]+$ || "$dir" == *..* ]]; then
    echo "$key=$dir in $ENV_FILE: use an absolute path of letters, digits, spaces and _ . / @ + -" >&2
    exit 2
  fi
  # ProtectHome=true hides these from the unit entirely; ReadWritePaths cannot reopen them.
  if [[ "$dir" == /home || "$dir" == /home/* || "$dir" == /root || "$dir" == /root/* || "$dir" == /run/user/* ]]; then
    echo "$key=$dir in $ENV_FILE: the backup unit cannot see /home, /root or /run/user (ProtectHome); pick another folder" >&2
    exit 2
  fi
  PATHS+=("$dir")
done

if [[ ${#PATHS[@]} -eq 0 ]]; then
  if [[ -e "$DROP_IN" ]]; then rm -f -- "$DROP_IN"; echo "Removed $DROP_IN (backups only in $DEFAULT_DIR)."; fi
else
  for dir in "${PATHS[@]}"; do
    if [[ -n "$RUN_AS" ]] && id "$RUN_AS" &>/dev/null; then
      # A Windows drive (drvfs) may refuse the owner change; the folder is still usable when the mount lets everyone write.
      mkdir -p -- "$dir" && { chown "$RUN_AS:$RUN_AS" -- "$dir" 2>/dev/null || true; chmod 750 -- "$dir" 2>/dev/null || true; }
    else
      mkdir -p -- "$dir"
    fi
  done
  mkdir -p -- "$UNIT_DIR"
  {
    echo "# Written by backup-paths.sh from $ENV_FILE; run it again after changing the backup folders there."
    echo "[Service]"
    # "-": a folder that is missing at start (an unmounted drive) does not stop the unit; backup.sh then reports it.
    for dir in "${PATHS[@]}"; do echo "ReadWritePaths=\"-$dir\""; done
  } > "$DROP_IN.new"
  mv -- "$DROP_IN.new" "$DROP_IN"
  echo "aion-backup may now write to: ${PATHS[*]} ($DROP_IN)"
fi
if [[ "${AION_NO_RELOAD:-}" != 1 ]] && command -v systemctl &>/dev/null; then systemctl daemon-reload; fi
