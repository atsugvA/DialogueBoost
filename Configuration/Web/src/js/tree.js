/*
 * The library tree, and the selection it paints.
 *
 * The rows come from the plugin, not from Jellyfin's /Items: that surface answers with Jellyfin's
 * merged view, so a row showed more than the scope behind it covered, and depending on its
 * query semantics is exactly what a plugin meant to survive Jellyfin's churn must not do.
 *
 * What is stored is the row — a ScopePath — and never the items under it. A chosen row means
 * "this and everything inside it, now and later", which is the single property that covers
 * tomorrow's download; materialising the members re-creates the bug this replaced.
 *
 * So a row is in one of four states, and each is a string test rather than a walk, which is why
 * every mark is right on first paint instead of appearing on the first expand:
 *
 *   selected            a stored path is this row
 *   partially selected  this row is a prefix of a stored path
 *   included via X      a stored path is a prefix of this row — X owns it, and clicking does not
 *                       store a second path for the same items
 *   nothing
 */

// The rows the user has chosen, as the paths the server stores.
var selectedPaths = new Set();

// The same set as the server last reported it; everything changed since is the difference.
var savedPaths = new Set();

// Libraries Jellyfin gained since the selection was last saved. Nothing contains a library, so
// no scope can cover one — the user has simply never been asked.
var newLibraryPaths = new Set();
var newLibraryPolicy = 'FlagOnly';

// What each loaded row is called, by path, so an "included via" row can name its owner.
var rowNames = {};

var searchTimer = null;
var searchTerm = '';

/* ── the four tests ─────────────────────────────────────────────────────────────────────── */

// Segments escape their own separator, so containment is a prefix test on the string.
function isUnder(ancestorPath, path) {
    return path.length > ancestorPath.length &&
           path.charAt(ancestorPath.length) === '/' &&
           path.indexOf(ancestorPath) === 0;
}

// The chosen row that covers this one, nearest first — an item can sit under two rows.
function coveringPath(path) {
    var nearest = null;
    selectedPaths.forEach(function (chosen) {
        if (isUnder(chosen, path) && (!nearest || chosen.length > nearest.length)) {
            nearest = chosen;
        }
    });
    return nearest;
}

function chosenInside(path) {
    var inside = 0;
    selectedPaths.forEach(function (chosen) {
        if (isUnder(path, chosen)) { inside++; }
    });
    return inside;
}

function markOf(path) {
    if (selectedPaths.has(path)) { return 'sel'; }
    if (coveringPath(path)) { return 'via'; }
    if (chosenInside(path)) { return 'part'; }
    return '';
}

/* ── painting ───────────────────────────────────────────────────────────────────────────── */

function stateText(path, mark) {
    if (mark === 'sel') { return 'Selected'; }
    if (mark === 'via') {
        var owner = coveringPath(path);
        return 'Included via ' + (rowNames[owner] || 'a row above');
    }
    if (mark === 'part') {
        return 'Partially selected · ' + count(chosenInside(path), 'row');
    }
    if (newLibraryPaths.has(path)) {
        return newLibraryPolicy === 'AutoInclude' ? 'Covered from the next run' : 'Not included';
    }
    return '';
}

function repaintTree(page) {
    page.querySelectorAll('#libraryTree .db-row').forEach(function (row) {
        var path = row.getAttribute('data-path');
        var mark = markOf(path);
        var text = stateText(path, mark);

        row.setAttribute('data-m', mark);
        row.querySelector('.db-mark').setAttribute('data-m', mark);

        var state = row.querySelector('.db-state');
        state.textContent = text;
        state.setAttribute('data-on', mark === 'sel' || mark === 'part' ? '1' : '0');
    });
}

/* ── one level of rows ──────────────────────────────────────────────────────────────────── */

function treeUrl(endpoint, path) {
    return pluginUrl('Library/' + endpoint + (path ? '?path=' + encodeURIComponent(path) : ''));
}

