# Shared by setup.sh, update.sh, backup.sh and admin-updates.sh in the project root. Not meant to be run on its own.
# shellcheck shell=bash

ENV_FILE=".env"
SERVICE="f3m-server"
START_TIMEOUT=180 # seconds to wait for the site to become healthy

# ── Output ──────────────────────────────────────────────────────────────────

if [ -t 1 ]; then
    BOLD=$'\e[1m'; DIM=$'\e[2m'; RED=$'\e[31m'; GREEN=$'\e[32m'; YELLOW=$'\e[33m'; RESET=$'\e[0m'
else
    BOLD=""; DIM=""; RED=""; GREEN=""; YELLOW=""; RESET=""
fi

info() { printf '%s\n' "$*"; }
step() { printf '\n%s== %s ==%s\n' "$BOLD" "$*" "$RESET"; }
warn() { printf '%sWarning:%s %s\n' "$YELLOW" "$RESET" "$*" >&2; }
# FAIL_HINT, when set, is printed after the error: what to do now.
fail() {
    printf '%sError:%s %s\n' "$RED" "$RESET" "$*" >&2
    [ -z "${FAIL_HINT:-}" ] || printf '%s\n' "$FAIL_HINT" >&2
    exit 1
}

# yes_no "Question" y|n: true for yes.
yes_no() {
    local answer hint="y/N"
    [ "$2" = "y" ] && hint="Y/n"
    while true; do
        read -r -p "$1 [$hint]: " answer
        answer=$(printf '%s' "${answer:-$2}" | tr '[:upper:]' '[:lower:]')
        case "$answer" in
            y | yes) return 0 ;;
            n | no) return 1 ;;
        esac
    done
}

# ── Docker ──────────────────────────────────────────────────────────────────

# Checks Docker and Compose, and sets COMPOSE to the command that runs Compose.
require_docker() {
    [ -f compose.yaml ] || fail "compose.yaml not found. Run the script from the F3M folder."
    command -v docker >/dev/null 2>&1 || fail "Docker is not installed: https://docs.docker.com/engine/install/"

    if docker compose version >/dev/null 2>&1; then
        COMPOSE=(docker compose)
    elif command -v docker-compose >/dev/null 2>&1; then
        COMPOSE=(docker-compose)
    else
        fail "Docker Compose is not installed: https://docs.docker.com/compose/install/"
    fi

    docker info >/dev/null 2>&1 || fail "Docker is not running, or this user may not use it (try sudo, or add the user to the docker group)."
}

# Builds the image from the checked-out code and (re)starts the container. The commit is baked into the image, so the
# admin page can show which version runs.
build_and_start() {
    F3M_SOURCE_COMMIT=$(git rev-parse HEAD 2>/dev/null || true)
    export F3M_SOURCE_COMMIT
    "${COMPOSE[@]}" up --build -d
}

# The server's container id; empty when it does not exist.
container_id() {
    "${COMPOSE[@]}" ps -q "$SERVICE" 2>/dev/null | head -n 1
}

container_running() {
    local id
    id=$(container_id)
    [ -n "$id" ] && [ "$(docker inspect -f '{{.State.Running}}' "$id" 2>/dev/null)" = "true" ]
}

# healthy, unhealthy or starting (Docker's HEALTHCHECK); empty for an image built before it had one.
container_health() {
    local id
    id=$(container_id)
    [ -n "$id" ] || return 0
    docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id" 2>/dev/null || true
}

# HTTP status of a URL on the host; empty when nothing answers.
http_status() {
    if command -v curl >/dev/null 2>&1; then
        curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$1" || true
    elif command -v wget >/dev/null 2>&1; then
        wget -q -S -O /dev/null -T 5 "$1" 2>&1 | awk '/^  HTTP\//{code=$2} END{print code}' || true
    fi
}

# Waits until the server is healthy: Docker's health check, or else /healthz on the published port. Shows the last
# log lines and fails when the container stops or the time runs out.
wait_until_healthy() {
    local port url deadline health code
    port=$(env_value F3M_HTTP_PORT)
    url="http://localhost:${port:-62444}/healthz"
    deadline=$((SECONDS + START_TIMEOUT))

    while [ "$SECONDS" -lt "$deadline" ]; do
        if ! container_running; then
            printf '\n'
            "${COMPOSE[@]}" logs --tail 40 "$SERVICE" >&2 || true
            fail "The container stopped. Its last log lines are above."
        fi

        health=$(container_health)
        if [ "$health" = "healthy" ]; then
            printf '\n'
            return 0
        fi
        if [ -z "$health" ] || [ "$health" = "unhealthy" ]; then
            # No HEALTHCHECK in the image, or Docker gave up early: ask the site itself.
            code=$(http_status "$url")
            if [ "$code" = "200" ]; then
                printf '\n'
                return 0
            fi
        fi

        printf '.'
        sleep 2
    done

    printf '\n'
    "${COMPOSE[@]}" logs --tail 40 "$SERVICE" >&2 || true
    fail "The server did not become healthy within $START_TIMEOUT seconds. The log lines above should say why; '${COMPOSE[*]} logs -f' shows more."
}

# ── .env ────────────────────────────────────────────────────────────────────

# Reads KEY from .env without running it as a script. Surrounding quotes are removed.
env_value() {
    [ -f "$ENV_FILE" ] || return 0
    local line value
    line=$(grep -E "^$1=" "$ENV_FILE" | tail -n 1) || return 0
    value=${line#*=}
    case "$value" in
        \'*\') value=${value:1:${#value}-2} ;;
        \"*\") value=${value:1:${#value}-2} ;;
    esac
    printf '%s' "$value"
}

site_url() {
    local port
    port=$(env_value F3M_HTTP_PORT)
    printf 'http://localhost:%s/' "${port:-62444}"
}
