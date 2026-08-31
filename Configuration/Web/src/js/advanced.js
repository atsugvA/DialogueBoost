/*
 * The Advanced panel: the detail behind the four numbers in the band.
 *
 * Storage and the last run are read from the same responses the band already fetched — the
 * storage probe opens every folder the selection covers, so asking for it twice on one page load
 * would double real filesystem work for the same answer.
 */

var historyRecords = [];

// The state database stores .NET ticks — 100-nanosecond intervals since year 1 — not a Unix
// timestamp, so it needs both the scale and the epoch moved.
var TICKS_PER_MS = 10000;
var TICKS_AT_UNIX_EPOCH = 621355968000000000;

function whenTicks(ticks) {
    if (!ticks) {
        return 'unknown';
    }
    return whenLocal(new Date((ticks - TICKS_AT_UNIX_EPOCH) / TICKS_PER_MS).toISOString());
}

/* ── storage, in full ───────────────────────────────────────────────────────────────────── */

function renderStorageDetail(report) {
    var problems = (report && report.Problems) || [];
    var checked = (report && report.FoldersChecked) || 0;

    document.getElementById('advStorageUser').textContent = (report && report.ServiceUser) || 'unknown';
    document.getElementById('advStorageCount').innerHTML = checked
        ? esc(checked) + ' — ' + (problems.length
            ? '<span class="db-bad">' + esc(problems.length) + ' refused</span>'
            : '<span class="db-ok">all writable</span>')
        : 'nothing chosen to probe';

    // The whole reason this exists: .NET reports "not mounted", "read-only" and "denied"
    // identically, so the explanation the probe wrote is worth more than the exception was.
    document.getElementById('advStorageProblems').innerHTML = problems.map(function (problem) {
        return '<div class="db-kv"><span class="db-kv-k db-bad">' + esc(problem.State) + '</span>' +
               '<span class="db-kv-v db-mono">' + esc(problem.Directory) +
               '<div class="db-path">' + esc(problem.Explanation) + '</div></span></div>';
    }).join('') + (report && report.Truncated
        ? '<div class="db-kv"><span class="db-kv-k"></span><span class="db-kv-v">' +
          'More folders exist than the check looks at; these are the first it reached.</span></div>'
        : '');
}

var CLEANUP_TASK = 'DialogueBoostCleanUpWatchedTask';

function renderRunDetail(tasks) {
    var found = tasks || [];
    var task = found.filter(function (t) { return t.Key === 'DialogueBoostNormalizeTask'; })[0];

    // Cleanup can be stopped, and the only place that could was the status page this replaces.
    var cleanup = found.filter(function (t) { return t.Key === CLEANUP_TASK; })[0];
    var stop = document.getElementById('btnStopCleanup');
    if (stop) {
        stop.hidden = !cleanup || cleanup.State !== 'Running';
    }

    var result = task && task.LastExecutionResult;
    var cell = document.getElementById('advLastRun');
    if (!cell) {
        return;
    }
    if (!result) {
        cell.textContent = 'never';
        return;
    }
    var seconds = (new Date(result.EndTimeUtc) - new Date(result.StartTimeUtc)) / 1000;
    cell.innerHTML = esc(whenLocal(result.EndTimeUtc)) +
        (isFinite(seconds) && seconds >= 0 ? ' — ' + esc(seconds.toFixed(1)) + ' s' : '') +
        (result.Status === 'Completed' ? '' : ' · <span class="db-bad">' + esc(result.Status) + '</span>') +
        (result.ErrorMessage
            ? '<div class="db-reason" title="' + esc(result.ErrorMessage) + '">' +
              esc(result.ErrorMessage) + '</div>'
            : '');
}

/* ── the records ────────────────────────────────────────────────────────────────────────── */

function recordMatches(record, query) {
    if (!query) {
        return true;
    }
    return [record.SourcePath, record.ProfileId, record.Status, record.ItemId]
        .some(function (field) {
            return String(field || '').toLowerCase().indexOf(query) !== -1;
        });
}

// The sidecar's name is the part of the path worth reading, so it is the part shown in the
// accent; the rest is there to be recognised, not read.
//
// db-file is not decoration: a release filename is one unbreakable token — dots are not break
// opportunities — so this column's *minimum* width was the longest name in the table, 423 px of
// a box 801 px wide, and the whole table scrolled sideways over it. See the class in advanced.css.
function pathCell(record) {
    var source = record.SourcePath || '';
    var name = source ? source.split('/').pop() : (record.ItemId || '');
    var sidecar = record.SidecarPath || '';
    var marker = sidecar ? sidecar.split('/').pop().replace(/^.*?(?=\.[^.]+\.mka$)/, '') : '';
    return '<div class="db-file">' + esc(name) + '</div>' +
           (sidecar ? '<div class="db-path">' + esc(sidecar.replace(marker, '')) +
                      '<b>' + esc(marker) + '</b></div>' : '');
}