// A row found by searching is drawn where it sits rather than where it opens: the tree above it
// is not on screen, so there is nothing to expand into.
function nodeHtml(node, depth, found) {
    var path = esc(node.Path);
    rowNames[node.Path] = node.Name;

    // An episode is named by its title, or by its filename where nothing titled it, and neither
    // says which episode it is. The number leads the row, so a season reads down one column.
    var number = node.Number ? '<span class="db-num">' + esc(node.Number) + '</span>' : '';

    var members = (node.MemberCount > 1 && !found)
        ? '<button type="button" class="db-members" data-members="1">' +
          esc(count(node.MemberCount, 'folder')) + '</button>'
        : '';

    var where = found && node.Location ? '<span class="db-where">' + esc(node.Location) + '</span>' : '';
    var isNew = newLibraryPaths.has(node.Path) ? '<span class="db-new">New library</span>' : '';

    return '<div class="db-node" data-path="' + path + '">' +
             '<div class="db-row" data-path="' + path + '" data-depth="' + depth + '"' +
                  ' style="padding-left: ' + (0.6 + depth * 1.5) + 'rem">' +
               '<span class="db-wedge" data-open="0" data-leaf="' +
                    (node.CanExpand && !found ? '0' : '1') + '">&#9654;</span>' +
               '<span class="db-mark"></span>' +
               number +
               '<span class="db-name">' + esc(node.Name) + '</span>' +
               members + where + isNew +
               '<span class="db-state"></span>' +
             '</div>' +
             '<div class="db-branch" data-branch="children" data-open="0" data-loaded="0"></div>' +
             '<div class="db-branch" data-branch="members" data-open="0" data-loaded="0"></div>' +
           '</div>';
}

function rowsHtml(nodes, depth, found) {
    return nodes.map(function (node) { return nodeHtml(node, depth, found); }).join('');
}

function depthOf(row) {
    return parseInt(row.getAttribute('data-depth'), 10) || 0;
}

/* ── expanding ──────────────────────────────────────────────────────────────────────────── */

function toggleBranch(page, node, which, endpoint) {
    var branch = node.querySelector(':scope > .db-branch[data-branch="' + which + '"]');
    var row = node.querySelector(':scope > .db-row');
    var wedge = row.querySelector('.db-wedge');
    var open = branch.getAttribute('data-open') === '1';

    branch.setAttribute('data-open', open ? '0' : '1');
    if (which === 'children') {
        wedge.setAttribute('data-open', open ? '0' : '1');
    }
    if (open || branch.getAttribute('data-loaded') === '1') {
        return;
    }

    branch.innerHTML = '<div class="db-branch-note">Loading…</div>';
    getJson(treeUrl(endpoint, node.getAttribute('data-path'))).then(function (level) {
        var nodes = (level && level.Nodes) || [];
        branch.setAttribute('data-loaded', '1');
        branch.innerHTML = nodes.length
            ? rowsHtml(nodes, depthOf(row) + 1, false)
            : '<div class="db-branch-note">Nothing inside this one.</div>';
        repaintTree(page);
    }).catch(function () {
        branch.innerHTML = '<div class="db-branch-note">That level could not be read.</div>';
    });
}

/* ── the tree itself ────────────────────────────────────────────────────────────────────── */

function renderTree(page) {
    var tree = page.querySelector('#libraryTree');
    tree.innerHTML = '<div class="db-tree-empty">Reading the library…</div>';

    return getJson(treeUrl('Children', '')).then(function (level) {
        var nodes = (level && level.Nodes) || [];
        tree.innerHTML = nodes.length
            ? rowsHtml(nodes, 0, false)
            : '<div class="db-tree-empty">Jellyfin has no media libraries yet.</div>';
        repaintTree(page);
    }).catch(function () {
        tree.innerHTML = '<div class="db-tree-empty">The library tree could not be read.</div>';
    });
}

function bindTree(page) {
    var tree = page.querySelector('#libraryTree');

    tree.onclick = function (event) {
        var row = event.target.closest('.db-row');
        if (!row) {
            return;
        }
        var node = row.parentNode;

        if (event.target.closest('.db-members')) {
            toggleBranch(page, node, 'members', 'Members');
            return;
        }

        // The mark is the control; the rest of the row opens it. Exactly one path changes per
        // click — nothing above it and nothing below it is touched.
        if (event.target.closest('.db-mark')) {
            var path = row.getAttribute('data-path');
            if (selectedPaths.has(path)) {
                selectedPaths.delete(path);
            } else if (!coveringPath(path)) {
                selectedPaths.add(path);
            }
            repaintTree(page);
            return;
        }

        if (row.querySelector('.db-wedge').getAttribute('data-leaf') !== '1') {
            toggleBranch(page, node, 'children', 'Children');
        }
    };
}

