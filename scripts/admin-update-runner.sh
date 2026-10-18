#!/usr/bin/env bash
# Run by the f3m-admin-update systemd service, not by hand (admin-updates.sh installs it). It answers the admin page:
#   - an update was requested (update-requested): runs ./update.sh --yes and records its progress and output;
#   - otherwise (a "check now" request, the timer, or after an update): looks for new commits on the branch.
#
# Usage: admin-update-runner.sh <F3M folder> <exchange folder> <user>
#   <exchange folder> is Storage/Updates in the server's storage volume, as seen from the host.
#   <user> owns the F3M folder; git and update.sh run as that user, never as root.
#
# The exchange folder can be written by the web app. So this script only checks whether request files exist (it
# never reads them), and it removes whatever is in the place of a file before writing it, so a link planted there
# can't make root write anywhere else.
set -euo pipefail

REPO=${1:?F3M folder missing}
EXCHANGE=${2:?exchange folder missing}
OWNER=${3:?user missing}

UPDATE_REQUEST="$EXCHANGE/update-requested"
CHECK_REQUEST="$EXCHANGE/check-requested"
STATUS="$EXCHANGE/status.txt"
CHECK="$EXCHANGE/check.txt"
LOG="$EXCHANGE/update.log"

[ -d "$REPO" ] || { echo "F3M folder $REPO not found." >&2; exit 1; }
[ -d "$EXCHANGE" ] && [ ! -L "$EXCHANGE" ] || { echo "Exchange folder $EXCHANGE missing (or a link)." >&2; exit 1; }

# One run at a time. systemd makes RUNTIME_DIRECTORY for the service (RuntimeDirectory=).
exec 9>"${RUNTIME_DIRECTORY:-/run}/f3m-admin-update.lock"
flock -n 9 || exit 0

now() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# The web app reads the files; they belong to the folder's owner (the app's user inside the container).
APP_UID=$(stat -c %u "$EXCHANGE")
APP_GID=$(stat -c %g "$EXCHANGE")

# fresh FILE: an empty new file at FILE, owned by the app, whatever was there before.
fresh() {
    rm -rf -- "$1"
    (set -o noclobber; : > "$1")
    chown --no-dereference "$APP_UID:$APP_GID" "$1"
    chmod 644 "$1"
}

# write FILE: replaces FILE with what comes on stdin, in one step (the app never sees half a file).
write() {
    local temporary="$1.tmp"
    fresh "$temporary"
    cat > "$temporary"
    mv -fT -- "$temporary" "$1"
}

# as_owner COMMAND...: runs a command as the F3M folder's owner, in the F3M folder.
as_owner() {
    if [ "$(id -un)" = "$OWNER" ]; then
        (cd "$REPO" && "$@")
    else
        local home
        home=$(getent passwd "$OWNER" | cut -d: -f6)
        (cd "$REPO" && runuser -u "$OWNER" -- env HOME="$home" "$@")
    fi
}

# Looks for new commits and writes check.txt: the branch, the commit checked out, and one line per new commit.
check_for_updates() {
    local branch upstream head
    branch=$(as_owner git symbolic-ref --short -q HEAD || true)
    upstream=$(as_owner git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>/dev/null || true)
    head=$(as_owner git rev-parse HEAD)

    local error=""
    if [ -z "$upstream" ]; then
        error="The branch follows no remote branch."
    elif ! as_owner git fetch --quiet 2>/dev/null; then
        error="git fetch failed (no network, or no access to the repository)."
    fi

    {
        printf 'checked=%s\n' "$(now)"
        printf 'branch=%s\n' "$branch"
        printf 'upstream=%s\n' "$upstream"
        printf 'head=%s\n' "$head"
        [ -z "$error" ] || printf 'error=%s\n' "$error"
        if [ -n "$upstream" ]; then
            # Tabs separate the fields; a subject can't contain one (git turns it into a space).
            as_owner git log --format='commit=%H%x09%cI%x09%s' "HEAD..$upstream" | head -n 50
        fi
    } | write "$CHECK"
}

run_update() {
    rm -f -- "$UPDATE_REQUEST"
    printf 'state=running\nstarted=%s\n' "$(now)" | write "$STATUS"
    fresh "$LOG"

    local started code=0
    started=$(now)
    as_owner ./update.sh --yes >> "$LOG" 2>&1 || code=$?

    {
        if [ "$code" -eq 0 ]; then printf 'state=succeeded\n'; else printf 'state=failed\n'; fi
        printf 'started=%s\nfinished=%s\nexit=%s\nhead=%s\n' "$started" "$(now)" "$code" "$(as_owner git rev-parse HEAD)"
    } | write "$STATUS"
}

if [ -e "$UPDATE_REQUEST" ] || [ -L "$UPDATE_REQUEST" ]; then
    run_update
fi

rm -f -- "$CHECK_REQUEST"
check_for_updates
