#!/usr/bin/env python3
"""Drive the plugin's config page inside the real Jellyfin web client.

The screenshot harness renders the *assembled* `config.html` as a whole document
with stubs standing in for `$`, `ApiClient`, `Dashboard`, `Events` and
`TaskButton`. That measures layout and colour, but every stub is a claim about
Jellyfin rather than a measurement of it, nothing is ever clicked, and a page
that throws after first paint still screenshots clean.

This logs in as a real user, lets Jellyfin's own router load the page, and reads
the result out of the live DOM — so what it reports is what the client does, not
what the harness was told to do. It is how the dropped-stylesheet bug was found: Jellyfin keeps only the
`data-role="page"` element and discards the rest of the document, so a `<style>`
in `<head>` never reaches the browser.

    JELLYFIN_USER=… JELLYFIN_PASS=… ./scripts/probe-config-page.py [--shot out.png]

Credentials come from the environment, never from this repo. JELLYFIN_URL
defaults to http://localhost:8096. Needs `geckodriver` and `python-selenium`.

Import it to write a one-off check:

    import sys; sys.path.insert(0, 'scripts')
    from importlib import import_module
    jf = import_module('probe-config-page')
    with jf.session() as d:
        print(jf.computed(d, '.db-head', 'display'))
"""

import argparse
import contextlib
import os
import sys

from selenium import webdriver
from selenium.common.exceptions import TimeoutException
from selenium.webdriver.common.by import By
from selenium.webdriver.firefox.options import Options
from selenium.webdriver.firefox.service import Service
from selenium.webdriver.support import expected_conditions as EC
from selenium.webdriver.support.ui import WebDriverWait

BASE = os.environ.get("JELLYFIN_URL", "http://localhost:8096")
PAGE = "Dialogue Boost"
ROOT = "dialogueBoostConfigPage"

# Firefox must not reach anything but the server under test: OpenSnitch prompts
# per application, and an unanswered prompt looks exactly like a hang.
OFFLINE_PREFS = {
    "network.dns.disablePrefetch": True,
    "network.prefetch-next": False,
    "network.captive-portal-service.enabled": False,
    "network.trr.mode": 5,
    "browser.safebrowsing.malware.enabled": False,
    "browser.safebrowsing.phishing.enabled": False,
    "browser.safebrowsing.downloads.enabled": False,
    "browser.search.update": False,
    "app.update.enabled": False,
    "extensions.update.enabled": False,
    "extensions.systemAddon.update.enabled": False,
    "browser.discovery.enabled": False,
    "datareporting.healthreport.uploadEnabled": False,
    "toolkit.telemetry.enabled": False,
    "browser.newtabpage.enabled": False,
    "browser.aboutwelcome.enabled": False,
    "browser.startup.homepage": "about:blank",
    "services.settings.server": "",
    "browser.region.network.url": "",
    "media.gmp-manager.url": "",
}


def build(width=1400, height=1800):
    """A headless Firefox that talks to nothing but the server under test."""
    os.environ.setdefault("SE_AVOID_STATS", "true")  # no Plausible ping
    opts = Options()
    opts.add_argument("-headless")
    opts.add_argument(f"--width={width}")
    opts.add_argument(f"--height={height}")
    for key, value in OFFLINE_PREFS.items():
        opts.set_preference(key, value)
    # Naming geckodriver skips Selenium Manager, which otherwise round-trips to
    # GitHub to pick a version it already has.
    return webdriver.Firefox(
        options=opts,
        service=Service(executable_path="/usr/bin/geckodriver", log_output="/dev/null"),
    )


def login(driver, wait=30):
    """Log in through the real form.

    The manual fields are in the DOM from the start but hidden until 'Manual
    Login' is clicked, so this tests visibility rather than presence.
    """
    driver.get(f"{BASE}/web/index.html#/login")
    waiter = WebDriverWait(driver, wait)
    waiter.until(EC.presence_of_element_located((By.ID, "txtManualName")))
    if not driver.find_element(By.ID, "txtManualName").is_displayed():
        driver.find_element(By.CSS_SELECTOR, ".btnManual").click()
    user = waiter.until(EC.visibility_of_element_located((By.ID, "txtManualName")))
    password = driver.find_element(By.ID, "txtManualPassword")
    user.clear()
    user.send_keys(os.environ["JELLYFIN_USER"])
    password.clear()
    password.send_keys(os.environ["JELLYFIN_PASS"])
    form = driver.execute_script(
        "return document.getElementById('txtManualName').closest('form')")
    form.find_element(By.CSS_SELECTOR, "button[type=submit], .button-submit").click()
    waiter.until(lambda d: "/login" not in d.current_url)


def trap_console(driver):
    """Start collecting console output and uncaught errors.

    Call this before `open_page`: the config page arrives by hash route with no
    document reload, so a trap installed now is in place before its scripts run.
    """
    driver.execute_script(r"""
      window.__log = [];
      for (const level of ['log', 'info', 'warn', 'error']) {
        const original = console[level].bind(console);
        console[level] = (...args) => {
          window.__log.push([level, args.map(String).join(' ')]);
          original(...args);
        };
      }
      addEventListener('error', e => window.__log.push(['uncaught',
        `${e.message} @ ${e.filename || '?'}:${e.lineno}` +
        (e.error && e.error.stack ? `\n    ${e.error.stack.split('\n').join('\n    ')}` : '')]));
      addEventListener('unhandledrejection', e => window.__log.push(
        ['rejection', String(e.reason)]));
    """)


