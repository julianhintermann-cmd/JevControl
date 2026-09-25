'use strict';
/*
 * End-to-end test of the Kairo Browser Bridge extension:
 *
 *   test (FakeDesktop, TCP)  <-- stub-host.cjs (native messaging host) <-- extension (Chromium)
 *
 * Chromium (Playwright's full build, new headless mode) loads the unpacked extension. The
 * native messaging host manifest for "com.kairo.bridge" is registered in
 * <userDataDir>/NativeMessagingHosts, pointing to stub-host.cjs, which relays frames
 * unchanged to the fake desktop app – the same role Kairo.BrowserHost.exe plays on Windows.
 */

const { describe, test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  EXTENSION_DIR,
  EXTENSION_ID,
  FIXTURES_DIR,
  FakeDesktop,
  loadPlaywright,
  startStaticServer,
} = require('./helpers.cjs');

const { chromium } = loadPlaywright();
const STUB_HOST = path.join(__dirname, 'stub-host.cjs');

let context = null;
let serviceWorker = null;
let desktop = null;
let server = null;
let userDataDir = null;
let skipReason = null;
let formUrl = '';
let formTabId = null;
let formWindowId = null;
let snap = null;

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function registerNativeHost(dir) {
  const hostsDir = path.join(dir, 'NativeMessagingHosts');
  fs.mkdirSync(hostsDir, { recursive: true });
  fs.chmodSync(STUB_HOST, 0o755);
  fs.writeFileSync(
    path.join(hostsDir, 'com.kairo.bridge.json'),
    JSON.stringify(
      {
        name: 'com.kairo.bridge',
        description: 'Kairo Browser Bridge (test stub)',
        path: STUB_HOST,
        type: 'stdio',
        allowed_origins: [`chrome-extension://${EXTENSION_ID}/`],
      },
      null,
      2,
    ),
  );
}

async function launch(port) {
  const options = {
    headless: true,
    args: [`--disable-extensions-except=${EXTENSION_DIR}`, `--load-extension=${EXTENSION_DIR}`],
    env: { ...process.env, KAIRO_STUB_PORT: String(port) },
    viewport: { width: 1200, height: 800 },
  };
  // The headless shell cannot run extensions; the full Chromium build in new headless mode can.
  try {
    return await chromium.launchPersistentContext(userDataDir, { ...options, channel: 'chromium' });
  } catch (e) {
    return chromium.launchPersistentContext(userDataDir, options);
  }
}

before(async () => {
  server = await startStaticServer(FIXTURES_DIR);
  formUrl = `${server.url}/contact-form.html`;
  desktop = new FakeDesktop();
  const port = await desktop.listen();
  userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'kairo-ext-e2e-'));
  registerNativeHost(userDataDir);
  try {
    context = await launch(port);
    [serviceWorker] = context.serviceWorkers();
    if (!serviceWorker) {
      serviceWorker = await context.waitForEvent('serviceworker', { timeout: 15000 });
    }
  } catch (e) {
    skipReason = `Chromium with extensions could not be started here: ${e.message.split('\n')[0]}`;
  }
});

after(async () => {
  if (context) await context.close().catch(() => {});
  if (desktop) await desktop.close().catch(() => {});
  if (server) await server.close();
  if (userDataDir) fs.rmSync(userDataDir, { recursive: true, force: true });
});

/** Wrapper that skips (with a reason) instead of failing when the browser is unavailable. */
function e2e(name, fn) {
  test(name, async (t) => {
    if (skipReason) {
      t.skip(skipReason);
      return;
    }
    await fn(t);
  });
}

function pageForUrl(url) {
  return context.pages().find((p) => p.url() === url);
}

function byLabel(s, label) {
  const el = s.elements.find((e) => e.label === label);
  assert.ok(el, `element "${label}" not in snapshot; labels: ${s.elements.map((e) => e.label).join(' | ')}`);
  return el;
}

