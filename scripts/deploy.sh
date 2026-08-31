#!/usr/bin/env bash
# Build the plugin and install it into the local Jellyfin server.
#
# Replaces the hand-run sudo cp chain that used to live in DEPLOYMENT.md.
# Idempotent: safe to re-run. Every step is announced and checked.
#
#   ./scripts/deploy.sh              build, install, restart
#   ./scripts/deploy.sh --no-restart build and install only
#   ./scripts/deploy.sh --build-only build only (no sudo needed)

set -euo pipefail

readonly REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
readonly PLUGIN_NAME="Dialogue Boost"
readonly ASSEMBLY="Jellyfin.Plugin.DialogueBoost.dll"
readonly CONFIG="Release"
readonly TFM="net9.0"

RESTART=1
INSTALL=1
for arg in "$@"; do
    case "$arg" in
        --no-restart) RESTART=0 ;;
        --build-only) RESTART=0; INSTALL=0 ;;
        -h|--help)    sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \?//'; exit 0 ;;
        *)            echo "unknown option: $arg" >&2; exit 2 ;;
    esac
done

say() { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
die() { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

# Version comes from build.yaml so the install path always matches what Jellyfin expects.
version="$(sed -n 's/^version:[[:space:]]*"\?\([0-9.]*\)"\?.*/\1/p' "$REPO_ROOT/build.yaml" | head -1)"
[[ -n "$version" ]] || die "could not read version from build.yaml"

readonly TARGET_DIR="/var/lib/jellyfin/plugins/${PLUGIN_NAME}_${version}"
readonly BUILD_OUT="$REPO_ROOT/bin/$CONFIG/$TFM/$ASSEMBLY"

say "Building $CONFIG (v$version)"
dotnet build "$REPO_ROOT/Jellyfin.Plugin.DialogueBoost.csproj" -c "$CONFIG" --nologo \
    || die "build failed"
[[ -f "$BUILD_OUT" ]] || die "expected build output missing: $BUILD_OUT"

if (( ! INSTALL )); then
    say "Build only — done. Artifact: $BUILD_OUT"
    exit 0
fi

command -v systemctl >/dev/null || die "systemctl not found — this script targets a systemd host"

say "Installing to $TARGET_DIR"
sudo install -d -o jellyfin -g jellyfin -m 0755 "$TARGET_DIR"
sudo install -o jellyfin -g jellyfin -m 0644 "$BUILD_OUT"        "$TARGET_DIR/$ASSEMBLY"
sudo install -o jellyfin -g jellyfin -m 0644 "$REPO_ROOT/meta.json" "$TARGET_DIR/meta.json"

if (( RESTART )); then
    say "Restarting jellyfin.service"
    sudo systemctl restart jellyfin
    # Wait for the HTTP endpoint rather than sleeping a guessed interval. The body has to be
    # checked, not just the status: while Jellyfin is still initialising it answers every request
    # with a "Server still starting" HTML page — at 200 — so a status-only probe reports "up" and
    # the next API call gets HTML instead of JSON.
    for _ in $(seq 1 60); do
        if curl -fsS --max-time 2 http://localhost:8096/System/Info/Public 2>/dev/null | grep -q '"Version"'; then
            say "Jellyfin is up."
            break
        fi
        sleep 1
    done
fi

say "Deployed v$version."
echo
echo "  config.html is an embedded resource — hard-refresh the browser"
echo "  (Ctrl+Shift+R) or the old UI stays cached."
