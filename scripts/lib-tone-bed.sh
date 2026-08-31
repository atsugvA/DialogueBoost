#!/usr/bin/env bash
# A multichannel test signal you can read a mistake off, and the reader for it.
#
# Source this; it defines FF, FP, tone_bed and channel_levels.
#
# Every channel of the bed carries its own tone, each 3.74 dB below the one before it, so a channel
# that was silenced, duplicated, swapped or dropped shows up in a single astats read rather than
# having to be listened for. That is what the silent-surround bug needed and did not have:
# `pan` answers an input
# channel the stream does not carry with silence, exit 0 and no warning, so every 5.1 sidecar the
# plugin wrote for months had two dead surrounds and still sounded like a working track.

FF=$(command -v /usr/lib/jellyfin-ffmpeg/ffmpeg || command -v ffmpeg) || { echo "no ffmpeg on PATH" >&2; exit 1; }
FP=$(command -v /usr/lib/jellyfin-ffmpeg/ffprobe || command -v ffprobe) || { echo "no ffprobe on PATH" >&2; exit 1; }
readonly FF FP

# tone_bed <layout> <output.wav> [seconds] — 24-bit PCM at the named layout.
tone_bed() {
    local layout="$1" out="$2" dur="${3:-3}"
    local names name ch inputs=() filters="" labels="" map="" i=0 amp freq
    names=$("$FF" -hide_banner -layouts 2>/dev/null | awk -v l="$layout" '$1==l{print $2}')
    [ -n "$names" ] || { echo "tone_bed: ffmpeg does not name the layout '$layout'" >&2; return 1; }
    local IFS='+'; read -ra ch <<< "$names"; IFS=$' \t\n'
    for name in "${ch[@]}"; do
        amp=$(awk -v i="$i" 'BEGIN{printf "%.6f", 0.65 ^ i}')
        # An LFE channel is band-limited by every encoder that has one, so a tone above its cut-off
        # would read back as a 47 dB loss that is the test signal's fault and not the encoder's.
        freq=$(( 200 * (i + 1) )); case "$name" in LFE*) freq=60;; esac
        inputs+=( -f lavfi -i "sine=frequency=${freq}:duration=${dur}:sample_rate=48000" )
        filters+="[${i}:a]volume=${amp}[c${i}];"; labels+="[c${i}]"; map+="${i}.0-${name}|"
        i=$((i + 1))
    done
    # join maps by channel *name* when it can, and every mono input is called FC — so the mapping is
    # written out rather than left to it, or input 0 lands in the centre and the rest shuffle up.
    "$FF" -hide_banner -loglevel error -y "${inputs[@]}" \
        -filter_complex "${filters}${labels}join=inputs=${i}:channel_layout=${layout}:map=${map%|}[a]" \
        -map "[a]" -c:a pcm_s24le "$out"
}

# channel_levels <file> [stream] — one RMS dB value per channel, in order, one per line.
channel_levels() {
    "$FF" -hide_banner -nostats -i "$1" -map "0:a:${2:-0}" \
        -af astats=measure_overall=none:measure_perchannel=RMS_level -f null - 2>&1 \
        | awk '/RMS level dB:/{print $NF}'
}