describe('extension end-to-end (native messaging)', () => {
  e2e('loads with the fixed extension id', async () => {
    const url = new URL(serviceWorker.url());
    assert.equal(url.protocol, 'chrome-extension:');
    assert.equal(url.host, EXTENSION_ID);
    assert.equal(url.pathname, '/background.js');
  });

  e2e('sends hello right after connecting', async () => {
    const hello = await desktop.waitFor((m) => m.type === 'hello', 20000, 'hello');
    assert.equal(hello.protocol, 1);
    assert.equal(hello.extensionVersion, '1.0.0');
    assert.ok(['chrome', 'edge', 'chromium'].includes(hello.browser), hello.browser);
    assert.match(hello.userAgent, /Chrome\//);
    assert.equal(desktop.connections, 1);
  });

  e2e('ping and unknown methods', async () => {
    const before = Date.now();
    const pong = await desktop.call('ping');
    assert.equal(pong.pong, true);
    assert.ok(pong.time >= before - 5000 && pong.time <= Date.now() + 5000);

    const unknown = await desktop.request('doSomethingElse');
    assert.equal(unknown.type, 'response');
    assert.equal(unknown.ok, false);
    assert.equal(unknown.error.code, 'not_supported');
  });

  e2e('listWindows', async () => {
    const { windows } = await desktop.call('listWindows');
    assert.ok(windows.length >= 1);
    const w = windows[0];
    for (const key of ['windowId', 'focused', 'state', 'type', 'left', 'top', 'width', 'height', 'activeTabId', 'tabs']) {
      assert.ok(key in w, `window.${key}`);
    }
    assert.ok(w.tabs.length >= 1);
    for (const key of ['tabId', 'index', 'title', 'url', 'active', 'status']) {
      assert.ok(key in w.tabs[0], `tab.${key}`);
    }
  });

  e2e('openTab loads the page and emits events', async () => {
    const r = await desktop.call('openTab', { url: formUrl });
    assert.ok(Number.isInteger(r.tabId));
    assert.ok(Number.isInteger(r.windowId));
    formTabId = r.tabId;
    formWindowId = r.windowId;

    const nav = await desktop.waitFor(
      (m) => m.type === 'event' && m.event === 'navigationCompleted' && m.tabId === formTabId,
      10000,
      'navigationCompleted',
    );
    assert.equal(nav.url, formUrl);
    assert.equal(nav.windowId, formWindowId);
    await desktop.waitFor(
      (m) => m.type === 'event' && m.event === 'tabUpdated' && m.tabId === formTabId && m.title === 'Kontakt – Muster AG',
      10000,
      'tabUpdated with title',
    );
    await desktop.waitFor(
      (m) => m.type === 'event' && m.event === 'tabActivated' && m.tabId === formTabId,
      10000,
      'tabActivated',
    );
    const { windows } = await desktop.call('listWindows');
    const tab = windows.flatMap((w) => w.tabs).find((t) => t.tabId === formTabId);
    assert.equal(tab.url, formUrl);
    assert.equal(tab.status, 'complete');
    assert.equal(tab.active, true);
  });

  e2e('snapshot merges frames with frame-prefixed kids', async () => {
    snap = await desktop.call('snapshot', { tabId: formTabId });
    assert.equal(snap.tabId, formTabId);
    assert.equal(snap.windowId, formWindowId);
    assert.equal(snap.url, formUrl);
    assert.equal(snap.title, 'Kontakt – Muster AG');
    assert.equal(snap.frames, 2, 'top frame + srcdoc iframe');
    assert.equal(snap.truncated, false);
    assert.equal(snap.viewport.innerWidth, 1200);
    assert.ok(snap.texts.includes('Kontaktformular'));
    assert.ok(snap.texts.includes('Bitte füllen Sie alle Pflichtfelder aus.'));
    assert.ok(snap.texts.includes('Gutschein einlösen'), 'texts from the iframe');

    for (const e of snap.elements) {
      assert.match(e.kid, /^\d+:k\d+$/);
      assert.equal(Number(e.kid.split(':')[0]), e.frameId);
    }
    const vorname = byLabel(snap, 'Vorname');
    assert.equal(vorname.frameId, 0);
    assert.equal(vorname.required, true);
    assert.equal(vorname.section, 'Persönliche Angaben');
    assert.equal(byLabel(snap, 'E-Mail-Adresse').autocomplete, 'email');
    assert.equal(byLabel(snap, 'Passwort').isPassword, true);
    assert.equal(snap.elements.find((e) => e.name === 'csrf'), undefined);

    const code = byLabel(snap, 'Gutscheincode');
    assert.notEqual(code.frameId, 0);
    assert.equal(code.kid, `${code.frameId}:k1`);

    // iframe rects are translated into top-level viewport coordinates
    const page = pageForUrl(formUrl);
    const frameBox = await page.locator('#gutschein-frame').boundingBox();
    const innerBox = await page.frameLocator('#gutschein-frame').locator('#code').boundingBox();
    assert.ok(Math.abs(code.rect.x - innerBox.x) <= 1, `x ${code.rect.x} vs ${innerBox.x}`);
    assert.ok(Math.abs(code.rect.y - innerBox.y) <= 1, `y ${code.rect.y} vs ${innerBox.y}`);
    assert.ok(code.rect.y >= frameBox.y);

    // ids are stable across snapshots
    const again = await desktop.call('snapshot', { tabId: formTabId });
    assert.deepEqual(again.elements.map((e) => e.kid), snap.elements.map((e) => e.kid));

    // default tab = active tab of the last focused window
    const dflt = await desktop.call('snapshot', { maxElements: 5, includeText: false });
    assert.equal(dflt.tabId, formTabId);
    assert.equal(dflt.elements.length, 5);
    assert.equal(dflt.truncated, true);
    assert.deepEqual(dflt.texts, []);
  });

  e2e('actBatch fills the form (incl. iframe and React-like field) in one round trip', async () => {
    const k = (label) => byLabel(snap, label).kid;
    const actions = [
      { kid: k('Vorname'), action: 'fill', value: 'Anna' },
      { kid: k('Nachname'), action: 'fill', value: 'Muster' },
      { kid: k('E-Mail-Adresse'), action: 'fill', value: 'anna.muster@example.com' },
      { kid: k('Land'), action: 'select', value: 'deutschland' },
      { kid: k('Benutzername'), action: 'fill', value: 'anna.m' },
      { kid: '0:k99999', action: 'fill', value: 'gibt es nicht' },
      { kid: 'kaputt', action: 'fill', value: 'x' },
      { kid: k('Gutscheincode'), action: 'fill', value: 'SOMMER2026' },
      { kid: k('Nachricht'), action: 'fill', value: 'Bitte um Rückruf.' },
      { kid: k('Ich akzeptiere die Datenschutzerklärung'), action: 'check' },
      { kid: k('Passwort'), action: 'fill', value: 'Geheim-123' },
    ];
    const { results } = await desktop.call('actBatch', { tabId: formTabId, actions });
    assert.equal(results.length, actions.length);
    results.forEach((r, i) => {
      assert.equal(r.kid, actions[i].kid);
      if (i === 5) {
        assert.equal(r.ok, false);
        assert.equal(r.error.code, 'element_not_found');
      } else if (i === 6) {
        assert.equal(r.ok, false);
        assert.equal(r.error.code, 'bad_request');
      } else {
        assert.equal(r.ok, true, `item ${i}: ${JSON.stringify(r.error)}`);
        assert.equal(r.error, null);
      }
    });
    assert.equal(results[3].value, 'DE');
    assert.equal(results[7].value, 'SOMMER2026');
    assert.equal(results[9].checked, true);
    assert.equal(results[10].value, '••••');

    const page = pageForUrl(formUrl);
    assert.equal(await page.inputValue('#vorname'), 'Anna');
    assert.equal(await page.inputValue('input[name=nachname]'), 'Muster');
    assert.equal(await page.inputValue('#email'), 'anna.muster@example.com');
    assert.equal(await page.inputValue('select[name=land]'), 'DE');
    assert.equal(await page.inputValue('#nachricht'), 'Bitte um Rückruf.');
    assert.equal(await page.inputValue('#passwort'), 'Geheim-123');
    assert.equal(await page.isChecked('input[name=datenschutz]'), true);
    assert.equal(await page.evaluate(() => window.__reactState.username), 'anna.m');
    assert.equal(await page.frameLocator('#gutschein-frame').locator('#code').inputValue(), 'SOMMER2026');
  });

  e2e('readValues returns current values, masks passwords', async () => {
    const kids = ['Vorname', 'Land', 'Passwort', 'Gutscheincode', 'Newsletter abonnieren'].map(
      (l) => byLabel(snap, l).kid,
    );
    const { values } = await desktop.call('readValues', { tabId: formTabId, kids: [...kids, '0:k77777', 'nope'] });
    assert.deepEqual(values[kids[0]], { exists: true, value: 'Anna', checked: null });
    assert.deepEqual(values[kids[1]], { exists: true, value: 'DE', checked: null });
    assert.deepEqual(values[kids[2]], { exists: true, value: '••••', checked: null });
    assert.deepEqual(values[kids[3]], { exists: true, value: 'SOMMER2026', checked: null });
    assert.deepEqual(values[kids[4]], { exists: true, value: 'on', checked: false });
    assert.equal(values['0:k77777'].exists, false);
    assert.equal(values.nope.exists, false);
  });

  e2e('act: single actions and error codes', async () => {
    const nl = byLabel(snap, 'Newsletter abonnieren').kid;
    assert.deepEqual(await desktop.call('act', { tabId: formTabId, kid: nl, action: 'check' }), {
      done: true,
      value: 'on',
      checked: true,
    });
    const anrede = await desktop.call('act', { tabId: formTabId, kid: byLabel(snap, 'Anrede').kid, action: 'select', value: 'Frau' });
    assert.equal(anrede.value, 'Frau');

    const bad = await desktop.request('act', { tabId: formTabId, kid: nl, action: 'explode' });
    assert.equal(bad.error.code, 'bad_request');
    const badKid = await desktop.request('act', { tabId: formTabId, kid: 'k1', action: 'click' });
    assert.equal(badKid.error.code, 'bad_request');
    const gone = await desktop.request('act', { tabId: formTabId, kid: '0:k88888', action: 'click' });
    assert.equal(gone.error.code, 'element_not_found');
    const noFrame = await desktop.request('act', { tabId: formTabId, kid: '987654:k1', action: 'click' });
    assert.equal(noFrame.error.code, 'element_not_found');
    const noTab = await desktop.request('snapshot', { tabId: 99999999 });
    assert.equal(noTab.error.code, 'no_tab');
    const disabled = await desktop.request('act', { tabId: formTabId, kid: byLabel(snap, 'Kundennummer').kid, action: 'fill', value: 'x' });
    assert.equal(disabled.error.code, 'not_supported');
  });

  e2e('scroll', async () => {
    const down = await desktop.call('scroll', { tabId: formTabId, direction: 'down', amount: 300 });
    assert.equal(down.scrollY, 300);
    assert.ok(down.scrollHeight > 1600);
    const top = await desktop.call('scroll', { tabId: formTabId, direction: 'top' });
    assert.equal(top.scrollY, 0);
    const bad = await desktop.request('scroll', { tabId: formTabId, direction: 'left' });
    assert.equal(bad.error.code, 'bad_request');
  });

  e2e('emits domChanged once after a snapshot when the DOM changes', async () => {
    await desktop.call('snapshot', { tabId: formTabId });
    const since = desktop.messages.length;
    const page = pageForUrl(formUrl);
    await page.evaluate(() => {
      const p = document.createElement('p');
      p.textContent = 'Neuer Inhalt';
      document.body.append(p);
    });
    const ev = await desktop.waitFor(
      (m) => m.type === 'event' && m.event === 'domChanged' && m.tabId === formTabId && desktop.messages.indexOf(m) >= since,
      10000,
      'domChanged',
    );
    assert.equal(ev.frameId, 0);
    assert.equal(ev.windowId, formWindowId);
  });

  e2e('snapshot handles cross-origin, about:blank, hidden and broken frames', async () => {
    const framesUrl = `${server.url}/frames.html`;
    const { tabId } = await desktop.call('openTab', { url: framesUrl });
    const page = pageForUrl(framesUrl);
    const s = await desktop.call('snapshot', { tabId });
    // top + cross-origin + about:blank + hidden srcdoc; the error page frame is skipped
    assert.equal(s.frames, 4);
    assert.ok(s.texts.includes('Vielen Dank!'), 'texts from the cross-origin frame');

    const link = byLabel(s, 'Zurück zum Formular');
    assert.notEqual(link.frameId, 0);
    assert.equal(link.visible, true);
    assert.equal(link.inViewport, true);
    // The rect is in top-level viewport coordinates (iframe offset + border + padding):
    // a real mouse click at its center must hit the link inside the cross-origin frame.
    const frame = page.frames().find((f) => f.url().startsWith('http://localhost:'));
    await frame.evaluate(() => {
      window.__hits = 0;
      document.querySelector('a').addEventListener('click', (e) => {
        e.preventDefault();
        window.__hits += 1;
      });
    });
    await page.mouse.click(link.rect.x + link.rect.width / 2, link.rect.y + link.rect.height / 2);
    assert.equal(await frame.evaluate(() => window.__hits), 1);

    const blank = byLabel(s, 'Im leeren Frame');
    assert.notEqual(blank.frameId, 0);
    const blankBox = await page.frameLocator('#leer').locator('input').boundingBox();
    assert.ok(Math.abs(blank.rect.x - blankBox.x) <= 1 && Math.abs(blank.rect.y - blankBox.y) <= 1);

    const hidden = byLabel(s, 'Versteckt im Frame');
    assert.equal(hidden.visible, false, 'elements of a display:none iframe are invisible');
    assert.equal(hidden.inViewport, false);

    // acting inside the cross-origin frame
    const r = await desktop.call('act', { tabId, kid: blank.kid, action: 'fill', value: 'im Frame' });
    assert.equal(r.value, 'im Frame');
    await desktop.call('closeTab', { tabId });
  });

  e2e('restricted pages and invalid URLs', async () => {
    const blank = await desktop.call('openTab', { url: 'about:blank', active: false });
    const snapBlank = await desktop.request('snapshot', { tabId: blank.tabId });
    assert.equal(snapBlank.error.code, 'restricted_page');

    const settings = await context.newPage();
    await settings.goto('chrome://version').catch(() => {});
    const { windows } = await desktop.call('listWindows');
    const chromeTab = windows.flatMap((w) => w.tabs).find((t) => t.url.startsWith('chrome://'));
    assert.ok(chromeTab, 'chrome://version tab');
    const r = await desktop.request('snapshot', { tabId: chromeTab.tabId });
    assert.equal(r.error.code, 'restricted_page');
    const actR = await desktop.request('act', { tabId: chromeTab.tabId, kid: '0:k1', action: 'click' });
    assert.equal(actR.error.code, 'restricted_page');
    await settings.close();

    for (const url of ['javascript:alert(1)', 'chrome://settings', 'data:text/html,hi', 'kein-url']) {
      const r = await desktop.request('navigate', { tabId: blank.tabId, url });
      assert.equal(r.error.code, 'bad_request', url);
    }
    assert.deepEqual(await desktop.call('closeTab', { tabId: blank.tabId }), { closed: true });
    await desktop.waitFor((m) => m.type === 'event' && m.event === 'tabRemoved' && m.tabId === blank.tabId, 5000, 'tabRemoved');
  });

  e2e('navigate, goBack, reload', async () => {
    const thanks = `${server.url}/danke.html`;
    const nav = await desktop.call('navigate', { tabId: formTabId, url: thanks });
    assert.equal(nav.tabId, formTabId);
    assert.equal(nav.complete, true);
    let s = await desktop.call('snapshot', { tabId: formTabId });
    assert.equal(s.url, thanks);
    assert.ok(s.texts.includes('Vielen Dank!'));

    assert.deepEqual(await desktop.call('goBack', { tabId: formTabId }), { done: true });
    s = await desktop.call('snapshot', { tabId: formTabId });
    assert.equal(s.url, formUrl);

    assert.deepEqual(await desktop.call('reload', { tabId: formTabId }), { done: true });
    const page = pageForUrl(formUrl);
    assert.equal(await page.inputValue('#vorname'), '', 'reload resets the form');

    // after navigation the old kids are gone
    const stale = await desktop.request('act', { tabId: formTabId, kid: byLabel(snap, 'Vorname').kid, action: 'fill', value: 'x' });
    assert.equal(stale.error.code, 'element_not_found');
  });

  e2e('activateTab focuses a tab, closeTab closes it', async () => {
    const other = await desktop.call('openTab', { url: `${server.url}/danke.html`, windowId: formWindowId });
    assert.equal(other.windowId, formWindowId);
    assert.deepEqual(await desktop.call('activateTab', { tabId: formTabId }), { tabId: formTabId });
    let { windows } = await desktop.call('listWindows');
    assert.equal(windows.find((w) => w.windowId === formWindowId).activeTabId, formTabId);
    assert.deepEqual(await desktop.call('closeTab', { tabId: other.tabId }), { closed: true });
    ({ windows } = await desktop.call('listWindows'));
    assert.ok(!windows.flatMap((w) => w.tabs).some((t) => t.tabId === other.tabId));
    const missing = await desktop.request('activateTab', { tabId: other.tabId });
    assert.equal(missing.error.code, 'no_tab');
  });

  e2e('popup status reflects the connection', async () => {
    const status = await serviceWorker.evaluate(() => publicStatus());
    assert.equal(status.connected, true);
    assert.equal(status.extensionId, EXTENSION_ID);
    assert.equal(status.browser, 'chromium');
    assert.ok(status.lastRequestAt > 0);
  });

  e2e('reconnects on tab activation after the desktop app went away', async () => {
    desktop.dropConnection();
    // The stub host exits when its socket closes -> the extension marks itself disconnected.
    for (let i = 0; i < 50; i++) {
      if (!(await serviceWorker.evaluate(() => state.connected))) break;
      await sleep(100);
    }
    assert.equal(await serviceWorker.evaluate(() => state.connected), false);

    // Opportunistic reconnects are throttled to one per 10 s.
    const lastAttempt = await serviceWorker.evaluate(() => state.lastConnectAttemptAt);
    const wait = 10_500 - (Date.now() - lastAttempt);
    if (wait > 0) await sleep(wait);

    const helloCount = desktop.messages.filter((m) => m.type === 'hello').length;
    const pages = context.pages();
    const target = pages.find((p) => p.url() !== formUrl) || (await context.newPage());
    await target.bringToFront();
    await pageForUrl(formUrl).bringToFront();
    await desktop.waitFor(
      (m) => m.type === 'hello' && desktop.messages.filter((x) => x.type === 'hello').length > helloCount,
      10000,
      'second hello',
      { includePast: false },
    );
    assert.equal(desktop.connections, 2);
    assert.equal((await desktop.call('ping')).pong, true);
  });

  e2e('desktop_unavailable marks the bridge as disconnected', async () => {
    await desktop.close();
    await serviceWorker.evaluate(() => {
      disconnect(null);
      connect();
    });
    let status = null;
    for (let i = 0; i < 50; i++) {
      status = await serviceWorker.evaluate(() => publicStatus());
      if (!status.connected) break;
      await sleep(100);
    }
    assert.equal(status.connected, false);
    assert.equal(status.lastError, 'Kairo-Desktop-App ist nicht erreichbar.');
  });
});
