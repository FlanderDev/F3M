#!/usr/bin/env bash
# Backs up the F3M server, or restores a backup.
#
# A backup is one .tar.gz with the storage volume (database, uploads, catalog, signing key, data-protection keys)
# and .env (the catalog key password and the other settings). The signing key is only usable with that password,
# so keep the archive as safe as the password itself. The server is stopped for the few seconds the copy takes, so
# the database is copied in a consistent state, and started again afterwards.
#
# Usage:
#   ./backup.sh                      Make a backup in ./backups
#   ./backup.sh --dir /srv/backups   ... somewhere else
#   ./backup.sh --keep 14            ... and delete all but the 14 newest backups there
#   ./backup.sh --with-downloads     ... including the import's downloads (Storage/Import/files; they can be
#                                    downloaded again, so they are left out by default)
#   ./backup.sh --restore FILE       Replace the server's data with a backup (asks first; --yes skips that)
#
# Making backups asks nothing, so it can run from cron, e.g. daily at 4:00:
#   0 4 * * * /path/to/F3M/backup.sh --keep 14 >> /path/to/F3M/backups/backup.log 2>&1
set -euo pipefail

cd "$(dirname "$0")"

# shellcheck source=scripts/common.sh
source scripts/common.sh

BACKUP_DIR="backups"
KEEP=""
WITH_DOWNLOADS=false
RESTORE=""
ASSUME_YES=false

usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit "${1:-0}"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --dir) BACKUP_DIR=${2:?--dir needs a folder}; shift 2 ;;
        --keep) KEEP=${2:?--keep needs a number}; shift 2 ;;
        --with-downloads) WITH_DOWNLOADS=true; shift ;;
        --restore) RESTORE=${2:?--restore needs a backup file}; shift 2 ;;
        -y | --yes) ASSUME_YES=true; shift ;;
        -h | --help) usage ;;
        *) warn "Unknown option: $1"; usage 1 ;;
    esac
done

if [ -n "$KEEP" ] && ! [[ "$KEEP" =~ ^[1-9][0-9]*$ ]]; then
    fail "--keep needs a number of 1 or more."
fi

require_docker

# The server's image runs the copy, with the storage volume mounted like in the server. Root inside the throwaway
# container, so it can read and write every file of the volume.
run_in_image() {
    "${COMPOSE[@]}" run --rm --no-deps --user root --entrypoint sh "$@"
}

# Stops the server while the data is copied, and makes sure it runs again afterwards, also when the copy fails.
WAS_RUNNING=false
STOPPED=false
stop_server() {
    if container_running; then
        WAS_RUNNING=true
        info "Stopping the server for the copy…"
        "${COMPOSE[@]}" stop "$SERVICE" >/dev/null
        STOPPED=true
    fi
}

start_server_again() {
    if [ "$STOPPED" = true ]; then
        STOPPED=false
        info "Starting the server again…"
        "${COMPOSE[@]}" start "$SERVICE" >/dev/null
        wait_until_healthy
    fi
}
trap 'start_server_again' EXIT

# ── Restore ─────────────────────────────────────────────────────────────────

