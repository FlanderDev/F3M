#!/usr/bin/env bash
# Puts server backups into the local development server's storage (src/F3M.Server/Storage), and downloads them from a
# server. Runs on your own computer (Linux, macOS, or Git Bash on Windows), not on the server.
#
# Without options it shows the backups in backups/, newest first, to pick one from, and the other actions:
#   - Placing a backup moves the current local storage to backups/replaced/ (it can be put back from the menu) and
#     extracts the backup in its place. Then it asks which signing key the local server uses: the backup's (sets
#     Catalog:KeyPassword in the server's user secrets) or a new development key.
#   - Downloading takes the newest backup from a server over SSH: one of the saved connections (from ~/.ssh/config, and
#     the ones this script saved in ~/.config/f3m/servers), or one entered by hand. When the SSH user can't read the
#     backups (made with sudo by an older backup.sh, so they belong to root), it asks for the sudo password there.
#
# The backups contain .env and the signing key: keep backups/ private (it is git-ignored).
#
# Usage:
#   tools/backups.sh                Shows the menu
#   tools/backups.sh fetch          Downloads the newest backup from a server (and offers to place it)
#   tools/backups.sh place [FILE]   Places FILE, or a backup picked from backups/, in the local storage
#   tools/backups.sh undo           Puts a replaced local storage back
#   tools/backups.sh clean          Deletes old backups and replaced storages
set -euo pipefail

# The repository root, like the server's scripts: backups/ is there.
cd "$(dirname "$0")/.."

# shellcheck source=scripts/common.sh
source scripts/common.sh

usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit "${1:-0}"; }

case "${1:-}" in
    -h | --help) usage ;;
esac

[ -t 0 ] || fail "This script asks questions; run it in a terminal."
command -v tar >/dev/null 2>&1 || fail "tar is not installed."

SERVER_PROJECT="src/F3M.Server"
STORAGE="$SERVER_PROJECT/Storage"
LOCAL_DIR="backups"
# A backup is extracted here first, so a failed extraction leaves the local storage as it was. In the git-ignored
# backups/, on the same drive as the storage, so moving it into place is quick.
INCOMING="$LOCAL_DIR/.incoming"
REPLACED_DIR="$LOCAL_DIR/replaced"
# Written into the local storage when a backup is placed: the menu marks that backup.
PLACED_MARKER=".placed-from-backup"
SAVED_FILE="${XDG_CONFIG_HOME:-$HOME/.config}/f3m/servers"
DEFAULT_REMOTE_DIR="F3M"

# Removed when the script ends, also when it fails: half-finished downloads and extractions.
CLEANUP=()
trap 'for path in ${CLEANUP[@]+"${CLEANUP[@]}"}; do rm -rf -- "$path"; done' EXIT

# ask VAR "Question" [default]: Enter keeps the default.
ask() {
    # Odd local names: printf -v sets the caller's variable, unless a local here has the same name.
    local _reply _prompt="$2"
    [ -n "${3:-}" ] && _prompt="$_prompt [$3]"
    read -r -p "$_prompt: " _reply
    printf -v "$1" '%s' "${_reply:-${3:-}}"
}

# pick VAR "Question" COUNT [default]: a number from 0 to COUNT.
pick() {
    local _picked
    while true; do
        ask _picked "$2" "${4:-}"
        if [[ "$_picked" =~ ^[0-9]+$ ]] && [ "$_picked" -le "$3" ]; then
            printf -v "$1" '%s' "$_picked"
            return 0
        fi
        warn "Pick a number from the list."
    done
}

file_size() { wc -c <"$1" | tr -d ' '; }

human_size() {
    local bytes=$1
    if [ "$bytes" -ge 1073741824 ]; then
        awk -v b="$bytes" 'BEGIN { printf "%.1f GB", b / 1073741824 }'
    elif [ "$bytes" -ge 1048576 ]; then
        printf '%d MB' $((bytes / 1048576))
    else
        printf '%d KB' $(((bytes + 1023) / 1024))
    fi
}

