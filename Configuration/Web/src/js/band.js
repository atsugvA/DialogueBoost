/*
 * The status band.
 *
 * Four numbers that answer "is it working?" before anything is clicked, and two strips for the
 * times something matters more than a number: a run in progress, and a folder that cannot be
 * written — the failure that went unreported for a week because the old page had nowhere to
 * say it.
 *
 * Live progress is event-driven, and over Jellyfin's own channel rather than one of ours.
 * window.TaskButton is the helper its dashboard uses for exactly this: it starts the task, keeps
 * the button disabled while it runs, opens the ScheduledTasksInfo socket stream, and re-polls
 * only when that channel is shut. We add one listener on the same stream to draw the strip. If a
 * future client drops either global the band still loads its numbers — it just stops moving,
 * which is honest, where a timer of our own would be a second mechanism to maintain.
 */

var NORMALIZE_TASK = 'DialogueBoostNormalizeTask';

var bandRunning = false;
var bandTaskBound = false;
var bandSocketBound = false;
var bandCountsAt = 0;
var bandCountsBusy = false;

/*
 * How often the numbers may be re-read while a run is moving them.
 *
 * Not a timer: Jellyfin's own task messages are the clock, and this only says how many of them to
 * act on — the stream ticks about once a second, and the forecast behind the numbers is one query
 * per account and one per profile. Cheap, but not free enough to run at the stream's rate.
 */
var BAND_COUNTS_MS = 4000;

function bandEl(id) {
    return document.getElementById(id);
}

function setCell(valueId, noteId, value, note) {
    var v = bandEl(valueId);
    var n = bandEl(noteId);
    if (v) { v.innerHTML = value; }
    if (n) { n.textContent = note; }
}

/* ── the four numbers ───────────────────────────────────────────────────────────────────── */

function renderStatusCells(status) {
    var covered = status.CoveredItems || 0;
    var scopes = status.SelectedScopeCount || 0;

    setCell('bandCovered', 'bandCoveredNote',
        covered ? esc(count(covered, 'item')) : 'Nothing chosen',
        covered ? 'in ' + count(scopes, 'chosen row') : 'pick what to process below');

    var written = status.WrittenTracks || 0;
    var outstanding = status.OutstandingTracks || 0;
    var unverified = status.UnverifiedTracks || 0;

    setCell('bandWritten', 'bandWrittenNote',
        esc(written) + writtenRest(outstanding, status.Skipped),
        writtenNote(status.DryRun, unverified));

    // Amber rather than a fourth strip: the number is still true, it is the note under it that
    // would otherwise promise something the settings have switched off.
    var cell = bandEl('bandWrittenCell');
    if (cell) { cell.classList.toggle('db-cell-warn', !!status.DryRun); }
}

/*
 * "at the current settings" is a promise, and a dry run is the one setting that breaks it.
 *
 * With DryRun on, a run decides everything and writes nothing — so "3 left · at the current
 * settings" read as work that was about to happen while the settings guaranteed it would not.
 * The server has always returned the flag; the page never looked at it. Same class of falsehood
 * as the "4 left" that could never go down.
 */
function writtenNote(dryRun, unverified) {
    if (dryRun) {
        return 'dry run is on — a run decides everything and writes nothing';
    }
    return unverified
        ? count(unverified, 'track') + ' written before the settings were recorded'
        : 'at the current settings';
}

/*
 * What follows the written count, and it is not always "left".
 *
 * A track with no sidecar is only *left* if the next run would write one. Four episodes everybody
 * has finished are not: the gate refuses them before it looks at anything, so "4 left" was a
 * number that could never go down, under a note that said "at the current settings" — the one part
 * of the cell that was actually false. The server now separates the two, so the band can name each
 * of them. Both together read "3 left · 4 watched", which is the whole truth in five words.
 */
function writtenRest(outstanding, skipped) {
    var parts = [];
    if (outstanding) { parts.push(esc(outstanding) + ' left'); }

    var reasons = { Watched: 'watched', NothingToProcess: 'nothing to boost' };
    Object.keys(skipped || {}).forEach(function (key) {
        if (skipped[key]) { parts.push(esc(skipped[key]) + ' ' + (reasons[key] || key.toLowerCase())); }
    });

    // One element per part, so a cell too narrow for all of them breaks between two facts rather
    // than through one: "0 · 29 left · 6" above "watched" is a sentence nobody wrote.
    return parts.map(function (part) { return ' <em>&middot; ' + part + '</em>'; }).join('');
}

