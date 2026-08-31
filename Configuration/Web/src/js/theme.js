/*
 * Reads the page's palette back off the theme Jellyfin actually rendered.
 *
 * Jellyfin publishes no CSS custom properties to inherit. Its six built-in themes are
 * class-based CSS that sets colours directly on component classes, and they differ in accent as
 * well as in ground — Purple Haze's submit button is #48c3c8 where every other theme's is
 * #00a4dc. Any palette written into this plugin is therefore wrong on at least one shipped theme,
 * and wrong on every theme nobody has written yet.
 *
 * So: render one hidden element per Jellyfin component class, take its computed colour, and
 * define our own tokens from what comes back. Nothing in this file names a theme, and the two
 * colours it does name are a starting point for a contrast walk, not a palette.
 *
 * Self-contained on purpose. It publishes CSS custom properties and nothing else, so no other
 * part of the page has to call it or know it ran.
 */
(function () {
    'use strict';

    var page = document.getElementById('dialogueBoostConfigPage');
    if (!page) {
        return;
    }

    /*
     * Every token that can be read straight off a Jellyfin class. Each row is
     * [ token, tag, class, computed property ] — the class list is the contract with Jellyfin,
     * and it is the whole reason the page needs no colour of its own.
     */
    var SWATCHES = [
        ['--db-accent',     'button', 'button-submit',    'backgroundColor'],
        ['--db-accent-ink', 'button', 'button-submit',    'color'],
        ['--db-raised',     'button', 'raised',           'backgroundColor'],
        ['--db-danger',     'button', 'button-delete',    'backgroundColor'],
        ['--db-danger-ink', 'button', 'button-delete',    'color'],
        ['--db-surface',    'div',    'paperList',        'backgroundColor'],
        ['--db-muted',      'div',    'fieldDescription', 'color'],
        ['--db-label',      'label',  'inputLabel',       'color'],
        ['--db-input',      'input',  'emby-input',       'backgroundColor'],
        ['--db-input-bd',   'input',  'emby-input',       'borderTopColor']
    ];

    /* Shape is themed too: Purple Haze rounds its cards to .8em where the dark theme squares them. */
    var RADII = [
        ['--db-radius',      'input', 'emby-input', 'borderTopLeftRadius'],
        ['--db-radius-card', 'div',   'paperList',  'borderTopLeftRadius']
    ];

    /* Where a contrast walk starts when Jellyfin states no colour at all: amber, for warnings. */
    var AMBER = [224, 160, 48];

    /* WCAG AA for body text. Below this the accent is not usable as a label, only as a fill. */
    var READABLE = 4.5;

    /* ── colour arithmetic ──────────────────────────────────────────────────────────────── */

    // Computed colours serialise as rgb()/rgba() whatever the stylesheet wrote, so pulling the
    // numbers out is enough — no need to know hex from hsla.
    function parse(value) {
        var nums = value ? value.match(/[-+]?[0-9]*\.?[0-9]+/g) : null;
        if (!nums || nums.length < 3) {
            return null;
        }
        var alpha = nums.length > 3 ? parseFloat(nums[3]) : 1;
        if (!isFinite(alpha)) {
            alpha = 1;
        }
        return [parseFloat(nums[0]), parseFloat(nums[1]), parseFloat(nums[2]), alpha > 1 ? alpha / 100 : alpha];
    }

    function css(rgba) {
        var r = Math.round(rgba[0]), g = Math.round(rgba[1]), b = Math.round(rgba[2]);
        return rgba[3] >= 1
            ? 'rgb(' + r + ', ' + g + ', ' + b + ')'
            : 'rgba(' + r + ', ' + g + ', ' + b + ', ' + Math.round(rgba[3] * 1000) / 1000 + ')';
    }

    function alpha(rgba, a) {
        return [rgba[0], rgba[1], rgba[2], a];
    }

    // A translucent token is left translucent — that is how Jellyfin means it to sit over the
    // ground — but the contrast maths needs something opaque, so flatten a copy.
    function over(top, bottom) {
        var a = top[3];
        if (a >= 1) {
            return [top[0], top[1], top[2], 1];
        }
        return [
            top[0] * a + bottom[0] * (1 - a),
            top[1] * a + bottom[1] * (1 - a),
            top[2] * a + bottom[2] * (1 - a),
            1
        ];
    }

    function toward(rgba, target, amount) {
        return [
            rgba[0] + (target - rgba[0]) * amount,
            rgba[1] + (target - rgba[1]) * amount,
            rgba[2] + (target - rgba[2]) * amount,
            rgba[3]
        ];
    }

    function luminance(rgba) {
        var lin = [0, 1, 2].map(function (i) {
            var v = rgba[i] / 255;
            return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
        });
        return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2];
    }

    function contrast(a, b) {
        var la = luminance(a), lb = luminance(b);
        return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
    }

    // Walk a colour away from the ground until it is legible on it. Which way is away depends on
    // the ground, which is the whole point: the same call darkens the accent on Apple TV and
    // lightens it on Purple Haze.
    function legible(colour, ground) {
        var pole = luminance(ground) > 0.4 ? 0 : 255;
        var out = colour;
        for (var step = 1; step <= 20 && contrast(out, ground) < READABLE; step++) {
            out = toward(colour, pole, step * 0.05);
        }
        return out;
    }

    /* ── the probe ──────────────────────────────────────────────────────────────────────── */

    function readSwatches(rows, host) {
        var seen = {};
        var out = {};
        rows.forEach(function (row) {
            var key = row[1] + '.' + row[2];
            if (!seen[key]) {
                var el = document.createElement(row[1]);
                el.className = row[2];
                host.appendChild(el);
                seen[key] = getComputedStyle(el);
            }
            out[row[0]] = seen[key][row[3]];
        });
        return out;
    }

    function apply() {
        // Rendered rather than display:none, and off-screen rather than hidden, so every value
        // below is one the browser actually resolved.
        var host = document.createElement('div');
        host.setAttribute('aria-hidden', 'true');
        host.style.cssText = 'position:absolute;left:-9999px;top:0;width:0;height:0;overflow:hidden;pointer-events:none';
        page.appendChild(host);

        var raw, radii;
        try {
            raw = readSwatches(SWATCHES, host);
            radii = readSwatches(RADII, host);
        } finally {
            host.parentNode.removeChild(host);
        }

        var root = getComputedStyle(document.documentElement);
        var ground = parse(root.backgroundColor);
        var fg = parse(root.color);
        if (!ground || ground[3] === 0 || !fg) {
            return;                                  // no theme to read; the CSS fallback stands
        }
        ground = over(ground, [0, 0, 0, 1]);

        var set = {};
        set['--db-bg'] = ground;
        set['--db-fg'] = fg;
        set['--db-fg-strong'] = alpha(fg, 1);

        Object.keys(raw).forEach(function (token) {
            var colour = parse(raw[token]);
            if (colour && colour[3] > 0) {
                set[token] = colour;
            }
        });

        // A theme that styles none of these still gets a page: lift a surface off the ground by
        // the smallest amount that stays visible, and rule it with the foreground.
        var lift = function (amount) { return alpha(fg, amount); };
        if (!set['--db-surface']) { set['--db-surface'] = lift(0.05); }
        if (!set['--db-raised']) { set['--db-raised'] = lift(0.1); }
        if (!set['--db-input']) { set['--db-input'] = lift(0.07); }
        if (!set['--db-input-bd']) { set['--db-input-bd'] = lift(0.13); }
        if (!set['--db-muted']) { set['--db-muted'] = alpha(fg, 0.55); }
        if (!set['--db-label']) { set['--db-label'] = alpha(fg, 0.75); }

        set['--db-rule'] = lift(0.13);
        set['--db-hover'] = lift(0.07);
        set['--db-shade'] = lift(0.04);

        var accent = set['--db-accent'] ? over(set['--db-accent'], ground) : null;
        if (accent) {
            set['--db-accent-text'] = legible(accent, ground);
            set['--db-accent-soft'] = alpha(accent, 0.13);
        }

        var warn = legible(AMBER.concat(1), ground);
        set['--db-warn'] = warn;
        set['--db-warn-soft'] = alpha(warn, 0.14);

        Object.keys(set).forEach(function (token) {
            page.style.setProperty(token, css(set[token]));
        });

        ['--db-radius', '--db-radius-card'].forEach(function (token) {
            var value = radii[token];
            if (value && /^[0-9.]+/.test(value)) {
                page.style.setProperty(token, value);
            }
        });
    }

    apply();

    // Jellyfin keeps the page element and re-shows it, so a theme changed in between would
    // otherwise stay unread until a reload.
    page.addEventListener('pageshow', apply);
})();