function historyRowHtml(record) {
    var success = record.Status === 'Success';
    var exempt = record.ExemptFromCleanup === true;
    var keys = ' data-item-id="' + esc(record.ItemId) + '" data-profile-id="' + esc(record.ProfileId) + '"';

    return '<tr>' +
        '<td>' + pathCell(record) + '</td>' +
        '<td>' + esc(record.ProfileId) + '</td>' +
        '<td>' + esc(whenTicks(record.ProcessedAtUtc)) + '</td>' +
        '<td>' + (success
            ? '<span class="db-ok">Success</span>'
            : '<span class="db-bad">' + esc(record.Status || 'Failed') + '</span>' +
              (record.SkipReason
                  ? '<div class="db-reason" title="' + esc(record.SkipReason) + '">' +
                    esc(record.SkipReason) + '</div>'
                  : '')) + '</td>' +
        '<td>' + (success
            ? '<button type="button" class="db-mini db-toggle-exempt' + (exempt ? ' db-mini-on' : '') + '"' +
              keys + ' data-exempt="' + (exempt ? 'false' : 'true') + '">' +
              (exempt ? 'Protected' : 'Removable') + '</button>'
            : '') + '</td>' +
        '<td>' + (success
            ? '<button type="button" class="db-mini db-mini-danger db-remove-sidecar"' + keys + '>Remove</button>'
            : '') + '</td>' +
        '</tr>';
}

function renderHistory(page) {
    var body = page.querySelector('#tblProcessedHistory tbody');
    var query = (page.querySelector('#txtSearchHistory').value || '').trim().toLowerCase();
    var shown = historyRecords.filter(function (record) { return recordMatches(record, query); });

    page.querySelector('#advRecordsCount').textContent = historyRecords.length
        ? count(historyRecords.length, 'record') + (query ? ', ' + shown.length + ' shown' : '')
        : '';

    body.innerHTML = shown.length
        ? shown.map(historyRowHtml).join('')
        : '<tr><td colspan="6" class="db-table-note">' +
          (query ? 'Nothing matches that.' : 'Nothing has been written yet.') + '</td></tr>';
}

function loadHistory(page) {
    return getPlugin('History?take=500').then(function (records) {
        historyRecords = records || [];
        renderHistory(page);
    }).catch(function () {
        page.querySelector('#tblProcessedHistory tbody').innerHTML =
            '<tr><td colspan="6" class="db-table-note">The records could not be read.</td></tr>';
    });
}

// Jellyfin's own dialog where the dashboard provides one, the browser's where it does not — a
// destructive action must not lose its question because a client dropped a helper.
function confirmed(question, title, then) {
    if (Dashboard && typeof Dashboard.confirm === 'function') {
        Dashboard.confirm(question, title, then);
        return;
    }
    then(window.confirm(question));
}

/* ── the way out ────────────────────────────────────────────────────────────────────────── */

/*
 * One button, two questions, and the first one knows the number.
 *
 * The server counts before it deletes and this asks for the count first, so the question is
 * "1,204 tracks in 137 folders will be deleted" rather than "are you sure?" — a warning that says
 * how much is at stake is worth more than a second dialog that does not. The second dialog is
 * still there, because this is the one action on the page that no other button can undo.
 *
 * Standing down is part of it, not an extra: deleting the tracks while the daily run is still
 * scheduled empties the library until tonight. The server does that first and in its own order;
 * this only re-reads the controls afterwards, so the page does not go on showing a schedule and
 * an enabled plugin that are no longer there.
 */
function removeEverything(page) {
    var note = page.querySelector('#removeEverythingMsg');
    note.textContent = 'Counting what is there…';

    return getPlugin('Sidecars/All').then(function (found) {
        var tracks = (found && found.Tracks) || 0;
        var folders = (found && found.Folders) || 0;
        note.textContent = '';

        var scale = tracks
            ? count(tracks, 'track') + ' in ' + count(folders, 'folder') + ' will be deleted.'
            : 'No tracks were found to delete.';

        confirmed(scale + ' Both daily runs will be switched off and the plugin disabled, so ' +
                  'nothing writes them back. Your source files are not touched.',
                  'Remove everything Dialogue Boost wrote?',
                  function (yes) {
                      if (yes) {
                          confirmLastly(page, note, tracks);
                      }
                  });
    }).catch(function () {
        note.textContent = 'Nothing was removed — the count could not be read.';
    });
}