if [ -n "$RESTORE" ]; then
    [ -f "$RESTORE" ] || fail "No such file: $RESTORE"
    command -v tar >/dev/null 2>&1 || fail "tar is not installed."
    tar -tzf "$RESTORE" Storage >/dev/null 2>&1 || fail "$RESTORE is not an F3M backup (it has no Storage folder)."

    restore_dir=$(cd "$(dirname "$RESTORE")" && pwd)
    restore_name=$(basename "$RESTORE")
    has_env=false
    tar -tzf "$RESTORE" .env >/dev/null 2>&1 && has_env=true

    step "Restoring $restore_name"
    info "This replaces the server's database, uploads, catalog and keys with the backup's."
    info "Downloads of the import (Storage/Import/files) are kept when the backup has none."
    if [ "$ASSUME_YES" != true ]; then
        [ -t 0 ] || fail "Restoring asks first; run it in a terminal, or add --yes."
        yes_no "Replace the server's data now?" n || fail "Nothing was changed."
    fi

    replace_env=false
    if [ "$has_env" = true ]; then
        if [ ! -f "$ENV_FILE" ] || ! tar -xzOf "$RESTORE" .env | cmp -s - "$ENV_FILE"; then
            info "The backup's .env differs from the current one. The restored signing key needs the backup's"
            info "catalog key password, which is in it."
            if [ "$ASSUME_YES" = true ] || yes_no "Use the backup's .env (the current one is kept as .env.bak)?" y; then
                replace_env=true
            fi
        fi
    else
        warn "The backup has no .env. The restored signing key needs the catalog key password it was made with."
    fi

    # The user the server runs as, to give it the restored files.
    app_user=$("${COMPOSE[@]}" run --rm --no-deps --entrypoint id "$SERVICE" -u 2>/dev/null | tr -d '\r' | tail -n 1)
    [[ "$app_user" =~ ^[0-9]+$ ]] || fail "Could not find out which user the server runs as."

    stop_server
    # From here on a failure leaves the server stopped: started on half-restored data it would make a new signing key.
    STOPPED=false

    # Everything in the volume is replaced, except the import's downloads when the backup has none.
    keep_downloads=true
    tar -tzf "$RESTORE" Storage/Import/files >/dev/null 2>&1 && keep_downloads=false
    run_in_image -v "$restore_dir:/backup:ro" "$SERVICE" -c "
        set -e
        cd /app/Storage
        for entry in * .[!.]*; do
            [ -e \"\$entry\" ] || continue
            if [ $keep_downloads = true ] && [ \"\$entry\" = Import ]; then
                find Import -mindepth 1 -maxdepth 1 ! -name files -exec rm -rf {} +
            else
                rm -rf \"\$entry\"
            fi
        done
        tar -xzf \"/backup/$restore_name\" -C /app Storage
        chown -R $app_user:$app_user /app/Storage
    " || fail "The restore failed. The server stays stopped, so it does not start on half-restored data; fix the cause and run the restore again."

    if [ "$replace_env" = true ]; then
        [ -f "$ENV_FILE" ] && cp -p "$ENV_FILE" "$ENV_FILE.bak"
        (umask 077 && tar -xzOf "$RESTORE" .env >"$ENV_FILE")
        chmod 600 "$ENV_FILE"
        info "Restored .env."
    fi

    step "Starting the server"
    # up, not start: a changed .env only applies to a new container.
    "${COMPOSE[@]}" up -d >/dev/null
    wait_until_healthy
    info "${GREEN}${BOLD}Restored $restore_name. F3M is running at $(site_url)${RESET}"
    exit 0
fi

# ── Backup ──────────────────────────────────────────────────────────────────

[ -f "$ENV_FILE" ] || warn "No .env next to compose.yaml; the backup has no catalog key password."

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
BACKUP_DIR=$(cd "$BACKUP_DIR" && pwd)
name="f3m-backup-$(date '+%Y%m%d-%H%M%S').tar.gz"

exclude=""
[ "$WITH_DOWNLOADS" = true ] || exclude="--exclude=Storage/Import/files"

env_mount=()
env_entry=""
if [ -f "$ENV_FILE" ]; then
    env_mount=(-v "$PWD/$ENV_FILE:/app/.env:ro")
    env_entry=".env"
fi

step "Backing up to $BACKUP_DIR/$name"
stop_server
run_in_image -v "$BACKUP_DIR:/backup" ${env_mount[@]+"${env_mount[@]}"} "$SERVICE" -c "
    set -e
    tar -czf /backup/$name.part $exclude --exclude='Storage/.health-*' -C /app Storage $env_entry
    chown $(id -u):$(id -g) /backup/$name.part
    chmod 600 /backup/$name.part
"
mv "$BACKUP_DIR/$name.part" "$BACKUP_DIR/$name"
start_server_again

size=$(du -h "$BACKUP_DIR/$name" | cut -f1)
info "${GREEN}${BOLD}Backup done:${RESET} $BACKUP_DIR/$name ($size)"
[ -n "$env_entry" ] && info "${DIM}It contains .env with the passwords: keep it private.${RESET}"

if [ -n "$KEEP" ]; then
    old=$(find "$BACKUP_DIR" -maxdepth 1 -name 'f3m-backup-*.tar.gz' | sort -r | tail -n +"$((KEEP + 1))")
    if [ -n "$old" ]; then
        printf '%s\n' "$old" | while read -r file; do rm -f -- "$file"; done
        info "Deleted $(printf '%s\n' "$old" | wc -l | tr -d ' ') older backup(s); the $KEEP newest are kept."
    fi
fi

[ "$WAS_RUNNING" = true ] || info "${DIM}The server was not running, and still isn't.${RESET}"
