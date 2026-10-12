#!/usr/bin/env bash
# Updates the F3M server: pulls the newest code, rebuilds the image and restarts the container, then waits until the
# server is healthy. Offers a backup first (./backup.sh). The settings in .env stay as they are.
#
# Usage:
#   ./update.sh          Asks before the backup and before rebuilding
#   ./update.sh --yes    Asks nothing: makes a backup, and only rebuilds when there are new commits (e.g. for cron)
set -euo pipefail

cd "$(dirname "$0")"

# shellcheck source=scripts/common.sh
source scripts/common.sh

ASSUME_YES=false
case "${1:-}" in
    -y | --yes) ASSUME_YES=true ;;
    -h | --help) sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
    "") ;;
    *) fail "Unknown option: $1 (see --help)" ;;
esac

if [ "$ASSUME_YES" != true ] && [ ! -t 0 ]; then
    fail "This script asks questions; run it in a terminal, or add --yes."
fi

# confirm "Question" y|n: the default with --yes, otherwise asks.
confirm() {
    if [ "$ASSUME_YES" = true ]; then
        [ "$2" = "y" ]
    else
        yes_no "$1" "$2"
    fi
}

# Everything below is one block, so bash reads it completely before it runs: the pull may replace this file.
{
require_docker
[ -f "$ENV_FILE" ] || fail "No .env yet. Set the server up with ./setup.sh first."
command -v git >/dev/null 2>&1 || fail "git is not installed."
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "This folder is not a git checkout, so it can't be updated with git."

# ── New code ────────────────────────────────────────────────────────────────

step "Looking for updates"
branch=$(git symbolic-ref --short -q HEAD) || fail "No branch is checked out (detached HEAD). Check out a branch first, e.g. git checkout release."
upstream=$(git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>/dev/null) \
    || fail "Branch $branch follows no remote branch. Set one, e.g. git branch --set-upstream-to=origin/$branch."

if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    git status --short --untracked-files=no >&2
    fail "There are local changes to tracked files (above). Commit or undo them first, so the update doesn't mix with them."
fi

git fetch --quiet
old_commit=$(git rev-parse HEAD)
incoming=$(git log --oneline "HEAD..$upstream")

rebuild=true
if [ -z "$incoming" ]; then
    info "$branch is up to date with $upstream."
    confirm "Rebuild and restart anyway?" n || rebuild=false
else
    info "New on $upstream:"
    printf '%s\n' "$incoming" | sed 's/^/  /'
fi

if [ "$rebuild" != true ]; then
    info "Nothing to do."
    exit 0
fi

# ── Backup ──────────────────────────────────────────────────────────────────

if container_running && confirm "Make a backup first (the server stops for a few seconds)?" y; then
    ./backup.sh
fi

# ── Update ──────────────────────────────────────────────────────────────────

if [ -n "$incoming" ]; then
    step "Pulling $upstream"
    git merge --ff-only --quiet "$upstream" || fail "$branch and $upstream have gone separate ways; merge them by hand."
fi

FAIL_HINT="To go back to the version before the update:
  git checkout $old_commit && ${COMPOSE[*]} up --build -d
  (then git checkout $branch, once the problem is fixed upstream)"

step "Rebuilding and restarting"
"${COMPOSE[@]}" up --build -d

step "Waiting for the server"
wait_until_healthy
FAIL_HINT=""

info "${GREEN}${BOLD}F3M is up to date ($(git rev-parse --short HEAD)) and running at $(site_url)${RESET}"
info "${DIM}Old images take disk space; 'docker image prune' removes the unused ones.${RESET}"
exit 0
}