function confirmLastly(page, note, tracks) {
    var cost = tracks
        ? 'This cannot be undone. Putting ' + count(tracks, 'track') + ' back means encoding the library again.'
        : 'This switches both daily runs off and disables the plugin.';

    confirmed(cost, tracks ? 'Delete them now?' : 'Stand the plugin down?', function (sure) {
        if (!sure) {
            return;
        }
        note.textContent = 'Removing…';
        Promise.resolve(ApiClient.ajax({ type: 'DELETE', url: pluginUrl('Sidecars/All') }))
            .then(function (result) {
                note.textContent = removalSummary(result);
                afterRemoval(page);
            })
            .catch(function () {
                note.textContent = 'It failed part way through — the records will say what is left.';
                afterRemoval(page);
            });
    });
}

function removalSummary(result) {
    var deleted = (result && result.TracksDeleted) || 0;
    var failed = (result && result.TracksFailed) || 0;
    var records = (result && result.RecordsCleared) || 0;

    return count(deleted, 'track') + ' deleted, ' + count(records, 'record') + ' cleared' +
        (failed ? ', ' + failed + ' could not be deleted — see the server log' : '') +
        '. The plugin is switched off and both daily runs are cleared.';
}

// The controls on this page are now describing a plugin that no longer holds any of it.
function afterRemoval(page) {
    page.querySelector('#chkEnabled').checked = false;
    loadSchedule(page);
    loadHistory(page);
    loadBand();
}

/* ── binding ────────────────────────────────────────────────────────────────────────────── */

/* ── who counts as having watched it ────────────────────────────────────────────────────── */

// One account finishing an episode used to count for everybody, so the cleanup deleted the track
// the others were still using. Which accounts decide is now a setting, and this is where it is
// picked. Rendered from the server's own account list so a name that changed, or an account that
// is gone, cannot linger as a tick nobody can see.
// The ticks are exactly what is stored, including none of them. An empty list is a real setting
// and the shipped one: no account decides, so nothing is ever watched, nothing is skipped and the
// cleanup deletes nothing. Pre-ticking everyone here showed a fresh install a choice it had not
// made, and one Save away is the half that deletes.
function renderWatchedAccounts(page, accounts, chosenIds) {
    var chosen = {};
    (chosenIds || []).forEach(function (id) { chosen[String(id).toLowerCase()] = true; });

    page.querySelector('#watchedAccounts').innerHTML = accounts.map(function (account) {
        // The span is not decoration: emby-checkbox classes the first span in its parent and
        // throws without one, and one throw abandons the rest of the upgrade pass.
        return '<label><input is="emby-checkbox" type="checkbox" class="db-watched-account"' +
               ' data-account-id="' + esc(account.Id) + '"' +
               (chosen[String(account.Id).toLowerCase()] ? ' checked' : '') + ' />' +
               '<span>' + esc(account.Name) + (account.IsDisabled ? ' — disabled' : '') + '</span></label>';
    }).join('');
    updateWatchedNote(page);
}

// What "none ticked" actually does, said where the ticks are — otherwise an empty list reads as
// an unfinished form rather than as the setting it is.
function updateWatchedNote(page) {
    var note = page.querySelector('#watchedNobody');
    if (note) {
        note.hidden = readWatchedAccounts(page).length > 0;
    }
}

// Switching *to* this policy is the moment where starting from every enabled account is helpful:
// it is what the policy being left behind did, so the change alone alters nothing. Doing it on
// load instead is what made the page disagree with the configuration.
function fillWatchedAccountsForSwitch(page) {
    if (readWatchedAccounts(page).length) {
        return;
    }
    page.querySelectorAll('.db-watched-account').forEach(function (box) {
        if (!/— disabled$/.test(box.parentNode.textContent.trim())) {
            box.checked = true;
        }
    });
}

function showWatchedAccounts(page) {
    var on = page.querySelector('#selWatchedBy').value === 'ChosenAccounts';
    page.querySelector('#watchedAccounts').hidden = !on;
    var note = page.querySelector('#watchedNobody');
    if (note) {
        note.hidden = !on || readWatchedAccounts(page).length > 0;
    }
}

function loadWatchedAccounts(page, config) {
    return getPlugin('Accounts')
        .then(function (report) {
            renderWatchedAccounts(page, (report && report.Accounts) || [], config.WatchedByUserIds);
            showWatchedAccounts(page);
        })
        .catch(function () {
            page.querySelector('#watchedAccounts').innerHTML =
                '<span class="db-kv-note">Could not read the server\'s accounts.</span>';
        });
}

