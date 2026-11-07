#!/usr/bin/env bash
# Sets up and starts the F3M server with Docker Compose.
#
# Asks for the settings (kept in .env next to compose.yaml; values already there are offered as defaults, so the
# script can be run again to change them), builds the image, starts the container and waits until it is healthy.
# The server keeps running in the background afterwards.
#
# Usage: ./setup.sh
set -euo pipefail

cd "$(dirname "$0")"

# shellcheck source=scripts/common.sh
source scripts/common.sh

[ -t 0 ] || fail "This script asks questions; run it in a terminal."
require_docker

# ── Settings ────────────────────────────────────────────────────────────────

# Values are written in single quotes, which Compose reads literally, so they can't contain one themselves.
valid_value() {
    case "$1" in
        *"'"* | *$'\n'*) warn "Single quotes and line breaks can't be used here."; return 1 ;;
    esac
}

# ask VAR "Question" [default]: a visible answer; Enter keeps the default.
ask() {
    local answer
    while true; do
        if [ -n "${3:-}" ]; then
            read -r -p "$2 [$3]: " answer
            answer=${answer:-$3}
        else
            read -r -p "$2: " answer
        fi
        valid_value "$answer" && break
    done
    printf -v "$1" '%s' "$answer"
}

# ask_secret VAR "Question" min_length [current]: hidden and typed twice. With a current value, Enter keeps it.
ask_secret() {
    local answer again
    while true; do
        if [ -n "${4:-}" ]; then
            read -r -s -p "$2 (Enter keeps the current one): " answer; echo
            if [ -z "$answer" ]; then
                printf -v "$1" '%s' "$4"
                return
            fi
        else
            read -r -s -p "$2: " answer; echo
        fi

        if [ "${#answer}" -lt "$3" ]; then
            warn "It needs at least $3 characters."
            continue
        fi
        valid_value "$answer" || continue
        read -r -s -p "Repeat it: " again; echo
        [ "$answer" = "$again" ] && break
        warn "The two entries differ. Try again."
    done
    printf -v "$1" '%s' "$answer"
}

random_password() {
    if command -v openssl >/dev/null 2>&1; then
        openssl rand -base64 33 | tr -d '/+=\n' | cut -c1-32
    else
        LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32
    fi
}

info "${BOLD}F3M server setup${RESET}"
info "Answers are saved in $ENV_FILE (readable only by you). Press Enter to keep the value in [brackets]."
if [ -f "$ENV_FILE" ]; then
    info "${DIM}$ENV_FILE exists; its values are the defaults.${RESET}"
fi

step "Catalog signing key"
info "The server signs the mod catalog with a key in its storage volume, encrypted with this password."
info "Desktop apps only trust that key: back up the password together with the volume, and never change it"
info "once the server has started, or the server can no longer open the key."
KEY_PASSWORD_CURRENT=$(env_value F3M_CATALOG_KEY_PASSWORD)
if [ -n "$KEY_PASSWORD_CURRENT" ]; then
    info "${GREEN}A password is already set; it is kept.${RESET}"
    KEY_PASSWORD=$KEY_PASSWORD_CURRENT
elif yes_no "Generate a random password?" y; then
    KEY_PASSWORD=$(random_password)
    info "Generated. Store it somewhere safe as well: ${BOLD}$KEY_PASSWORD${RESET}"
else
    ask_secret KEY_PASSWORD "Catalog key password" 12
fi

step "Admin account"
info "Created at the first start (an existing account with the name becomes admin) and made admin at every start."
info "The password is only used when the account is created; change it on the site afterwards."
ask ADMIN_USERNAME "Admin user name" "$(env_value F3M_ADMIN_USERNAME)"
while [ -z "$ADMIN_USERNAME" ]; do ask ADMIN_USERNAME "Admin user name (required)"; done
ADMIN_PASSWORD_CURRENT=$(env_value F3M_ADMIN_PASSWORD)
ask_secret ADMIN_PASSWORD "Admin password (8+ characters, with a digit and a lower-case letter)" 8 "$ADMIN_PASSWORD_CURRENT"

ADMIN_OVERWRITE=false
if [ -n "$ADMIN_PASSWORD_CURRENT" ] && [ "$ADMIN_PASSWORD" != "$ADMIN_PASSWORD_CURRENT" ]; then
    info "The admin account may exist already, with its old password."
    if yes_no "Reset its password to the new one at this start?" y; then
        ADMIN_OVERWRITE=true
    fi
fi

step "Network"
ask HTTP_PORT "Port on this machine for the site" "$(env_value F3M_HTTP_PORT || true)"
HTTP_PORT=${HTTP_PORT:-62444}
while ! [[ "$HTTP_PORT" =~ ^[0-9]+$ ]] || [ "$HTTP_PORT" -lt 1 ] || [ "$HTTP_PORT" -gt 65535 ]; do
    ask HTTP_PORT "A port number between 1 and 65535" "62444"
