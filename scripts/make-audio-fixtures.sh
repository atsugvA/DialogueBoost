#!/usr/bin/env bash
# Build a synthetic Jellyfin library for testing external-audio (sidecar) behaviour.
#
# Each fixture is one movie folder holding a 15-second video with two embedded
# audio tracks (eng 5.1 + ger stereo), plus sidecars that differ in exactly one
# property. Point a Jellyfin *movies* library at the target directory, then run
# scripts/probe-audio-selection.py to see how the server treats each one.
#
# This is the harness behind the default-track findings. Re-run it whenever a Jellyfin
# upgrade could have changed external-audio handling.
#
#   ./scripts/make-audio-fixtures.sh [target-dir]   # default: /srv/dbm1
#   SERVICE_USER=jellyfin ./scripts/make-audio-fixtures.sh   # who ends up owning the rig
#
# Requires ffmpeg on PATH. Writes nothing outside target-dir.

set -euo pipefail

readonly ROOT="${1:-/srv/dbm1}"
readonly SERVICE_USER="${SERVICE_USER:-jellyfin}"
readonly MARKER="Dialogue Boost"
FF=(ffmpeg -hide_banner -loglevel error -y)

# A source video with two embedded tracks: 5.1 (default) + stereo.
# $2/$3 override their languages, which default to eng and ger.
make_movie() {
    local dir="$ROOT/$1" video="$ROOT/$1/$1.mkv"
    local first="${2:-eng}" second="${3:-ger}"
    mkdir -p "$dir"
    "${FF[@]}" \
        -f lavfi -i testsrc2=size=320x240:rate=15:duration=15 \
        -f lavfi -i sine=frequency=440:duration=15 \
        -f lavfi -i sine=frequency=660:duration=15 \
        -map 0:v -c:v libx264 -preset ultrafast -pix_fmt yuv420p \
        -map 1:a -c:a:0 ac3 -ac:a:0 6 -b:a:0 384k \
        -map 2:a -c:a:1 ac3 -ac:a:1 2 -b:a:1 192k \
        -metadata:s:a:0 "language=$first" -metadata:s:a:0 "title=Original ${first^^}" \
        -metadata:s:a:1 "language=$second" -metadata:s:a:1 "title=Original ${second^^}" \
        -disposition:a:0 default -disposition:a:1 0 \
        "$video"
    printf '%s' "$video"
}

# One processed stream. $2 = "default" | "0" container disposition, $3 = file suffix.
one_stream() {
    "${FF[@]}" -i "$1" -map 0:a:0 -filter:a "volume=2" -c:a aac -b:a 256k \
        -metadata:s:a:0 title="$MARKER" -metadata:s:a:0 language=eng \
        -disposition:a:0 "$2" -f matroska "${1%.mkv}.$3.mka"
}

# Two processed streams (eng + ger). $2/$3 = dispositions, $4 = file suffix.
two_processed() {
    "${FF[@]}" -i "$1" -map 0:a:0 -map 0:a:1 \
        -filter:a:0 "volume=2" -c:a:0 aac -b:a:0 256k \
        -metadata:s:a:0 title="$MARKER" -metadata:s:a:0 language=eng -disposition:a:0 "$2" \
        -filter:a:1 "volume=2" -c:a:1 aac -b:a:1 256k \
        -metadata:s:a:1 title="$MARKER" -metadata:s:a:1 language=ger -disposition:a:1 "$3" \
        -f matroska "${1%.mkv}.$4.mka"
}

echo "Building audio fixtures in $ROOT"
rm -rf "$ROOT"; mkdir -p "$ROOT"

# no sidecar — the baseline every other row is compared against
make_movie "DB M1 Control (2020)" >/dev/null

# one stream, container disposition set / cleared: is the disposition read at all?
V=$(make_movie "DB M1 One Stream Default (2020)");    one_stream "$V" default "$MARKER"
V=$(make_movie "DB M1 One Stream Nodefault (2020)");  one_stream "$V" 0       "$MARKER"

# what the plugin writes today: every original copied in, plus the processed track
V=$(make_movie "DB M1 Two Stream Plugin (2020)")
"${FF[@]}" -i "$V" -map 0:a:0 -map 0:a:1 -map 0:a:0 \
    -c:a:0 copy -metadata:s:a:0 title="Original" -metadata:s:a:0 language=eng -disposition:a:0 0 \
    -c:a:1 copy -metadata:s:a:1 title="Original" -metadata:s:a:1 language=ger -disposition:a:1 0 \
    -filter:a:2 "volume=2" -c:a:2 aac -b:a:2 256k \
    -metadata:s:a:2 title="$MARKER" -metadata:s:a:2 language=eng -disposition:a:2 default \
    -f matroska "${V%.mkv}.$MARKER.mka"