/* ── searching ──────────────────────────────────────────────────────────────────────────── */

// Searching is the server's job: the page holds one level at a time, so filtering here could
// only ever search what someone had already opened.
function runSearch(page, text) {
    var note = page.querySelector('#librarySearchStatus');
    searchTerm = text.trim();
    note.setAttribute('data-bad', '0');

    if (searchTerm.length < 2) {
        note.textContent = searchTerm.length ? 'Two characters at least.' : '';
        if (!searchTerm.length) { renderTree(page); }
        return;
    }

    note.textContent = 'Searching…';
    getJson(pluginUrl('Library/Search?q=' + encodeURIComponent(searchTerm) + '&limit=50'))
        .then(function (result) {
            // An answer to something the user has since typed past is not an answer.
            if (!result || result.Query.trim() !== searchTerm) {
                return;
            }
            var nodes = result.Nodes || [];
            page.querySelector('#libraryTree').innerHTML = nodes.length
                ? rowsHtml(nodes, 0, true)
                : '<div class="db-tree-empty">Nothing in the library matches that.</div>';
            repaintTree(page);

            note.textContent = nodes.length === 0 ? ''
                : result.Complete
                    ? count(nodes.length, 'match', 'matches') + '. Choosing one covers it exactly as it would in the tree.'
                    : count(nodes.length, 'row') + ' shown — there may be more. Narrow the search.';
        })
        .catch(function () {
            note.setAttribute('data-bad', '1');
            note.textContent = 'The search could not be run.';
        });
}

function bindSearch(page) {
    var box = page.querySelector('#txtLibrarySearch');
    box.value = '';
    box.oninput = function () {
        var text = box.value || '';
        if (searchTimer) { clearTimeout(searchTimer); }
        // One request per pause, not per keystroke.
        searchTimer = setTimeout(function () { runSearch(page, text); }, 250);
    };
}

/* ── the stored selection ───────────────────────────────────────────────────────────────── */

// Only what is exceptional is said out loud: the band already carries the count, so the normal
// case is silent and a scope that has gone missing is not.
function renderSelectionNotice(page, coverage) {
    var notice = page.querySelector('#selectionNotice');
    var scopes = (coverage && coverage.Scopes) || [];
    var missing = scopes.filter(function (scope) { return scope.Exists === false; }).length;
    var words = [];

    if (coverage && coverage.CoverNewMedia === false && scopes.length) {
        words.push('Frozen — media added after each row was chosen is not covered.');
    }
    if (missing) {
        words.push(count(missing, 'chosen row') + ' no longer ' + (missing === 1 ? 'exists' : 'exist') +
                   ' in the library; saving will drop ' + (missing === 1 ? 'it' : 'them') + '.');
    }
    notice.textContent = words.join(' ');
}

function loadSelection(page, rebuild) {
    return Promise.all([
        getPlugin('Selection'),
        getPlugin('Selection/Coverage').catch(function () { return null; })
    ]).then(function (results) {
        var selection = results[0];

        selectedPaths = new Set((selection.Scopes || []).map(function (scope) { return scope.Path; }));
        savedPaths = new Set(selectedPaths);
        newLibraryPaths = new Set(selection.NewLibraryPaths || []);

        page.querySelector('#chkCoverNewMedia').checked = selection.CoverNewMedia !== false;
        renderSelectionNotice(page, results[1]);

        return rebuild ? renderTree(page) : repaintTree(page);
    });
}

function saveSelection(page) {
    var chosen = [];
    selectedPaths.forEach(function (path) { chosen.push(path); });

    return Promise.resolve(ApiClient.ajax({
        type: 'PUT',
        url: pluginUrl('Selection'),
        contentType: 'application/json',
        data: JSON.stringify({
            Paths: chosen,
            CoverNewMedia: page.querySelector('#chkCoverNewMedia').checked
        })
    })).then(function (stored) {
        // The response is canonical, but so is where it sits in the tree — so the page re-reads
        // both rather than patching what it hoped it saved.
        return loadSelection(page, false).then(function () { return stored; });
    });
}