/*
 * "02:00" as the reader's own clock writes it.
 *
 * The cell printed the stored HH:mm straight through while the note under it went through
 * toLocaleTimeString, so a 12-hour reader got "02:00" above "last ran today 06:30 PM" and could not
 * tell which two in the morning was meant. Same formatter for both, so the two halves of one cell
 * agree.
 *
 * The time is the *server's* clock — that is when the task fires — so it is formatted, never
 * converted. Shifting it into the browser's timezone would name an hour the task does not run at.
 */
function clockLocal(hhmm) {
    var parts = /^(\d{1,2}):(\d{2})$/.exec(hhmm || '');
    if (!parts) { return hhmm || ''; }

    var when = new Date();
    when.setHours(Number(parts[1]), Number(parts[2]), 0, 0);
    return when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function renderScheduleCell(schedule, lastRun) {
    var daily = (schedule && schedule.Normalization) || {};
    var note = daily.Enabled ? 'daily' : 'no daily run — start it in Advanced';
    if (lastRun) {
        note += ' · last ran ' + lastRun;
    }
    setCell('bandNext', 'bandNextNote',
        daily.Enabled && daily.TimeOfDay ? esc(clockLocal(daily.TimeOfDay)) : 'Manual', note);
}

function renderStorageCell(report) {
    var checked = (report && report.FoldersChecked) || 0;
    var problems = (report && report.Problems) || [];
    var writable = checked - problems.length;
    var tight = storageIsTight(report);
    var cell = bandEl('bandStorageCell');
    var band = bandEl('dbBand');

    // The value is a ratio with no unit on it, so the note carries the noun: "30 / 30" over
    // "folders writable as jellyfin" rather than over "writable as jellyfin", which left the
    // number standing for nothing anybody could name. Folders, not items — several episodes share
    // a release folder, and the permission that decides everything lives on the folder.
    setCell('bandStorage', 'bandStorageNote',
        checked ? esc(writable) + ' / ' + esc(checked) : 'Not checked',
        checked
            ? (problems.length
                ? count(problems.length, 'folder') + ' of ' + checked + ' cannot be written'
                : tight
                    ? 'only ' + size(report.FreeBytes) + ' free on Jellyfin\'s own disk — see Advanced'
                    : (checked === 1 ? 'folder' : 'folders') + ' writable as ' +
                      (report.ServiceUser || 'the service account') +
                      (report.Location === 'BesideMedia' ? ', beside the media' : ', in Jellyfin\'s metadata folder'))
            : 'nothing chosen to probe');

    if (cell) { cell.classList.toggle('db-cell-warn', problems.length > 0 || tight); }
    if (band) { band.setAttribute('data-storage', problems.length ? 'bad' : 'ok'); }
    if (problems.length) { renderStorageAlert(report, problems); }
}

// Named paths, not a count: a per-folder permission failure stayed invisible for a week
// because nothing ever said which folder.
function renderStorageAlert(report, problems) {
    var title = bandEl('bandAlertTitle');
    var list = bandEl('bandAlertPaths');
    if (title) {
        title.innerHTML = esc(count(problems.length, 'folder')) + ' of ' + esc(report.FoldersChecked) +
            ' cannot be written by <b>' + esc(report.ServiceUser) + '</b> — those items will be skipped.';
    }
    if (list) {
        list.innerHTML = problems.slice(0, 3).map(function (p) {
            return '<div class="db-alert-p">' + esc(p.Directory) + ' &mdash; <b>' + esc(p.State) + '</b></div>';
        }).join('') + (problems.length > 3
            ? '<div class="db-alert-p">and ' + esc(problems.length - 3) + ' more — see Advanced</div>'
            : '');
    }
}

/* ── live progress ──────────────────────────────────────────────────────────────────────── */

// "today 06:12", "yesterday 02:00", or the date once it is older than that.
function whenLocal(utcText) {
    if (!utcText) { return ''; }
    var when = new Date(utcText);
    if (isNaN(when.getTime())) { return ''; }
    var clock = when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    var midnight = new Date();
    midnight.setHours(0, 0, 0, 0);
    var days = Math.floor((midnight - when) / 86400000);
    if (days < 0) { return 'today ' + clock; }
    if (days < 1) { return 'yesterday ' + clock; }
    return when.toLocaleDateString() + ' ' + clock;
}

function renderRun(task) {
    var band = bandEl('dbBand');
    var stop = bandEl('btnBandStop');
    var running = !!task && task.State === 'Running';
    var pct = Math.min(100, Math.max(0, task && task.CurrentProgressPercentage || 0));

    if (band) { band.setAttribute('data-running', running ? '1' : '0'); }
    if (stop) { stop.hidden = !running; }

    var fill = bandEl('bandBarFill');
    if (fill) { fill.style.width = pct.toFixed(1) + '%'; }
    var text = bandEl('bandPct');
    if (text) { text.textContent = pct.toFixed(1) + '%'; }
    var note = bandEl('bandRunNote');
    if (note) {
        note.textContent = task && task.Name ? task.Name.replace(/^Dialogue Boost: /, '') : '';
    }

    // A run in progress is changing the numbers above the bar as it goes. They used to sit at
    // whatever the page had loaded with until the whole run ended — "29 left" for thirteen
    // minutes, and then nothing left at all.
    if (running) {
        refreshCountsWhileRunning();
    }

    // A run that has just finished changed the numbers the band is showing, so re-read them.
    if (bandRunning && !running) {
        loadBandCounts();
    }
    bandRunning = running;
}

function onServerMessage(event, message) {
    if (!message || message.MessageType !== 'ScheduledTasksInfo' || !message.Data) {
        return;
    }
    renderRun(message.Data.filter(function (t) { return t.Key === NORMALIZE_TASK; })[0]);
    renderRunDetail(message.Data);
}

/* ── loading ────────────────────────────────────────────────────────────────────────────── */

// Each cell is loaded on its own so that one endpoint being down leaves the others honest
// rather than blanking the whole band.
function loadBandCounts() {
    if (bandCountsBusy) {
        return;
    }
    bandCountsBusy = true;
    bandCountsAt = Date.now();

    getPlugin('Status').then(function (status) {
        bandCountsBusy = false;
        renderStatusCells(status);
    }).catch(function () {
        bandCountsBusy = false;
        setCell('bandCovered', 'bandCoveredNote', 'Unknown', 'the plugin did not answer');
        setCell('bandWritten', 'bandWrittenNote', 'Unknown', 'the plugin did not answer');
    });
}

// One re-read per BAND_COUNTS_MS at most, and never two at once — a run is the busiest the server
// ever is, and the band asking again before its last answer arrived would be the page competing
// with the encodes it is reporting on.
function refreshCountsWhileRunning() {
    if (Date.now() - bandCountsAt >= BAND_COUNTS_MS) {
        loadBandCounts();
    }
}

function loadBand() {
    loadBandCounts();

    Promise.all([
        getPlugin('Schedule').catch(function () { return null; }),
        getPlugin('Tasks/Status').catch(function () { return []; })
    ]).then(function (results) {
        var tasks = results[1] || [];
        var task = tasks.filter(function (t) { return t.Key === NORMALIZE_TASK; })[0];
        var last = task && task.LastExecutionResult ? whenLocal(task.LastExecutionResult.EndTimeUtc) : '';
        renderScheduleCell(results[0], last);
        renderRunDetail(tasks);
        renderRun(task);
        bandRunning = !!task && task.State === 'Running';
    });

    // One fetch, two renderers: the probe opens every folder the selection covers, so asking
    // for it again to fill in the Advanced rows would double real filesystem work.
    getPlugin('Storage').then(function (report) {
        renderStorageCell(report);
        renderStorageDetail(report);
    }).catch(function () {
        setCell('bandStorage', 'bandStorageNote', 'Unknown', 'the probe did not answer');
    });
}

function bindBand(page) {
    var run = page.querySelector('#btnBandRun');

    if (run && typeof TaskButton === 'function' && !bandTaskBound) {
        TaskButton({ mode: 'on', taskKey: NORMALIZE_TASK, button: run });
        bandTaskBound = true;
    }

    if (typeof Events !== 'undefined' && !bandSocketBound) {
        Events.on(ApiClient, 'message', onServerMessage);
        bandSocketBound = true;
    }

    var stop = page.querySelector('#btnBandStop');
    if (stop) {
        stop.onclick = function () {
            postPlugin('Task/Stop').then(function () { stop.hidden = true; });
        };
    }

    loadBand();
}

function unbindBand(page) {
    var run = page.querySelector('#btnBandRun');
    if (run && typeof TaskButton === 'function' && bandTaskBound) {
        TaskButton({ mode: 'off', taskKey: NORMALIZE_TASK, button: run });
        bandTaskBound = false;
    }
    if (typeof Events !== 'undefined' && bandSocketBound) {
        Events.off(ApiClient, 'message', onServerMessage);
        bandSocketBound = false;
    }
}