# f3m-backup-20261029-120341.tar.gz, or Storage-20261029-120341: "2026-10-29 12:03".
date_of() {
    local stamp
    stamp=$(printf '%s' "$1" | grep -oE '[0-9]{8}-[0-9]{6}' | head -n 1) || true
    [ -n "$stamp" ] || return 0
    printf '%s-%s-%s %s:%s' "${stamp:0:4}" "${stamp:4:2}" "${stamp:6:2}" "${stamp:9:2}" "${stamp:11:2}"
}

# The archives in backups/, newest first (the name holds the time it was made).
list_backups() {
    find "$LOCAL_DIR" -maxdepth 1 -type f -name 'f3m-backup-*.tar.gz' 2>/dev/null | sort -r
}

list_replaced() {
    find "$REPLACED_DIR" -mindepth 1 -maxdepth 1 -type d -name 'Storage-*' 2>/dev/null | sort -r
}

placed_backup() {
    [ -f "$STORAGE/$PLACED_MARKER" ] && tr -d '\r\n' <"$STORAGE/$PLACED_MARKER"
    return 0
}

private_dir() {
    mkdir -p "$1"
    chmod 700 "$1" 2>/dev/null || true
}

# ── Local server ────────────────────────────────────────────────────────────

# The local server holds its database open: moving the storage away under it would fail on Windows and mix old and new
# files elsewhere. A server started as "dotnet F3M.Server.dll" isn't found here; the move then fails on Windows.
server_running() {
    case "$(uname -s)" in
        MINGW* | MSYS* | CYGWIN*) tasklist //FO CSV //NH 2>/dev/null | grep -qi '"F3M.Server.exe"' ;;
        *) pgrep -f 'F3M\.Server' >/dev/null 2>&1 ;;
    esac
}

require_server_stopped() {
    while server_running; do
        warn "The local F3M server is running. Stop it first: it keeps the database open."
        yes_no "Stopped it? Check again" y || fail "Nothing was changed."
    done
}

# Moves the current local storage to backups/replaced/, so it can be put back.
move_storage_away() {
    [ -d "$STORAGE" ] || return 0
    private_dir "$REPLACED_DIR"
    local destination
    destination="$REPLACED_DIR/Storage-$(date '+%Y%m%d-%H%M%S')"
    mv -- "$STORAGE" "$destination" ||
        fail "The local storage could not be moved; something still uses it (the local server, or an open database)."
    info "The previous local storage is in $destination."
}

# ── Database version ────────────────────────────────────────────────────────

# The migrations a database has, one per line; nothing when neither sqlite3 nor Python can read it.
applied_migrations() {
    local db=$1 python
    [ -f "$db" ] || return 0
    if command -v sqlite3 >/dev/null 2>&1; then
        sqlite3 -readonly "$db" 'SELECT MigrationId FROM "__EFMigrationsHistory";' 2>/dev/null | tr -d '\r' && return 0
    fi
    # python3 first; on Windows it may be the Store's stand-in, which can't import anything.
    for python in python3 python; do
        if command -v "$python" >/dev/null 2>&1 && "$python" -c 'import sqlite3' >/dev/null 2>&1; then
            "$python" - "$db" 2>/dev/null <<'EOF' | tr -d '\r'
import sqlite3, sys
db = sqlite3.connect("file:" + sys.argv[1] + "?mode=ro", uri=True)
for (name,) in db.execute('SELECT MigrationId FROM "__EFMigrationsHistory"'):
    print(name)
EOF
            return 0
        fi
    done
}