# processed tracks only — no lossless copies
V=$(make_movie "DB M1 Processed Only (2020)");  two_processed "$V" default 0 "$MARKER"
# which stream the disposition is read from, and what happens when none claims it
V=$(make_movie "DB M1 None Default (2020)");    two_processed "$V" 0 0       "$MARKER"
V=$(make_movie "DB M1 Second Default (2020)");  two_processed "$V" 0 default "$MARKER"

# a cheap second stream instead of copying an original: full-length and 1-second silence
for name in "Filler Full" "Filler One Second"; do
    V=$(make_movie "DB M1 $name (2020)")
    # -t is an input option (how much silence to read); -shortest is an *output* option (stop when
    # the video does). ffmpeg 9 rejects -shortest in input position; older builds only warned.
    if [ "$name" = "Filler Full" ]; then IN_LEN=(); OUT_LEN=(-shortest); else IN_LEN=(-t 1); OUT_LEN=(); fi
    "${FF[@]}" -i "$V" -f lavfi "${IN_LEN[@]}" -i anullsrc=r=48000:cl=mono \
        -map 0:a:0 -filter:a:0 "volume=2" -c:a:0 aac -b:a:0 256k \
        -metadata:s:a:0 title="$MARKER" -metadata:s:a:0 language=eng -disposition:a:0 default \
        -map 1:a:0 -c:a:1 aac -b:a:1 16k \
        -metadata:s:a:1 title="Filler" -metadata:s:a:1 language=und -disposition:a:1 0 \
        "${OUT_LEN[@]}" -f matroska "${V%.mkv}.$MARKER.mka"
done

# Jellyfin's ".default" filename flag, on one stream and on two
V=$(make_movie "DB M1 Name Flag Default (2020)");  one_stream    "$V" default "$MARKER.default"
V=$(make_movie "DB M1 Name Flag Lang (2020)");     one_stream    "$V" default "eng.default"
V=$(make_movie "DB M1 Name Flag Multi (2020)");    two_processed "$V" default 0 "$MARKER.default"

# two profiles side by side, each claiming default
V=$(make_movie "DB M1 Two Profiles (2020)")
two_processed "$V" default 0 "$MARKER"
"${FF[@]}" -i "$V" -map 0:a:0 -map 0:a:1 \
    -filter:a:0 "dynaudnorm=m=10" -c:a:0 aac -b:a:0 256k \
    -metadata:s:a:0 title="Night Mode" -metadata:s:a:0 language=eng -disposition:a:0 default \
    -filter:a:1 "dynaudnorm=m=10" -c:a:1 aac -b:a:1 256k \
    -metadata:s:a:1 title="Night Mode" -metadata:s:a:1 language=ger -disposition:a:1 0 \
    -f matroska "${V%.mkv}.Night Mode.mka"

# A name that breaks a flat command string: a double quote inside it ends the argument early,
# which is how the argument-quoting bug was caught — ffmpeg reported "No such file or directory"
# for a path with the
# quotes stripped out. No sidecar here; the point is to run the plugin against it and watch it
# write one. Tagged deu so the default German track rules accept it, and 5.1 so the Dialogue Boost
# centre-channel branch is the one that runs.
make_movie 'DB M5 Hostile "Quote" (2020)' deu eng >/dev/null

# The rig exists to be written into: the plugin's whole job is putting a sidecar next to the source,
# and a root-owned tree silently makes every run fail the storage preflight. Advice in an echo was
# not enough — the fixtures built here had never been writable by the server.
if id -u "$SERVICE_USER" >/dev/null 2>&1; then
    chown -R "$SERVICE_USER":"$SERVICE_USER" "$ROOT"
    chmod -R u=rwX,go=rX "$ROOT"
    echo "Owned by $SERVICE_USER, so the plugin can write sidecars here."
else
    echo "WARNING: user '$SERVICE_USER' does not exist — the fixtures stay root-owned and the plugin"
    echo "         will refuse to write into them. Pass SERVICE_USER=<account> to fix that."
fi

echo "Built $(find "$ROOT" -mindepth 1 -maxdepth 1 -type d | wc -l) fixtures ($(du -sh "$ROOT" | cut -f1))"
