#!/usr/bin/env bash
# Run the centre branch's whole layout table through real ffmpeg.
#
# The table in Analysis/ChannelLayoutDetector.cs makes three claims about each layout, and all
# three are measurements rather than readings of a spec:
#
#   * the channels it holds, in order      — checked against `ffmpeg -layouts`
#   * that `pan` will build that graph     — checked by running it
#   * what the chosen encoder then writes  — checked by reading the output back
#
# So the table is parsed out of the source rather than restated here: the two cannot drift.
# Every layout gets a bed of tones, one per channel, each 3.74 dB below the last, so a channel
# that was silenced, duplicated or swapped is visible in one read. A silenced surround looks
# like: `pan` answers an input channel that is not there with silence, exit 0 and no warning.
#
#   ./scripts/check-filter-graphs.sh            # every row
#   ./scripts/check-filter-graphs.sh 6.1 7.1    # just these
#
# Requires jellyfin-ffmpeg (or ffmpeg on PATH). Writes only to its own temp directory.

set -uo pipefail

readonly SRC="$(dirname "$0")/../Analysis/ChannelLayoutDetector.cs"
# shellcheck source=scripts/lib-tone-bed.sh
. "$(dirname "$0")/lib-tone-bed.sh"
readonly WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT

# +4.0 dB, the shipped default, as the linear multiplier the plugin writes into the graph.
readonly GAIN_DB=4.0
readonly GAIN=$(awk -v d="$GAIN_DB" 'BEGIN{printf "%.6g", 10 ^ (d / 20)}')
# What alimiter=limit=0.891 adds to everything, measured: the same makeup on every channel.
readonly MAKEUP=1.0
readonly TOLERANCE=1.5