done

BEHIND_PROXY_DEFAULT=y
[ "$(env_value F3M_BEHIND_PROXY)" = "false" ] && BEHIND_PROXY_DEFAULT=n
info "For HTTPS, put a reverse proxy (Caddy, nginx, Traefik…) in front that forwards to port $HTTP_PORT."
if yes_no "Will the site be reached through such a proxy?" "$BEHIND_PROXY_DEFAULT"; then
    BEHIND_PROXY=true
else
    BEHIND_PROXY=false
fi

step "Account linking bot (optional)"
info "New users sign up by posting a code on their forum profile; a bot account reads it. It can also be"
info "entered later on the site, under Admin Panel → Site setup."
BOT_USERNAME=$(env_value F3M_F95BOT_USERNAME)
BOT_PASSWORD=$(env_value F3M_F95BOT_PASSWORD)
BOT_USER_ID=$(env_value F3M_F95BOT_USER_ID)
BOT_BASE_URL=$(env_value F3M_F95BOT_BASE_URL)
BOT_BASE_URL=${BOT_BASE_URL:-https://f95zone.to}
BOT_DEFAULT=n
[ -n "$BOT_USERNAME" ] && BOT_DEFAULT=y
if yes_no "Set the bot account here?" "$BOT_DEFAULT"; then
    ask BOT_USERNAME "Bot user name" "$BOT_USERNAME"
    ask_secret BOT_PASSWORD "Bot password" 1 "$BOT_PASSWORD"
    ask BOT_USER_ID "Bot user ID (the number in its profile address, optional)" "$BOT_USER_ID"
else
    BOT_USERNAME=""; BOT_PASSWORD=""; BOT_USER_ID=""
fi

# ── Write .env ──────────────────────────────────────────────────────────────

step "Saving $ENV_FILE"
if [ -f "$ENV_FILE" ]; then
    cp -p "$ENV_FILE" "$ENV_FILE.bak"
    info "The previous file is kept as $ENV_FILE.bak."
fi

umask 077
cat >"$ENV_FILE" <<EOF
# Written by setup.sh on $(date '+%Y-%m-%d %H:%M'). See .env.example for every setting. Never commit this file.
F3M_CATALOG_KEY_PASSWORD='$KEY_PASSWORD'

F3M_ADMIN_USERNAME='$ADMIN_USERNAME'
F3M_ADMIN_PASSWORD='$ADMIN_PASSWORD'
F3M_ADMIN_PASSWORD_OVERWRITE=$ADMIN_OVERWRITE

F3M_HTTP_PORT=$HTTP_PORT
F3M_BEHIND_PROXY=$BEHIND_PROXY

F3M_F95BOT_BASE_URL='$BOT_BASE_URL'
F3M_F95BOT_USERNAME='$BOT_USERNAME'
F3M_F95BOT_PASSWORD='$BOT_PASSWORD'
F3M_F95BOT_USER_ID='$BOT_USER_ID'
EOF
chmod 600 "$ENV_FILE"
give_to_folder_owner "$ENV_FILE" "$ENV_FILE.bak"
info "${GREEN}Saved.${RESET}"

# ── Build and start ─────────────────────────────────────────────────────────

step "Building and starting the container"
info "${DIM}The first build downloads the .NET images and takes a few minutes.${RESET}"
build_and_start

step "Waiting for the server"
wait_until_healthy

# A reset password should only happen once: switch it off for the next start.
if [ "$ADMIN_OVERWRITE" = true ]; then
    sed -i.tmp 's/^F3M_ADMIN_PASSWORD_OVERWRITE=true$/F3M_ADMIN_PASSWORD_OVERWRITE=false/' "$ENV_FILE" && rm -f "$ENV_FILE.tmp"
fi

if "${COMPOSE[@]}" logs 2>/dev/null | grep -q "admin account .* could not be set up"; then
    warn "The admin account could not be set up; the reason is in '${COMPOSE[*]} logs'. Fix it with ./setup.sh."
fi

URL=$(site_url)
info "${GREEN}${BOLD}F3M is running at $URL${RESET}"
info ""
info "Next:"
info "  1. Sign in as ${BOLD}$ADMIN_USERNAME${RESET} and open Admin Panel → Site setup (${URL}admin/setup)."
info "  2. Make a backup now and then: ./backup.sh (it includes the signing key and .env)."
info ""
info "${DIM}Logs: ${COMPOSE[*]} logs -f    Stop: ${COMPOSE[*]} down    Change settings: ./setup.sh    Update: ./update.sh${RESET}"