# Warns when the backup's database was made by code this checkout doesn't have: the local server can't open it then.
# False when the user doesn't want to go on.
check_migrations() {
    local applied known unknown pending
    applied=$(applied_migrations "$1")
    if [ -z "$applied" ]; then
        info "${DIM}The database's version wasn't checked (needs sqlite3 or Python).${RESET}"
        return 0
    fi
    known=$(find "$SERVER_PROJECT/Migrations" -maxdepth 1 -name '[0-9]*_*.cs' ! -name '*.Designer.cs' 2>/dev/null |
        sed 's#.*/##; s#\.cs$##' | sort)
    unknown=$(comm -23 <(printf '%s\n' "$applied" | sort) <(printf '%s\n' "$known"))
    pending=$(comm -13 <(printf '%s\n' "$applied" | sort) <(printf '%s\n' "$known"))
    if [ -n "$unknown" ]; then
        warn "The backup's database has migrations this checkout doesn't know, so the local server can't use it:"
        printf '%s\n' "$unknown" | sed 's/^/    /' >&2
        info "Switch to a branch that has them (e.g. the one the server runs) before starting the local server."
        yes_no "Place it anyway?" n || return 1
    elif [ -n "$pending" ]; then
        info "${DIM}The local server applies $(printf '%s\n' "$pending" | wc -l | tr -d ' ') newer migration(s) when it starts.${RESET}"
    fi
    return 0
}

# ── Signing key ─────────────────────────────────────────────────────────────

