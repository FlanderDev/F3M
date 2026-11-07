#!/usr/bin/env bash
# Writes the GitHub release notes (markdown) to stdout. Run by release.yml before the new tag exists.
# Needs the full history with tags (checkout fetch-depth: 0) and GH_TOKEN for the gh CLI.
#
# Usage: release-notes.sh <name> <desktop version> <pipeline duration>
set -euo pipefail

NAME="$1"
DESKTOP="$2"
DURATION="$3"
REPO="${GITHUB_REPOSITORY:?}"
SERVER="${GITHUB_SERVER_URL:-https://github.com}"
SHA="$(git rev-parse HEAD)"

# From HEAD^, so a re-run on an already tagged commit still compares with the release before.
PREV="$(git describe --tags --abbrev=0 --match 'release/*' HEAD^ 2>/dev/null || true)"
RANGE="${PREV:+$PREV..}HEAD"

# PRs merged since the last release. The develop → release PRs are just the release itself, so they're skipped.
features="" fixes="" docs="" other=""
authors=()
for n in $(git log "$RANGE" --merges --format='%s' | sed -n 's/^Merge pull request #\([0-9]*\).*/\1/p' | sort -n); do
    pr="$(gh pr view "$n" --repo "$REPO" --json title,author,labels,baseRefName \
        -q '[.baseRefName, .author.login, (.labels | map(.name) | join(",")), .title] | join("\t")' 2>/dev/null)" || continue
    IFS=$'\t' read -r base author labels title <<<"$pr"
    [ "$base" = "release" ] && continue

    line="- ${title} (#${n}) by @${author}"$'\n'
    case ",$labels," in
        *,enhancement,*) features+="$line" ;;
        *,bug,*) fixes+="$line" ;;
        *,documentation,*) docs+="$line" ;;
        *) other+="$line" ;;
    esac
    authors+=("$author")
done

section() { [ -n "$2" ] && printf '### %s\n\n%s\n' "$1" "$2"; return 0; }

# New EF migrations: the server runs them on start, so the operator should back up first.
migrations=""
if [ -n "$PREV" ]; then
    migrations="$(git diff --name-only --diff-filter=A "$PREV" HEAD -- src/F3M.Server/Migrations \
        | grep -v -e '\.Designer\.cs$' -e 'ModelSnapshot\.cs$' \
        | sed -n 's#.*/[0-9]*_\(.*\)\.cs$#- `\1`#p' || true)"
fi

{
    if [ -n "$PREV" ]; then
        echo "Changes since [${PREV#release/}](${SERVER}/${REPO}/releases/tag/${PREV})."
    else
        echo "First release."
    fi
    echo

    if [ -n "$features$fixes$docs$other" ]; then
        section "✨ New" "$features"
        section "🐛 Fixes" "$fixes"
        section "📝 Docs" "$docs"
        section "🔧 Other" "$other"
    else
        echo "_No merged pull requests, see the commits below._"
        echo
    fi

    if [ -n "$migrations" ]; then
        echo "### ⚠️ Database changes"
        echo
        echo "The server updates the database when it starts, and there's no way back without a backup."
        echo "Run \`./backup.sh\` first (\`./update.sh\` offers it)."
        echo
        echo "$migrations"
        echo
    fi

    echo "### 📦 Downloads"
    echo
    echo "| File | What it is |"
    echo "|---|---|"
    echo "| \`F3M.Desktop-win-Setup.exe\` | Desktop app ${DESKTOP} for Windows, installer. Updates itself. |"
    echo "| \`F3M.Desktop-win-Portable.zip\` | Desktop app ${DESKTOP} for Windows, no install. |"
    echo "| \`F3M.Desktop-${DESKTOP}-x86_64.AppImage\` | Desktop app ${DESKTOP} for Linux. |"
    echo "| \`F3M-linux-x64-selfcontained-${NAME}.zip\` | Server for Linux, .NET included. |"
    echo "| \`F3M-windows-x64-selfcontained-${NAME}.zip\` | Server for Windows, .NET included. |"
    echo "| \`F3M-linux-${NAME}.zip\`, \`F3M-windows-${NAME}.zip\` | Server, needs the ASP.NET Core 10 runtime. |"
    echo
    echo "Running it with Docker? Just \`./update.sh\` on the host, no download needed."
    echo
    echo "The \`.nupkg\`, \`RELEASES\` and \`*.json\` files are for the desktop app's updater."
    echo

    if [ "${#authors[@]}" -gt 0 ]; then
        echo "### 🙌 Contributors"
        echo
        printf '%s\n' "${authors[@]}" | sort -fu | sed 's/^/@/' | paste -sd ' ' | sed 's/ /, /g'
        echo
    fi

    echo "---"
    echo
    if [ -n "$PREV" ]; then
        echo "**Full changelog:** [${PREV#release/}...${NAME}](${SERVER}/${REPO}/compare/${PREV}...${SHA})"
        echo "**Commits:** $(git rev-list --count --no-merges "$RANGE")"
    fi
    echo "**Commit:** [\`${SHA:0:7}\`](${SERVER}/${REPO}/commit/${SHA})"
    echo "**Built at:** ${NAME} UTC in ${DURATION}"
}
