#!/usr/bin/env bash
# Build a Jellyfin library of source files in the codecs and channel layouts this plugin had never
# been run against.
#
# The rig it complements, scripts/make-audio-fixtures.sh, varies how a *sidecar* is built and holds
# every source at ac3 5.1 + ac3 stereo. This one varies the *source*: thirty movies, each a
# 20-second video with audio the plugin has to make a different decision about — which branch, which
# encoder, how many channels come out, whether the source's own bitrate is a number worth following,
# and whether the layout name that reaches the plugin is the one being decoded.
#
# Every audio track is a bed of tones, one per channel, each 3.74 dB below the last, so what the
# plugin writes beside it can be read rather than listened to: a channel that was dropped, silenced,
# duplicated or swapped is visible in one astats pass. Point a Jellyfin *movies* library at the
# target directory, run the plugin over it, and compare.
#
#   sudo ./scripts/make-format-fixtures.sh [target-dir]    # default: /srv/dbm2
#   SERVICE_USER=jellyfin sudo ./scripts/make-format-fixtures.sh
#
# Requires jellyfin-ffmpeg (or ffmpeg on PATH). Writes nothing outside target-dir.

set -euo pipefail

readonly ROOT="${1:-/srv/dbm2}"
readonly SERVICE_USER="${SERVICE_USER:-jellyfin}"
readonly SECONDS_EACH=20
# shellcheck source=scripts/lib-tone-bed.sh
. "$(dirname "$0")/lib-tone-bed.sh"

# name | track[,track…] where a track is codec:layout:rate:language. An empty rate means the
# encoder decides, which is what every lossless one does.
#
# Tagged deu because that is what the profile on this rig is set to process, exactly as the M5
# fixture in make-audio-fixtures.sh is — a fixture nothing will pick up teaches nothing. The two
# exceptions are the point of their own rows: 16 carries the bibliographic spelling of the same
# language, and 19 carries one the profile is not asked for.
#
# What each row is here to make the plugin decide:
#   01-02  the two rates this library actually carries, at the layout it actually has
#   03-04  a front centre below 5.1 — lifted in place since these layouts joined the table
#   05     four channels and no centre at all: the enhancer, not the centre lift
#   06-09  above eac3's ceiling, lossless and lossy, at seven channels and at eight
#   10-12  codecs whose rate says nothing about the next encode: dts, truehd, pcm
#   13-15  the lossy codecs a download can arrive in that this plugin had never seen
#   16     a bibliographic language tag, which Jellyfin and ffprobe spell differently
#   17     two languages in one file, and the wanted one is the eight-channel track:
#          the profile has to pick by language and then take that track's own branch
#   19     a file in a language the profile is not set to process: a skip, not a failure
#   20-30  the layouts Jellyfin reports under a shorter name than they have. It drops the
#          parenthesised variant, so 6.1(front) — which has no front centre and keeps its LFE where
#          6.1 keeps FC — arrives as "6.1". Four of these rows have no centre at all and have to
#          reach the stereo enhancer rather than have a surround channel lifted 4 dB
#   18     a height layout aac refuses, where eac3's fold is the fallback — and only wavpack
#          could author it: FLAC stops at eight channels and PCM in matroska loses the layout
readonly FIXTURES=(
    "DB F01 AC3 5.1 (2020)|ac3:5.1:384k:deu"
    "DB F02 EAC3 5.1 (2020)|eac3:5.1:640k:deu"
    "DB F03 AC3 5.0 (2020)|ac3:5.0:384k:deu"
    "DB F04 AC3 3.0 (2020)|ac3:3.0:256k:deu"
    "DB F05 AC3 Quad (2020)|ac3:quad:384k:deu"
    "DB F06 FLAC 7.1 (2020)|flac:7.1::deu"
    "DB F07 AAC 7.1 (2020)|aac:7.1:512k:deu"
    "DB F08 FLAC 6.1 (2020)|flac:6.1::deu"
    "DB F09 AAC 6.1 (2020)|aac:6.1:448k:deu"
    "DB F10 DTS 5.1 (2020)|dca:5.1(side):1411k:deu"
    "DB F11 TrueHD 5.1 (2020)|truehd:5.1(side)::deu"
    "DB F12 PCM 5.1 (2020)|pcm_s16le:5.1::deu"
    "DB F13 Opus Stereo (2020)|libopus:stereo:128k:deu"
    "DB F14 MP3 Mono (2020)|libmp3lame:mono:128k:deu"
    "DB F15 Vorbis 5.1 (2020)|libvorbis:5.1:448k:deu"
    "DB F16 Bibliographic Tag (2020)|ac3:5.1:384k:ger"
    "DB F17 Mixed Languages (2020)|ac3:5.1:384k:eng,flac:7.1::ger"
    "DB F18 WavPack 5.1.4 (2020)|wavpack:5.1.4::deu"
    "DB F19 Unwanted Language (2020)|ac3:5.1:384k:eng"

    # 20-30: one per layout that Jellyfin reports under a shorter name than it has. wavpack because
    # it is the only encoder here that keeps every one of these layouts intact in matroska.
    "DB F20 Variant 5.1 side (2020)|wavpack:5.1(side)::deu"
    "DB F21 Variant 5.0 side (2020)|wavpack:5.0(side)::deu"
    "DB F22 Variant quad side (2020)|wavpack:quad(side)::deu"
    "DB F23 Variant 3.0 back (2020)|wavpack:3.0(back)::deu"
    "DB F24 Variant 6.0 front (2020)|wavpack:6.0(front)::deu"
    "DB F25 Variant 6.1 back (2020)|wavpack:6.1(back)::deu"
    "DB F26 Variant 6.1 front (2020)|wavpack:6.1(front)::deu"
    "DB F27 Variant 7.0 front (2020)|wavpack:7.0(front)::deu"
    "DB F28 Variant 7.1 wide (2020)|wavpack:7.1(wide)::deu"
    "DB F29 Variant 7.1 wide side (2020)|wavpack:7.1(wide-side)::deu"
    "DB F30 Hexagonal (2020)|wavpack:hexagonal::deu"
)