# secret_set PASSWORD / secret_remove: Catalog:KeyPassword in the server project's user secrets (outside the repository).
# The password goes in as JSON on the input, so it doesn't show up in the process list.
secret_set() {
    local bs='\' quote='"' escaped
    escaped=${1//"$bs"/"$bs$bs"}
    escaped=${escaped//"$quote"/"$bs$quote"}
    printf '{ "Catalog:KeyPassword": "%s" }' "$escaped" | dotnet user-secrets set --project "$SERVER_PROJECT" >/dev/null
}

secret_remove() {
    dotnet user-secrets remove Catalog:KeyPassword --project "$SERVER_PROJECT" >/dev/null 2>&1 || true
}

# choose_signing_key PASSWORD: the backup's key (with its password), or a new development key.
choose_signing_key() {
    local password=$1 choice=1
    step "Signing key"
    if [ -z "$password" ]; then
        info "The backup has no key password (no .env), so the local server can't open the backup's key."
        choice=2
    else
        info "  1) The backup's key: sets Catalog:KeyPassword in the local server's user secrets"
        info "  2) A new development key: removes Catalog:KeyPassword and the backup's key"
        pick choice "Signing key" 2 1
        [ "$choice" -eq 0 ] && choice=1
    fi

    if ! command -v dotnet >/dev/null 2>&1; then
        if [ "$choice" -eq 1 ]; then
            info "dotnet isn't installed here. Set the password (F3M_CATALOG_KEY_PASSWORD in the backup's .env) with:"
            info "  dotnet user-secrets set Catalog:KeyPassword <password> --project $SERVER_PROJECT"
        else
            rm -f -- "$STORAGE/Secrets/catalog-signing.pem"
            info "Removed the backup's key. If Catalog:KeyPassword is set, remove it with:"
            info "  dotnet user-secrets remove Catalog:KeyPassword --project $SERVER_PROJECT"
        fi
        return 0
    fi

    if [ "$choice" -eq 1 ]; then
        secret_set "$password" || { warn "Setting Catalog:KeyPassword failed; dotnet's message is above."; return 0; }
        info "${GREEN}Catalog:KeyPassword is set.${RESET} The local server signs with the server's key: desktop apps trust what it publishes."
    else
        rm -f -- "$STORAGE/Secrets/catalog-signing.pem"
        secret_remove
        info "${GREEN}The local server makes a new development key when it starts.${RESET}"
        info "The catalog from the backup is still signed with the server's key: rebuild it on the admin page (Mod catalog)."
    fi
}

# ── Place a backup ──────────────────────────────────────────────────────────

place_backup() {
    local file=$1 name password=""
    name=$(basename "$file")
    [ -f "$file" ] || fail "No such file: $file"

    step "Placing $name"
    private_dir "$LOCAL_DIR"
    rm -rf -- "$INCOMING"
    private_dir "$INCOMING"
    CLEANUP+=("$INCOMING")
    # Through the input: Git Bash's tar reads a path like C:/... as a remote host.
    tar -xzf - -C "$INCOMING" <"$file" || fail "$name could not be extracted."
    [ -d "$INCOMING/Storage" ] || fail "$name is not an F3M backup (it has no Storage folder)."

    check_migrations "$INCOMING/Storage/Database/F3M.db" || { info "Nothing was changed."; return 0; }

    if [ -d "$STORAGE" ]; then
        info "The current local storage moves to $REPLACED_DIR/ (the menu can put it back)."
        yes_no "Replace the local storage with $name?" y || { info "Nothing was changed."; return 0; }
    fi
    require_server_stopped

    if [ -f "$INCOMING/.env" ]; then
        password=$(ENV_FILE="$INCOMING/.env" env_value F3M_CATALOG_KEY_PASSWORD)
    fi
    move_storage_away
    mv -- "$INCOMING/Storage" "$STORAGE"
    printf '%s\n' "$name" >"$STORAGE/$PLACED_MARKER"
    rm -rf -- "$INCOMING"
    info "${GREEN}${BOLD}Placed $name${RESET} in $STORAGE."

    choose_signing_key "$password"
}

# ── Put a replaced storage back ─────────────────────────────────────────────

undo_replace() {
    local replaced=() choice chosen
    mapfile -t replaced < <(list_replaced)
    [ ${#replaced[@]} -gt 0 ] || { info "There is no replaced storage in $REPLACED_DIR."; return 0; }

    step "Put a replaced storage back"
    for i in "${!replaced[@]}"; do
        local from=" ${DIM}(the local server's own)${RESET}"
        [ -f "${replaced[$i]}/$PLACED_MARKER" ] && from=" ${DIM}(from $(tr -d '\r\n' <"${replaced[$i]}/$PLACED_MARKER"))${RESET}"
        printf '  %2d) replaced %s%s\n' "$((i + 1))" "$(date_of "${replaced[$i]##*/}")" "$from"
    done
    printf '  %2d) %s\n' 0 "Back"
    pick choice "Put back" ${#replaced[@]} 1
    [ "$choice" -eq 0 ] && return 0
    chosen=${replaced[$((choice - 1))]}

    require_server_stopped
    move_storage_away
    mv -- "$chosen" "$STORAGE"
    info "${GREEN}${BOLD}Put back${RESET} the storage replaced $(date_of "${chosen##*/}")."
    info "If it uses another signing key than the storage before, set or remove Catalog:KeyPassword:"
    info "  dotnet user-secrets set Catalog:KeyPassword <password> --project $SERVER_PROJECT"
    info "  dotnet user-secrets remove Catalog:KeyPassword --project $SERVER_PROJECT"
}

# ── Delete old backups ──────────────────────────────────────────────────────

clean_up() {
    local backups=() replaced=() keep old=()
    mapfile -t backups < <(list_backups)
    mapfile -t replaced < <(list_replaced)
    step "Delete old backups"
    info "${#backups[@]} backup(s) in $LOCAL_DIR, ${#replaced[@]} replaced storage(s) in $REPLACED_DIR."

    while true; do
        ask keep "Keep how many of the newest backups" "5"
        [[ "$keep" =~ ^[0-9]+$ ]] && break
        warn "Enter a number."
    done
    [ ${#backups[@]} -gt "$keep" ] && old+=("${backups[@]:$keep}")

    while true; do
        ask keep "Keep how many of the newest replaced storages" "2"
        [[ "$keep" =~ ^[0-9]+$ ]] && break
        warn "Enter a number."
    done
    [ ${#replaced[@]} -gt "$keep" ] && old+=("${replaced[@]:$keep}")

    [ ${#old[@]} -gt 0 ] || { info "Nothing to delete."; return 0; }
    info "To delete:"
    printf '    %s\n' "${old[@]}"
    yes_no "Delete them for good?" n || { info "Nothing was deleted."; return 0; }
    rm -rf -- "${old[@]}"
    info "Deleted ${#old[@]}."
}

# ── Download from a server ──────────────────────────────────────────────────

# Sets FETCHED to the downloaded file.
fetch_backup() {
    local targets=() ports=() keys=() dirs=() choice target port key dir i
    FETCHED=""

    if [ -f "$SAVED_FILE" ]; then
        # One connection per line, separated by |: target (user@host or an ~/.ssh/config alias), port, key file, F3M
        # folder on the server. Empty fields use ssh's defaults; an empty folder is asked for.
        while IFS='|' read -r target port key dir; do
            [ -n "$target" ] && { targets+=("$target"); ports+=("$port"); keys+=("$key"); dirs+=("$dir"); }
        done <"$SAVED_FILE"
    fi
    if [ -f "$HOME/.ssh/config" ]; then
        # Every Host name without wildcards; ssh itself knows their user, port and key.
        while read -r target; do
            targets+=("$target"); ports+=(""); keys+=(""); dirs+=("")
        done < <(awk 'tolower($1) == "host" { for (i = 2; i <= NF; i++) if ($i !~ /[*?!]/) print $i }' "$HOME/.ssh/config")
    fi

    step "Server"
    for i in "${!targets[@]}"; do
        local extra=""
        [ -n "${ports[$i]}" ] && extra="$extra, port ${ports[$i]}"
        [ -n "${keys[$i]}" ] && extra="$extra, key ${keys[$i]##*/}"
        [ -n "${dirs[$i]}" ] && extra="$extra, folder ${dirs[$i]}"
        printf '  %2d) %s' "$((i + 1))" "${targets[$i]}"
        [ -n "$extra" ] && printf ' %s(%s)%s' "$DIM" "${extra#, }" "$RESET"
        printf '\n'
    done
    printf '  %2d) %s\n' 0 "Enter a connection"
    pick choice "Connect to" ${#targets[@]} "$([ ${#targets[@]} -gt 0 ] && echo 1 || echo 0)"

    if [ "$choice" -eq 0 ]; then
        target=""
        while [ -z "$target" ]; do ask target "User and host, e.g. flan@203.0.113.7"; done
        while true; do
            ask port "Port" "22"
            [[ "$port" =~ ^[0-9]+$ ]] && break
            warn "The port is a number."
        done
        [ "$port" = "22" ] && port=""

        # The private keys in ~/.ssh: the files that have a .pub next to them.
        local found_keys=() pub key_choice
        for pub in "$HOME"/.ssh/*.pub; do
            [ -f "${pub%.pub}" ] && found_keys+=("${pub%.pub}")
        done
        key=""
        if [ ${#found_keys[@]} -gt 0 ]; then
            info "Keys in ~/.ssh:"
            for i in "${!found_keys[@]}"; do
                printf '  %2d) %s\n' "$((i + 1))" "${found_keys[$i]##*/}"
            done
            printf '  %2d) %s\n' 0 "ssh's default"
            pick key_choice "Key" ${#found_keys[@]} 0
            [ "$key_choice" -gt 0 ] && key="${found_keys[$((key_choice - 1))]}"
        fi

        ask dir "F3M folder on the server (relative to the home folder)" "$DEFAULT_REMOTE_DIR"
        if yes_no "Save this connection?" y; then
            mkdir -p "$(dirname "$SAVED_FILE")"
            printf '%s|%s|%s|%s\n' "$target" "$port" "$key" "$dir" >>"$SAVED_FILE"
            info "${DIM}Saved in $SAVED_FILE.${RESET}"
        fi
    else
        i=$((choice - 1))
        target=${targets[$i]}; port=${ports[$i]}; key=${keys[$i]}; dir=${dirs[$i]}
        [ -n "$dir" ] || ask dir "F3M folder on the server (relative to the home folder)" "$DEFAULT_REMOTE_DIR"
    fi

    # ~/F3M means the same as F3M: the commands start in the home folder.
    case "$dir" in
        "~") dir="." ;;
        "~/"*) dir=${dir#"~/"} ;;
    esac

    # ssh ignores a key that others can read. Not checked on Windows, where Git Bash shows every file as 644.
    case "$(uname -s)" in
        MINGW* | MSYS* | CYGWIN*) ;;
        *)
            if [ -n "$key" ] && [ -f "$key" ]; then
                local mode
                mode=$(stat -c '%a' "$key" 2>/dev/null || stat -f '%Lp' "$key" 2>/dev/null || true)
                # The last two digits are the group's and everyone else's rights.
                if [[ "$mode" =~ ^[0-7]{3,4}$ ]] && [ "${mode: -2}" != "00" ]; then
                    warn "Others can read the key $key (permissions $mode), so ssh would ignore it."
                    if yes_no "Make it readable only for you (chmod 600)?" y; then
                        chmod 600 "$key"
                    else
                        fail "Fix it with: chmod 600 $key"
                    fi
                fi
            fi
            ;;
    esac

    SSH=(ssh -o ConnectTimeout=15)
    [ -n "$port" ] && SSH+=(-p "$port")
    [ -n "$key" ] && SSH+=(-i "$key" -o IdentitiesOnly=yes)
    SSH+=("$target")
    REMOTE_DIR=$dir

    # One line: printf %q quotes line breaks in a way only bash understands.
    local find_newest
    find_newest='[ -d backups ] || exit 5; [ -r backups ] && [ -x backups ] || exit 4;'
    find_newest+=' f=$(ls -1t backups/f3m-backup-*.tar.gz 2>/dev/null | head -n 1); [ -n "$f" ] || exit 5; [ -r "$f" ] || exit 4;'
    find_newest+=' printf "%s\n%s\n" "$f" "$(wc -c <"$f")"'

    step "Looking for the newest backup on $target"
    local password="" status=0 found
    found=$(remote "$find_newest") || status=$?
    if [ "$status" -eq 4 ]; then
        info "The backups belong to another user (made with sudo), so they are read with sudo."
        read -r -s -p "Sudo password of the SSH user on the server: " password
        printf '\n'
        status=0
        found=$(remote "$find_newest" "$password") || status=$?
    fi
    case "$status" in
        0) ;;
        3) fail "There is no folder $dir on the server. Enter the folder backup.sh is in." ;;
        4) fail "The backups on the server can't be read, also not with sudo." ;;
        5) fail "There is no backup in $dir/backups on the server yet. Make one there with: sudo bash backup.sh" ;;
        255) fail "Could not connect to $target. ssh's message above should say why." ;;
        *) fail "Looking for the backup failed (exit code $status); a wrong sudo password is a common cause." ;;
    esac

    local remote_file remote_size name local_file part
    remote_file=$(printf '%s\n' "$found" | sed -n 1p)
    remote_size=$(printf '%s\n' "$found" | sed -n 2p | tr -d ' \r')
    name=${remote_file##*/}
    info "Newest: $name ($(human_size "$remote_size"))"

    private_dir "$LOCAL_DIR"
    local_file="$LOCAL_DIR/$name"
    if [ -f "$local_file" ] && [ "$(file_size "$local_file")" = "$remote_size" ]; then
        info "${DIM}Already downloaded: $local_file${RESET}"
    else
        step "Downloading"
        part="$local_file.part"
        CLEANUP+=("$part")
        (umask 077 && remote "cat -- $(printf '%q' "$remote_file")" "$password" >"$part") || fail "The download failed."
        [ "$(file_size "$part")" = "$remote_size" ] || fail "The download is incomplete; try again."
        mv "$part" "$local_file"
        info "Saved $local_file"
    fi
    FETCHED=$local_file
}

# remote "shell script" [password]: runs the script in the F3M folder on the server, with sudo when a password is given
# (it goes to sudo on the input, before anything else). Exit codes of the script: 3 no such folder, 4 not readable,
# 5 no backup.
remote() {
    local script="cd $(printf '%q' "$REMOTE_DIR") || exit 3; $1"
    if [ -n "${2:-}" ]; then
        printf '%s\n' "$2" | "${SSH[@]}" "sudo -S -p '' sh -c $(printf '%q' "$script")"
    else
        "${SSH[@]}" "sh -c $(printf '%q' "$script")" </dev/null
    fi
}

fetch_and_offer() {
    command -v ssh >/dev/null 2>&1 || fail "ssh is not installed."
    fetch_backup
    if [ "$(placed_backup)" = "$(basename "$FETCHED")" ]; then
        info "${DIM}It is already in the local storage.${RESET}"
    elif yes_no "Place it in the local storage now?" y; then
        place_backup "$FETCHED"
    fi
}

# ── Menu ────────────────────────────────────────────────────────────────────

menu() {
    local backups=() replaced=() placed choice default i
    mapfile -t backups < <(list_backups)
    mapfile -t replaced < <(list_replaced)
    placed=$(placed_backup)

    step "Backups"
    if [ -n "$placed" ]; then
        info "Local storage: from $placed"
    elif [ -d "$STORAGE" ]; then
        info "Local storage: the local server's own"
    else
        info "Local storage: none yet"
    fi
    info ""
    if [ ${#backups[@]} -eq 0 ]; then
        info "  ${DIM}No backups in $LOCAL_DIR yet.${RESET}"
    fi
    for i in "${!backups[@]}"; do
        local name=${backups[$i]##*/} note=""
        [ "$i" -eq 0 ] && note=" ${DIM}newest${RESET}"
        [ "$name" = "$placed" ] && note="$note ${GREEN}in use${RESET}"
        printf '  %2d) %s  %8s%s\n' "$((i + 1))" "$(date_of "$name")" "$(human_size "$(file_size "${backups[$i]}")")" "$note"
    done
    info ""
    info "   s) Download the newest backup from a server"
    [ ${#replaced[@]} -gt 0 ] && info "   u) Put a replaced storage back (${#replaced[@]})"
    [ ${#backups[@]} -gt 0 ] || [ ${#replaced[@]} -gt 0 ] && info "   c) Delete old backups"
    info "   q) Quit"

    default=s
    [ ${#backups[@]} -gt 0 ] && default=1
    while true; do
        ask choice "Choose" "$default"
        choice=$(printf '%s' "$choice" | tr '[:upper:]' '[:lower:]')
        case "$choice" in
            s) fetch_and_offer; return 0 ;;
            u) [ ${#replaced[@]} -gt 0 ] && { undo_replace; return 0; } ;;
            c) clean_up; return 0 ;;
            q) return 1 ;;
            *)
                if [[ "$choice" =~ ^[0-9]+$ ]] && [ "$choice" -ge 1 ] && [ "$choice" -le ${#backups[@]} ]; then
                    place_backup "${backups[$((choice - 1))]}"
                    return 0
                fi
                ;;
        esac
        warn "Pick a backup's number or a letter from the list."
    done
}

case "${1:-}" in
    "") while menu; do :; done ;;
    fetch) fetch_and_offer ;;
    place) if [ -n "${2:-}" ]; then place_backup "$2"; else menu || true; fi ;;
    undo) undo_replace ;;
    clean) clean_up ;;
    *) warn "Unknown option: $1"; usage 1 ;;
esac
