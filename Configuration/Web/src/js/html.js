/*
 * Building markup as text, safely.
 *
 * The tree and the records table are built as strings because they are long lists rebuilt whole;
 * everything that comes from the library goes through esc() on the way in. A filename is not
 * trusted input — it is whatever a torrent was called.
 */

function esc(text) {
    return String(text === null || text === undefined ? '' : text)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

// "35" / "1 item" / "35 items" — the count and its noun, agreeing.
function count(n, noun, plural) {
    return n + ' ' + (n === 1 ? noun : (plural || noun + 's'));
}