function readWatchedAccounts(page) {
    return Array.prototype.map.call(
        page.querySelectorAll('.db-watched-account:checked'),
        function (box) { return box.getAttribute('data-account-id'); });
}

/* ── the profiles, as the server defines them ───────────────────────────────────────────── */

// The configuration carries each profile's own id and name, so the page never has to hold a
// second copy of them. It used to, and the copy drifted.
var PROFILE_KEYS = [
    'DialogueBoostProfile', 'NightModeProfile', 'SpeechProfile', 'Ebur128Profile', 'CustomProfile'
];

function renderProfileChoices(page, config) {
    page.querySelector('#selectProfileToDelete').innerHTML = PROFILE_KEYS
        .map(function (key) { return config[key]; })
        .filter(function (profile) { return profile && profile.Id; })
        .map(function (profile) {
            return '<option value="' + esc(profile.Id) + '">' + esc(profile.Name || profile.Id) + '</option>';
        })
        .join('');
}

function bindAdvanced(page) {
    page.querySelector('#selWatchedBy').onchange = function () {
        if (this.value === 'ChosenAccounts') {
            fillWatchedAccountsForSwitch(page);
        }
        showWatchedAccounts(page);
    };
    page.querySelector('#watchedAccounts').onchange = function () { updateWatchedNote(page); };
    page.querySelector('#txtSearchHistory').oninput = function () { renderHistory(page); };
    page.querySelector('#btnRefreshHistory').onclick = function () { loadHistory(page); };

    page.querySelector('#tblProcessedHistory').onclick = function (event) {
        var button = event.target.closest('.db-mini');
        if (!button) {
            return;
        }
        var route = 'Items/' + encodeURIComponent(button.getAttribute('data-item-id'));
        var profile = 'profileId=' + encodeURIComponent(button.getAttribute('data-profile-id'));

        if (button.classList.contains('db-toggle-exempt')) {
            postPlugin(route + '/Exempt?' + profile + '&exempt=' + button.getAttribute('data-exempt'))
                .then(function () { loadHistory(page); });
            return;
        }

        // Removing a written track changes what the band is counting.
        Promise.resolve(ApiClient.ajax({ type: 'DELETE', url: pluginUrl(route + '/Sidecar?' + profile) }))
            .then(function () { loadHistory(page); loadBandCounts(); });
    };

    page.querySelector('#btnDeleteProfileSidecars').onclick = function () {
        var profile = page.querySelector('#selectProfileToDelete').value;
        var note = page.querySelector('#bulkDeleteStatusMsg');

        var question = 'Every track written by ' + profile + ' will be deleted from disk. ' +
            'Source files are not touched. This cannot be undone.';

        confirmed(question, 'Remove every track for that profile?', function (yes) {
                if (!yes) {
                    return;
                }
                note.textContent = 'Removing…';
                Promise.resolve(ApiClient.ajax({
                    type: 'DELETE',
                    url: pluginUrl('Sidecars/DeleteByProfile?profileId=' + encodeURIComponent(profile))
                })).then(function (result) {
                    note.textContent = (result && (result.Message || result.message)) || 'Removed.';
                    loadHistory(page);
                    loadBandCounts();
                }).catch(function () {
                    note.textContent = 'Nothing was removed — the request failed.';
                });
        });
    };

    page.querySelector('#btnRemoveEverything').onclick = function () { removeEverything(page); };

    // Both cleanup buttons start the same task; the second one tells it to ignore protection,
    // which is what "purge" always meant, and it asks first because nothing else can undo it.
    var cleanupNote = page.querySelector('#cleanupStatusMessage');

    function startCleanup(route, starting) {
        cleanupNote.textContent = starting;
        postPlugin(route)
            .then(function () { cleanupNote.textContent = 'Started. It runs as a scheduled task.'; })
            .catch(function () { cleanupNote.textContent = 'It could not be started.'; });
    }

    page.querySelector('#btnRunCleanupNow').onclick = function () {
        startCleanup('Task/CleanUpWatched/Start', 'Starting…');
    };

    page.querySelector('#btnStopCleanup').onclick = function () {
        postPlugin('Task/CleanUpWatched/Stop')
            .then(function () { cleanupNote.textContent = 'Stop requested.'; });
    };

    page.querySelector('#btnPurgeExemptNow').onclick = function () {
        confirmed('Every written track on a watched item will be removed, including the ones ' +
                  'marked protected. Source files are not touched.',
                  'Remove protected tracks too?',
                  function (yes) {
                      if (yes) {
                          startCleanup('Task/CleanUpWatched/Start?includeExempt=true', 'Starting…');
                      }
                  });
    };

    loadHistory(page);
}
