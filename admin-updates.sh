#!/usr/bin/env bash
# Lets admins update the F3M server from the admin page, or stops that again.
#
# Installs a small systemd service on this host. The admin page only leaves a request in the server's storage volume;
# the service sees it and runs ./update.sh --yes here (backup, newest code, rebuild, restart), then reports the result
# back to the page. It also looks for new commits every 6 hours, so the page can say when an update is waiting. The
# web app itself gets no access to Docker or to this host.
#
# Usage:
#   ./admin-updates.sh             Asks what to do: install, show the status, or remove
#   ./admin-updates.sh install     Installs (or reinstalls) the service
#   ./admin-updates.sh status      Shows whether it is installed and what it did last
#   ./admin-updates.sh remove      Removes the service again
#
# Installing and removing need root for systemd; the script asks for sudo when needed.
set -euo pipefail

cd "$(dirname "$0")"

# shellcheck source=scripts/common.sh
source scripts/common.sh

UNIT="f3m-admin-update"
UNIT_DIR="/etc/systemd/system"
RUNNER="/usr/local/libexec/f3m-admin-update"
EXCHANGE_IN_CONTAINER="/app/Storage/Updates"

ACTION=""
case "${1:-}" in
    install | status | remove) ACTION=$1 ;;
    -h | --help) sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
    "") ;;
    *) fail "Unknown option: $1 (see --help)" ;;
esac

if [ -z "$ACTION" ] && [ ! -t 0 ]; then
    fail "This script asks questions; run it in a terminal, or name what to do (install, status or remove)."
fi

# as_root COMMAND...: runs a command as root (systemd, the runner, the volume's folder); with sudo when needed.
as_root() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    else
        command -v sudo >/dev/null 2>&1 || fail "This needs root. Run the script as root, or install sudo."
        sudo "$@"
    fi
}

command -v systemctl >/dev/null 2>&1 || fail "systemd was not found. Updating from the admin page needs a host with systemd."

installed() { [ -f "$UNIT_DIR/$UNIT.service" ]; }

# The storage volume's folder on this host, from the server's container (running or not).
exchange_on_host() {
    local id source
    id=$("${COMPOSE[@]}" ps -aq "$SERVICE" 2>/dev/null | head -n 1)
    [ -n "$id" ] || return 1
    source=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/app/Storage"}}{{.Source}}{{end}}{{end}}' "$id" 2>/dev/null)
    [ -n "$source" ] || return 1
    printf '%s/Updates' "$source"
}

# ── Status ──────────────────────────────────────────────────────────────────

show_status() {
    step "Admin page updates"
    if ! installed; then
        info "Not installed. The admin page can show the version, but not update the server."
        return 0
    fi

    info "${GREEN}Installed.${RESET} The admin page can update the server."
    info "  Watcher:   $(systemctl is-active "$UNIT.path" 2>/dev/null || true) ($UNIT.path)"
    info "  Checks:    $(systemctl is-active "$UNIT.timer" 2>/dev/null || true) ($UNIT.timer, every 6 hours)"

    local exchange
    if exchange=$(exchange_on_host) && as_root test -f "$exchange/status.txt"; then
        info "  Last update:"
        as_root cat "$exchange/status.txt" | sed 's/^/    /'
    else
        info "  Last update: none yet"
    fi
    info "${DIM}The service's own messages: journalctl -u $UNIT.service${RESET}"
}

# ── Install ─────────────────────────────────────────────────────────────────

