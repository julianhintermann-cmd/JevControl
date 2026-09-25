'use strict';
/*
 * Tests for the popup UI (extension/popup.html + popup.js). The page is loaded from disk with
 * a stubbed chrome.runtime that plays the background service worker's part.
 */

const { describe, test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { EXTENSION_DIR, loadPlaywright } = require('./helpers.cjs');

const { chromium } = loadPlaywright();
const POPUP_URL = pathToFileURL(path.join(EXTENSION_DIR, 'popup.html')).href;

let browser;

before(async () => {
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  if (browser) await browser.close();
});

/** Opens the popup with a fake background that answers with `status`. */
async function openPopup(status, { colorScheme = 'light', hostPermission } = {}) {
  const page = await browser.newPage({ colorScheme });
  await page.addInitScript(({ initial, granted }) => {
    window.__sent = [];
    window.__status = initial;
    window.__permissionRequests = [];
    window.chrome = window.chrome || {};
    if (granted !== undefined) {
      // Firefox/Zen (MV3): website access may be missing until the user grants it.
      window.__granted = granted;
      window.chrome.permissions = {
        contains: async (p) => {
          if (JSON.stringify(p) !== JSON.stringify({ origins: ['<all_urls>'] })) throw new Error('unexpected query');
          return window.__granted;
        },
        request: async (p) => {
          window.__permissionRequests.push(p);
          window.__granted = true;
          return true;
        },
      };
    }
    window.chrome.runtime = {
      sendMessage: async (msg) => {
        window.__sent.push(msg.kind);
        if (msg.kind === 'kairo:reconnect') {
          window.__status = { ...window.__status, connected: true, lastError: null };
        }
        if (window.__status === 'fail') throw new Error('no receiver');
        return window.__status;
      },
    };
  }, { initial: status, granted: hostPermission });
  await page.goto(POPUP_URL);
  return page;
}

const base = {
  connected: true,
  browser: 'edge',
  lastError: null,
  lastRequestAt: Date.now() - 12_000,
  extensionVersion: '1.0.0',
  extensionId: 'fjdcafkellelfdkneebdlmoggkhkilmh',
};

describe('popup', () => {
  test('shows the connected state in German', async () => {
    const page = await openPopup(base);
    await page.waitForFunction(() => document.getElementById('status').dataset.state === 'connected');
    assert.equal(await page.textContent('#status-title'), 'Verbunden mit Kairo');
    assert.equal(await page.textContent('#browser'), 'Microsoft Edge');
    assert.match(await page.textContent('#last-activity'), /^Vor 1\d s$/);
    assert.equal(await page.textContent('#version'), '1.0.0');
    assert.equal(await page.getAttribute('html', 'lang'), 'de');
    await page.close();
  });

  test('shows the disconnected state with the reason and reconnects on click', async () => {
    const page = await openPopup({
      ...base,
      connected: false,
      browser: 'chrome',
      lastRequestAt: null,
      lastError: 'Kairo-Desktop-App ist nicht erreichbar.',
    });
    await page.waitForFunction(() => document.getElementById('status').dataset.state === 'disconnected');
    assert.equal(await page.textContent('#status-title'), 'Nicht verbunden');
    assert.equal(await page.textContent('#status-detail'), 'Kairo-Desktop-App ist nicht erreichbar.');
    assert.equal(await page.textContent('#browser'), 'Google Chrome');
    assert.equal(await page.textContent('#last-activity'), 'Noch keine');

    await page.click('#reconnect');
    assert.equal(await page.textContent('#reconnect'), 'Verbinde …');
    assert.equal(await page.isDisabled('#reconnect'), true);
    await page.waitForFunction(() => document.getElementById('reconnect').textContent === 'Erneut verbinden');
    assert.ok((await page.evaluate(() => window.__sent)).includes('kairo:reconnect'));
    assert.equal(await page.textContent('#status-title'), 'Verbunden mit Kairo');
    await page.close();
  });

  test('shows the Gecko product name (Zen) and hides the permission box when access is granted', async () => {
    const page = await openPopup({ ...base, browser: 'firefox', product: 'Zen' }, { hostPermission: true });
    await page.waitForFunction(() => document.getElementById('status').dataset.state === 'connected');
    assert.equal(await page.textContent('#browser'), 'Zen');
    assert.equal(await page.isHidden('#permission'), true);
    await page.close();
  });

  test('asks for website access when it is missing and hides the request after granting', async () => {
    const page = await openPopup({ ...base, browser: 'firefox' }, { hostPermission: false });
    await page.waitForSelector('#permission', { state: 'visible' });
    assert.equal(await page.textContent('#browser'), 'Firefox');
    assert.equal(await page.textContent('#grant'), 'Zugriff auf Websites erlauben');
    await page.click('#grant');
    await page.waitForSelector('#permission', { state: 'hidden' });
    assert.deepEqual(await page.evaluate(() => window.__permissionRequests), [{ origins: ['<all_urls>'] }]);
    await page.close();
  });

  test('handles an unreachable background and supports dark mode', async () => {
    const page = await openPopup('fail', { colorScheme: 'dark' });
    await page.waitForFunction(() => document.getElementById('status-title').textContent === 'Status nicht verfügbar');
    const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    assert.equal(bg, 'rgb(32, 32, 32)');
    await page.close();
  });
});
