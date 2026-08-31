/*
 * Tab switching, and nothing else.
 *
 * The panels are all in the page from the start — they are three views of one form, and one
 * submit saves all of them, so hiding is the whole mechanism.
 */

function showPanel(page, name) {
    page.querySelectorAll('.db-tab').forEach(function (tab) {
        tab.setAttribute('aria-selected', String(tab.getAttribute('data-panel') === name));
    });
    page.querySelectorAll('.db-panel').forEach(function (panel) {
        panel.setAttribute('data-on', panel.getAttribute('data-panel') === name ? '1' : '0');
    });
}

function bindTabs(page) {
    var tabs = Array.prototype.slice.call(page.querySelectorAll('.db-tab'));

    tabs.forEach(function (tab, index) {
        tab.onclick = function () {
            showPanel(page, tab.getAttribute('data-panel'));
        };

        // A tablist is arrow-navigable, which is the only reason these are buttons in a row
        // rather than links.
        tab.onkeydown = function (event) {
            var step = event.key === 'ArrowRight' ? 1 : (event.key === 'ArrowLeft' ? -1 : 0);
            if (!step) {
                return;
            }
            event.preventDefault();
            var next = tabs[(index + step + tabs.length) % tabs.length];
            next.focus();
            next.click();
        };
    });
}