def drain(driver):
    return driver.execute_script("return window.__log || []")


def open_page(driver, wait=30):
    name = PAGE.replace(" ", "%20")
    driver.get(f"{BASE}/web/index.html#/configurationpage?name={name}")
    WebDriverWait(driver, wait).until(EC.presence_of_element_located((By.ID, ROOT)))


def settle(driver, timeout=20):
    """Wait for the band to carry an answer rather than its placeholder.

    The page fills itself from several calls, so the root element exists well
    before the numbers do — reading immediately reports a cold server as an empty
    page. WebDriverWait polls; that is Selenium's only mechanism, and this is a
    harness rather than anything that ships.
    """
    try:
        WebDriverWait(driver, timeout).until(
            lambda d: d.execute_script(
                "const e = document.getElementById('bandCovered');"
                "return e && !['', '\u2014'].includes(e.textContent.trim());"))
    except TimeoutException:
        pass  # report what is actually there; an empty band may be the finding


def read(driver, element_id):
    """Text plus whether it is on screen — Selenium's .text hides both as ''."""
    return driver.execute_script("""
      const e = document.getElementById(arguments[0]);
      if (!e) return null;
      return {text: e.textContent.trim(), shown: !!e.offsetParent || e.offsetHeight > 0};
    """, element_id)


@contextlib.contextmanager
def session(**kwargs):
    """A logged-in browser sitting on the config page, console already trapped."""
    driver = build(**kwargs)
    try:
        login(driver)
        trap_console(driver)
        open_page(driver)
        settle(driver)
        yield driver
    finally:
        driver.quit()


def computed(driver, selector, prop):
    """A resolved CSS value off the live element — measured, not read off source."""
    return driver.execute_script(
        "const e = document.querySelector(arguments[0]);"
        "return e ? getComputedStyle(e)[arguments[1]] : null;", selector, prop)


def tokens(driver):
    """Every `--db-*` token as the active theme resolved it.

    They are published onto the page element by js/theme.js, not onto :root.
    """
    return driver.execute_script("""
      const page = document.getElementById(arguments[0]);
      const style = getComputedStyle(page);
      const out = {};
      for (const sheet of document.styleSheets) {
        let rules; try { rules = sheet.cssRules; } catch (e) { continue; }
        for (const rule of rules || []) {
          for (const name of (rule.style ? [...rule.style] : []))
            if (name.startsWith('--db-')) out[name] = style.getPropertyValue(name).trim();
        }
      }
      for (const name of [...page.style]) 
        if (name.startsWith('--db-')) out[name] = style.getPropertyValue(name).trim();
      return out;
    """, ROOT)


def stylesheet_reached_browser(driver):
    """Whether the page's own CSS is live in the document.

    Jellyfin keeps only the `data-role="page"` element of a plugin page, so CSS
    parked in `<head>` is dropped without a warning anywhere.
    """
    return driver.execute_script("""
      const inline = [...document.querySelectorAll('style')]
        .some(s => s.textContent.includes('--db-'));
      let inSheets = false;
      for (const sheet of document.styleSheets) {
        let rules; try { rules = sheet.cssRules; } catch (e) { continue; }
        for (const rule of rules || [])
          if (rule.cssText && rule.cssText.includes('--db-')) inSheets = true;
      }
      return inline || inSheets;
    """)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--shot", metavar="PATH", help="write a screenshot here")
    parser.add_argument("--width", type=int, default=1400)
    parser.add_argument("--height", type=int, default=1800)
    args = parser.parse_args()

    for var in ("JELLYFIN_USER", "JELLYFIN_PASS"):
        if not os.environ.get(var):
            sys.exit(f"{var} is not set — credentials come from the environment.")

    with session(width=args.width, height=args.height) as driver:
        print(f"url        {driver.current_url}")
        css_live = stylesheet_reached_browser(driver)
        print(f"stylesheet {'live in the document' if css_live else 'MISSING'}")

        tabs = [b.text for b in driver.find_elements(By.CSS_SELECTOR, ".tabButton, [role=tab]")]
        print(f"tabs       {[t for t in tabs if t]}")

        print("band")
        for element_id in ("bandCovered", "bandWritten", "bandNext", "bandStorage", "bandPct"):
            cell = read(driver, element_id)
            if cell is None:
                print(f"  {element_id:<14} MISSING")
            else:
                print(f"  {element_id:<14} {cell['text']!r}"
                      f"{'' if cell['shown'] else '  (hidden)'}")

        print("tokens")
        for name, value in sorted(tokens(driver).items()):
            print(f"  {name:<18} {value}")

        log = drain(driver)
        bad = [m for m in log if m[0] in ("error", "uncaught", "rejection")]
        print(f"console    {len(log)} messages, {len(bad)} errors")
        for level, message in bad:
            print(f"  ! {level}: {message}")  # stacks in full: the head names
                                              # Jellyfin's bundle, not the caller

        if args.shot:
            driver.save_screenshot(args.shot)
            print(f"screenshot {args.shot}")


if __name__ == "__main__":
    main()