pass=0; fail=0; skipped=0
printf '%-16s %-5s %-6s %-8s %s\n' LAYOUT CODEC OUT GRAPH LEVELS
while IFS='|' read -r key name channels codec outch; do
    [ -n "${1:-}" ] && { printf '%s\n' "$@" | grep -qxF "$key" || continue; }

    # What the plugin takes from a row is how many channels it holds and where the centre sits —
    # the graph is built by position, never by name. Those two have to be what this ffmpeg makes of
    # the name; the other names are allowed to differ, because the builds do: jellyfin-ffmpeg 8
    # moved the surrounds of 5.1.2 and 5.1.4 from BL BR to SL SR. A layout this build has no name
    # for is one no stream can arrive under while it is the decoder, so it is skipped, not failed.
    want=$("$FF" -hide_banner -layouts 2>/dev/null | awk -v l="$name" '$1==l{print $2}' | tr '+' ' ')
    if [ -z "$want" ]; then
        printf '%-16s %-5s %-6s %-8s n/a   this ffmpeg has no layout of that name\n' "$key" "$codec" "$outch" "channels"
        skipped=$((skipped + 1)); continue
    fi
    centre_of() { local i=0 c; for c in $1; do [ "$c" = FC ] && { echo "$i"; return; }; i=$((i + 1)); done; echo -1; }
    if [ "$(wc -w <<< "$want")" != "$(wc -w <<< "$channels")" ] || [ "$(centre_of "$want")" != "$(centre_of "$channels")" ]; then
        printf '%-16s %-5s %-6s %-8s FAIL  table says "%s", ffmpeg says "%s"\n' \
            "$key" "$codec" "$outch" "channels" "$channels" "$want"; fail=$((fail + 1)); continue
    fi
    renamed=""; [ "$want" != "$channels" ] && renamed="; this ffmpeg spells it \"$want\""

    tone_bed "$name" "$WORK/in.wav" || { printf '%-16s no bed\n' "$key"; fail=$((fail + 1)); continue; }
    read -ra ch <<< "$channels"; n=${#ch[@]}
    centre=-1; for i in "${!ch[@]}"; do [ "${ch[$i]}" = FC ] && centre=$i; done

    # A row with no FC is in the table only for its carrier — cube, which aac refuses — so the
    # graph is a straight pass-through and there is no lift to look for.
    graph=""; for ((i = 0; i < n; i++)); do
        [ "$i" -eq "$centre" ] && graph+="|c${i}=${GAIN}*c${i}" || graph+="|c${i}=c${i}"
    done
    rate=640; [ "$codec" != eac3 ] && rate=$((64 * outch))

    if ! "$FF" -hide_banner -loglevel error -y -i "$WORK/in.wav" \
            -af "pan=${name}${graph},alimiter=limit=0.891" -c:a "$codec" -b:a "${rate}k" \
            -f matroska "$WORK/out.mka" 2>"$WORK/err"; then
        printf '%-16s %-5s %-6s %-8s FAIL  %s\n' "$key" "$codec" "$outch" "pan" "$(head -1 "$WORK/err")"
        fail=$((fail + 1)); continue
    fi

    got=$("$FP" -v error -select_streams a:0 -show_entries stream=channels -of csv=p=0 "$WORK/out.mka")
    mapfile -t db < <(channel_levels "$WORK/out.mka")
    note=""; ok=1
    [ "$got" = "$outch" ] || { note="encoder wrote $got channels, table says $outch"; ok=0; }

    if [ "$ok" = 1 ] && [ "$got" = "$n" ]; then
        # Nothing folded, so every channel is comparable to the tone that went in.
        for ((i = 0; i < n; i++)); do
            expect=$(awk -v i="$i" -v m="$MAKEUP" -v g="$GAIN_DB" -v c="$centre" \
                'BEGIN{printf "%.2f", -21.07 - 3.741 * i + m + (i == c ? g : 0)}')
            off=$(awk -v a="${db[$i]:--999}" -v b="$expect" 'BEGIN{d=a-b; printf "%.2f", d<0?-d:d}')
            awk -v o="$off" -v t="$TOLERANCE" 'BEGIN{exit !(o > t)}' \
                && { note="c$i at ${db[$i]} dB, expected $expect"; ok=0; break; }
        done
    elif [ "$ok" = 1 ]; then
        # A fold: the channels no longer line up, but none of them may be silent.
        for ((i = 0; i < got; i++)); do
            awk -v a="${db[$i]:--999}" 'BEGIN{exit !(a < -80)}' \
                && { note="c$i is silent at ${db[$i]} dB after the fold"; ok=0; break; }
        done
        note="${note:-folded ${n} -> ${got}, every channel live}"
    fi

    [ "$centre" -lt 0 ] && note="${note:+$note; }no centre, carrier only"
    note="${note}${renamed}"

    if [ "$ok" = 1 ]; then
        pass=$((pass + 1)); printf '%-16s %-5s %-6s %-8s ok    %s\n' "$key" "$codec" "$got" "pan" "$note"
    else
        fail=$((fail + 1)); printf '%-16s %-5s %-6s %-8s FAIL  %s\n' "$key" "$codec" "$got" "pan" "$note"
    fi
done < <(sed -nE 's/^ *\["([^"]+)"\] = Layout\("([^"]+)", "([^"]+)", "([^"]+)", ([0-9]+)\).*/\1|\2|\3|\4|\5/p' "$SRC")

# The other table in that file: what ffmpeg calls a stream that names no layout. Raw PCM carries
# none by construction, so feeding it some at each count asks ffmpeg the question directly.
echo
printf '%-16s %-6s %s\n' COUNT DEFAULT 'WHAT FFMPEG CALLS AN UNNAMED STREAM OF THAT MANY'
while IFS='|' read -r count want; do
    got=$(head -c $((4096 * count)) /dev/zero \
        | "$FF" -hide_banner -nostats -f s16le -ar 48000 -ac "$count" -i - -f null - 2>&1 \
        | grep -m1 -oP 'Audio: pcm_s16le, 48000 Hz, \K[^,]+')
    if [ "$got" = "$want" ]; then
        pass=$((pass + 1)); printf '%-16s %-6s ok\n' "$count channels" "$want"
    else
        fail=$((fail + 1)); printf '%-16s %-6s FAIL  ffmpeg says "%s"\n' "$count channels" "$want" "$got"
    fi
done < <(sed -nE 's/^ *\[([0-9]+)\] = "([^"]+)",?$/\1|\2/p' "$SRC")

echo
echo "$pass passed, $fail failed, $skipped not known to this ffmpeg ($("$FF" -hide_banner -version | head -1 | cut -d' ' -f3))"
[ "$fail" -eq 0 ]
