/*
 * Which code to write in a "process these languages" field.
 *
 * ISO 639 spells most languages two ways and twenty of them three, and most people have met one
 * spelling. Someone who types `eng` is right and so is someone who types `en`; someone who types
 * `ger` is right and so is `deu` and `de`, because the plugin normalises every spelling to one
 *. Nothing on the page said so. So the question the hint answers is not "what is the code"
 * but "is the one I know good enough".
 *
 * The second half of it is `und`, which is not a language and is easy to read as a typo. It is
 * ISO 639-2 for *undetermined*, and it is what a track with no language tag at all normalises to
 * — so it is the only way to name those tracks in a rule, and worth a clause rather than a bare
 * mention. Checked: `Covers({und}, null)` and `Covers({und}, "")` are both true.
 *
 * The list is fetched rather than written here. A copy of ISO 639 in the browser could name a code
 * LanguageCodes does not map, and an unmapped code fails silently: it is compared as itself, and
 * matches nothing. Every code below came out of the same class the run compares with.
 */

// Fetched once and shared by all five fields; the fields are identical and the answer is static.
var languageHint = null;

function languageHintMarkup(languages) {
    var rows = languages.map(function (language) {
        return '<div class="db-lang"><span class="db-lang-name">' + esc(language.Name) + '</span>' +
            '<span class="db-lang-codes">' + esc((language.Codes || []).join('  ')) + '</span></div>';
    }).join('');

    return '<p class="db-hint-lead">Any spelling of a language works and they all mean the same ' +
        'thing — <b>en</b> and <b>eng</b> are both English, and some languages have a third ' +
        'spelling as well. Every one this plugin accepts is listed below.</p>' +
        '<p class="db-hint-lead">A track that carries no language tag counts as <b>und</b>, the ' +
        'ISO code for <i>undetermined</i>. Write <b>und</b> to include those tracks; leave the ' +
        'field empty to take every language.</p>' +
        '<div class="db-langs">' + rows + '</div>';
}

function renderLanguageHints(page) {
    var slots = page.querySelectorAll('[data-language-codes]');
    if (!slots.length || languageHint === null) {
        return;
    }
    slots.forEach(function (slot) {
        slot.innerHTML = languageHint;
    });
}

// Asked for once per page load, and a failure costs the hint and nothing else: the field takes
// the same codes either way, so the <details> is left closed and empty rather than showing an
// error the user cannot act on.
function loadLanguageHints(page) {
    if (languageHint !== null) {
        renderLanguageHints(page);
        return Promise.resolve();
    }

    return getPlugin('Languages').then(function (languages) {
        languageHint = languageHintMarkup(languages || []);
        renderLanguageHints(page);
    }).catch(function () {
        page.querySelectorAll('.db-hint').forEach(function (hint) {
            hint.hidden = true;
        });
    });
}