echo "Building format fixtures in $ROOT"
rm -rf "$ROOT"; mkdir -p "$ROOT"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT

"$FF" -hide_banner -loglevel error -y \
    -f lavfi -i "testsrc2=size=320x240:rate=15:duration=$SECONDS_EACH" \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p "$work/video.mkv"

for row in "${FIXTURES[@]}"; do
    name="${row%%|*}"; tracks="${row#*|}"
    mkdir -p "$ROOT/$name"

    args=( -i "$work/video.mkv" ); maps=( -map 0:v -c:v copy ); i=0
    IFS=',' read -ra defs <<< "$tracks"
    for def in "${defs[@]}"; do
        IFS=':' read -r codec layout rate lang <<< "$def"
        tone_bed "$layout" "$work/bed$i.wav" "$SECONDS_EACH"
        args+=( -i "$work/bed$i.wav" )
        maps+=( -map "$((i + 1)):a" "-c:a:$i" "$codec" )
        # dca and truehd are marked experimental in this build and refuse to run without it.
        case "$codec" in dca|truehd) maps+=( -strict -2 );; esac
        [ -n "$rate" ] && maps+=( "-b:a:$i" "$rate" )
        maps+=( "-metadata:s:a:$i" "language=$lang"
                "-metadata:s:a:$i" "title=${layout} ${codec}" )
        maps+=( "-disposition:a:$i" "$([ "$i" = 0 ] && echo default || echo 0)" )
        i=$((i + 1))
    done

    "$FF" -hide_banner -loglevel error -y "${args[@]}" "${maps[@]}" "$ROOT/$name/${name}.mkv"
    printf '  %-34s %s\n' "$name" \
        "$("$FP" -v error -select_streams a -show_entries stream=codec_name,channels,channel_layout,bit_rate:stream_tags=language -of csv=p=0 "$ROOT/$name/${name}.mkv" | paste -sd' | ')"
done

# The rig exists to be written into: a root-owned tree silently fails the plugin's storage
# preflight, which is how the first fixture library sat unusable for a week.
if id -u "$SERVICE_USER" >/dev/null 2>&1; then
    chown -R "$SERVICE_USER":"$SERVICE_USER" "$ROOT"
    chmod -R u=rwX,go=rX "$ROOT"
    echo "Owned by $SERVICE_USER, so the plugin can write sidecars here."
else
    echo "WARNING: user '$SERVICE_USER' does not exist — the fixtures stay root-owned and the plugin"
    echo "         will refuse to write into them. Pass SERVICE_USER=<account> to fix that."
fi

echo "Built ${#FIXTURES[@]} fixtures ($(du -sh "$ROOT" | cut -f1))"