install_service() {
    step "Installing admin page updates"
    [ -f "$ENV_FILE" ] || fail "No .env yet. Set the server up with ./setup.sh first."
    git rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "This folder is not a git checkout, so it can't be updated with git."

    local exchange repo owner
    exchange=$(exchange_on_host) || fail "The server's container wasn't found. Start the server first (./setup.sh)."
    repo=$(pwd -P)
    owner=$(stat -c %U "$repo")
    [ "$owner" != "UNKNOWN" ] || fail "The owner of $repo isn't a user on this host."

    info "The service will:"
    info "  - watch $exchange for requests from the admin page,"
    info "  - run ./update.sh --yes in $repo as the user $owner (git and Docker run as $owner, not as root),"
    info "  - look for new commits every 6 hours."
    if ! docker_works_for "$owner"; then
        warn "$owner can't use Docker here; update.sh would fail. Add $owner to the docker group first."
    fi
    if [ -t 0 ] && ! yes_no "Install it?" y; then
        info "Nothing changed."
        return 0
    fi

    # The app creates the folder at startup; an older server version may not have yet.
    as_root test -d "$exchange" || fail "$exchange doesn't exist yet. Update the server once (./update.sh), so it creates it."

    # Root runs its own copy of the runner: a script in the F3M folder could be changed by $owner.
    as_root install -D -m 755 -o root -g root scripts/admin-update-runner.sh "$RUNNER"

    local quoted_repo quoted_exchange
    quoted_repo=$(systemd_quote "$repo")
    quoted_exchange=$(systemd_quote "$exchange")

    as_root tee "$UNIT_DIR/$UNIT.service" >/dev/null <<EOF
# Installed by $repo/admin-updates.sh. Remove it with: ./admin-updates.sh remove
[Unit]
Description=F3M: updates requested on the admin page
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RuntimeDirectory=$UNIT
ExecStart=$RUNNER $quoted_repo $quoted_exchange $owner
# A rebuild downloads and compiles; give it time.
TimeoutStartSec=45min
EOF

    as_root tee "$UNIT_DIR/$UNIT.path" >/dev/null <<EOF
# Installed by $repo/admin-updates.sh. Remove it with: ./admin-updates.sh remove
[Unit]
Description=F3M: watch for update requests from the admin page

[Path]
PathExists=$exchange/update-requested
PathExists=$exchange/check-requested
Unit=$UNIT.service

[Install]
WantedBy=multi-user.target
EOF

    as_root tee "$UNIT_DIR/$UNIT.timer" >/dev/null <<EOF
# Installed by $repo/admin-updates.sh. Remove it with: ./admin-updates.sh remove
[Unit]
Description=F3M: look for new commits for the admin page

[Timer]
OnBootSec=5min
OnUnitActiveSec=6h
Unit=$UNIT.service

[Install]
WantedBy=timers.target
EOF

    as_root systemctl daemon-reload
    as_root systemctl enable --now "$UNIT.path" "$UNIT.timer" >/dev/null

    step "Looking for updates once"
    if as_root systemctl start "$UNIT.service"; then
        info "${GREEN}${BOLD}Done.${RESET} The admin page now shows whether an update is waiting, and can start it."
    else
        warn "The first check failed; 'journalctl -u $UNIT.service' says why. The page will show the error too."
    fi
}

# docker_works_for USER: whether USER may use Docker (root, or in the docker group).
docker_works_for() {
    [ "$1" = "root" ] && return 0
    id -nG "$1" 2>/dev/null | tr ' ' '\n' | grep -qx docker
}

# Quotes a path for an ExecStart line (systemd's quoting: double quotes, with \ and " escaped).
systemd_quote() {
    local value=${1//\\/\\\\}
    value=${value//\"/\\\"}
    printf '"%s"' "$value"
}

# ── Remove ──────────────────────────────────────────────────────────────────

remove_service() {
    step "Removing admin page updates"
    if ! installed && [ ! -f "$RUNNER" ]; then
        info "Not installed; nothing to remove."
        return 0
    fi
    if [ -t 0 ] && ! yes_no "Remove it? The admin page then can't update the server any more." y; then
        info "Nothing changed."
        return 0
    fi

    as_root systemctl disable --now "$UNIT.path" "$UNIT.timer" >/dev/null 2>&1 || true
    as_root systemctl stop "$UNIT.service" >/dev/null 2>&1 || true
    as_root rm -f "$UNIT_DIR/$UNIT.service" "$UNIT_DIR/$UNIT.path" "$UNIT_DIR/$UNIT.timer" "$RUNNER"
    as_root systemctl daemon-reload
    as_root systemctl reset-failed "$UNIT.service" >/dev/null 2>&1 || true

    # What the service left for the page; without it, the page says the service isn't installed.
    local exchange
    if exchange=$(exchange_on_host); then
        as_root rm -f "$exchange/check.txt" "$exchange/status.txt" "$exchange/update.log" \
            "$exchange/update-requested" "$exchange/check-requested"
    fi

    info "${GREEN}${BOLD}Removed.${RESET} Update the server with ./update.sh as before."
}

# ── Main ────────────────────────────────────────────────────────────────────

require_docker

if [ -z "$ACTION" ]; then
    show_status
    printf '\n'
    if installed; then
        info "1) Reinstall (after moving the F3M folder, or to pick up a newer runner)"
        info "2) Remove"
        info "3) Quit"
        while true; do
            read -r -p "Choose [3]: " choice
            case "${choice:-3}" in
                1) ACTION=install; break ;;
                2) ACTION=remove; break ;;
                3) exit 0 ;;
            esac
        done
    else
        info "1) Install"
        info "2) Quit"
        while true; do
            read -r -p "Choose [1]: " choice
            case "${choice:-1}" in
                1) ACTION=install; break ;;
                2) exit 0 ;;
            esac
        done
    fi
fi

case "$ACTION" in
    install) install_service ;;
    remove) remove_service ;;
    status) show_status ;;
esac
