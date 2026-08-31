/*
 * "And what about things that arrive later?"
 *
 * CoverNewMedia, NewMediaTrigger, NewLibraryPolicy and the settle window are four settings that
 * answer that one question, so they read as one sentence under the tree rather than as four
 * controls in three sections. Only the behaviour lives here — the values are part of the plugin
 * configuration document, which config.js owns.
 */

function showSettleWindow(page) {
    var settled = page.querySelector('#selNewMediaTrigger').value === 'WhenSettled';
    page.querySelector('#coverRules').setAttribute('data-settled', settled ? '1' : '0');
}

function bindCoverage(page) {
    var trigger = page.querySelector('#selNewMediaTrigger');
    trigger.onchange = function () { showSettleWindow(page); };

    // The policy decides what a library badged "new" is going to mean, so the tree says so as
    // soon as it changes rather than after a save.
    page.querySelector('#selNewLibraryPolicy').onchange = function () {
        newLibraryPolicy = this.value;
        repaintTree(page);
    };

    showSettleWindow(page);
}
