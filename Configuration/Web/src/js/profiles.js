/*
 * What each profile is going to do, said before it does it.
 *
 * Two things here are not settings: the figure beside Dialogue Boost, which is the whole plugin
 * in one glyph — five channels left alone, the centre lifted — and the filename each card ends
 * with, which is the fastest way to understand what a "sidecar" is. Both follow the fields as
 * they are typed, so the card answers "what will this actually write" without saving first.
 */

// A placeholder, and deliberately not a real release name. The shape is what the card is for —
// the source's own name, then the ending this profile adds — but a real-looking name reads as
// something out of the viewer's own library, and the first person to see one asked whether that
// half was fixed too. It is not: the sidecar takes whatever the source file is called. Saying so
// in a sentence underneath was the other way to answer that, and it needed the sentence on every
// card; a name no library contains answers it once, in the name itself.
var SAMPLE_SOURCE = 'Example.Movie.2024.1080p.BluRay.x264';

// Anything a filesystem cannot take is stripped here exactly as the server strips it. The server
// also renames a marker Jellyfin would read as a flag or a language code — `ebu` becomes
// `ebu_Boost` — which needs ICU's list of every language and is not worth shipping to the browser
// for a preview. So this shows the name for every marker but that one, where the server's is
// longer, never different in kind.
function markerPreview(marker) {
    var cleaned = String(marker || '').replace(/[\\/:*?"<>|]/g, '').trim();
    return cleaned || 'DialogueBoost';
}

// The ".default" token is Jellyfin's own, and it is what actually starts playback on a track —
// the container flag is ignored outright on a one-track sidecar. So the switch changes the
// filename, and the card has to show that rather than describe it.
function claimsDefault(page, markerId) {
    var code = /^txt(.+)Marker$/.exec(markerId);
    if (!code) {
        return false;
    }
    var enabled = page.querySelector('#chk' + code[1] + 'Enabled');
    var claim = page.querySelector('#chk' + code[1] + 'SetDefault');
    return !!(enabled && enabled.checked && claim && claim.checked);
}

function renderOutputName(page, markerId) {
    var out = page.querySelector('[data-out-for="' + markerId + '"]');
    if (!out) {
        return;
    }
    var tail = '.' + markerPreview(page.querySelector('#' + markerId).value) +
        (claimsDefault(page, markerId) ? '.default' : '') + '.mka';
    out.innerHTML = '<span class="db-out-src">' + esc(SAMPLE_SOURCE) + '</span>' +
        '<b>' + esc(tail) + '</b>';
}

function renderOutputNames(page) {
    page.querySelectorAll('[data-out-for]').forEach(function (out) {
        renderOutputName(page, out.getAttribute('data-out-for'));
    });
}

// A configuration written through the API can hold two claims; the server resolves them the same
// way, so the page has to show the winner rather than both. First enabled one wins, in the order
// the profiles are declared — which is the order they appear here.
function resolveDefaultClaim(page) {
    var claims = Array.prototype.slice.call(page.querySelectorAll('[id$="SetDefault"]'))
        .filter(function (claim) { return claim.checked; });

    var winner = claims.filter(function (claim) {
        var code = /^chk(.+)SetDefault$/.exec(claim.id);
        var enabled = code && page.querySelector('#chk' + code[1] + 'Enabled');
        return enabled && enabled.checked;
    })[0] || claims[0];

    claims.forEach(function (claim) {
        claim.checked = claim === winner;
    });
}

/*
 * What each bitrate mode will actually write.
 *
 * "The profile default" was a phrase naming a number the page never showed. Picking it did
 * nothing visible on four of the five cards — only Custom, which carries a Profile default field
 * of its own, could be read at all — so the one mode with a fixed answer was the one nobody could
 * see the answer to. Each card names its own on the select, because the default is not one number:
 * the centre-gain branch writes eac3, the two stereo branches write aac, and Auto falls back to
 * whichever of them applies.
 */
var BITRATE_HINT = {
    Auto: 'Follows the source: a 384k track is written at 384k. A branch that changes the channel ' +
        'count, or a lossless source, has no rate worth following and falls back to the profile ' +
        'default — {preset}.',
    Preset: 'Always {preset} — whatever rate the source itself carries. A re-encode cannot add what ' +
        'the source did not have, so 640k over a 384k track is paid on disk and nowhere else.',
    Manual: 'Always the rate below. A re-encode cannot add what the source did not have, so 640k ' +
        'over a 384k track is paid on disk and nowhere else.'
};

// The rate field is only a setting in one of the three modes; in the other two the number shown
// would be a rate nothing writes. The sentence under the mode says what the other two do instead.
function renderBitrateFields(page) {
    page.querySelectorAll('[data-bitrate-manual]').forEach(function (field) {
        var code = field.getAttribute('data-bitrate-manual');
        var mode = page.querySelector('#sel' + code + 'BitrateMode');
        field.hidden = !mode || mode.value !== 'Manual';
    });

    page.querySelectorAll('[data-bitrate-hint]').forEach(function (note) {
        var mode = page.querySelector('#sel' + note.getAttribute('data-bitrate-hint') + 'BitrateMode');
        if (!mode) {
            return;
        }
        note.textContent = (BITRATE_HINT[mode.value] || '')
            .replace('{preset}', mode.getAttribute('data-preset') || 'the profile default');
    });
}

// Schematic on purpose: the bar says the centre is the one being lifted, and the number beside
// it says by how much. Anything more precise would be a claim about a mix we have not read.
function renderMeter(page) {
    var gain = parseFloat(page.querySelector('#txtCenterGainDb').value);
    if (!isFinite(gain)) {
        gain = 0;
    }
    var lift = Math.max(0, Math.min(12, gain));

    page.querySelectorAll('#dbMeter .db-meter-ch').forEach(function (channel, index) {
        var centre = channel.getAttribute('data-centre') === '1';
        var base = [42, 42, 44, 28, 34, 34][index] || 38;
        channel.querySelector('.db-meter-bar').style.height =
            (centre ? 44 + lift * 3.6 : base) + '%';
    });

    page.querySelector('#dbMeterCap').textContent =
        (gain > 0 ? '+' : '') + gain.toFixed(1) + ' dB';
}

function bindProfiles(page) {
    page.querySelectorAll('[data-out-for]').forEach(function (out) {
        var markerId = out.getAttribute('data-out-for');
        var field = page.querySelector('#' + markerId);
        field.addEventListener('input', function () { renderOutputName(page, markerId); });
        renderOutputName(page, markerId);
    });

    // One track, at most, can be the one playback starts on. Two sidecars both claiming it is not
    // a tie the plugin gets to break — Jellyfin takes the lowest external stream index, so the
    // winner would be whichever filename sorted first. Offered as a single choice instead, and the
    // server resolves it the same way for a configuration written any other way.
    page.querySelectorAll('[id$="SetDefault"]').forEach(function (claim) {
        claim.addEventListener('change', function () {
            if (claim.checked) {
                page.querySelectorAll('[id$="SetDefault"]').forEach(function (other) {
                    if (other !== claim) {
                        other.checked = false;
                    }
                });
            }
            renderOutputNames(page);
        });
    });

    // Switching a profile off gives up its claim, which is a filename change like any other.
    page.querySelectorAll('[id$="Enabled"]').forEach(function (enabled) {
        enabled.addEventListener('change', function () { renderOutputNames(page); });
    });

    page.querySelectorAll('[id$="BitrateMode"]').forEach(function (mode) {
        mode.addEventListener('change', function () { renderBitrateFields(page); });
    });
    renderBitrateFields(page);

    var gain = page.querySelector('#txtCenterGainDb');
    gain.addEventListener('input', function () { renderMeter(page); });
    renderMeter(page);

    // A collapsed profile opens from anywhere on its row except its own switch, which is the
    // one control that has to keep working while the card is shut.
    page.querySelectorAll('.db-alt').forEach(function (alt) {
        alt.querySelector('.db-alt-head').onclick = function (event) {
            if (event.target.closest('label')) {
                return;
            }
            alt.setAttribute('data-open', alt.getAttribute('data-open') === '1' ? '0' : '1');
        };
    });
}

// A profile switched on off-screen is still a card the user should be able to see, so anything
// enabled opens with the page.
function openEnabledProfiles(page) {
    page.querySelectorAll('.db-alt').forEach(function (alt) {
        var code = alt.getAttribute('data-alt');
        if (page.querySelector('#chk' + code + 'Enabled').checked) {
            alt.setAttribute('data-open', '1');
        }
    });
}
