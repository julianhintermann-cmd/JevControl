/*
 * Kairo Browser Bridge – popup.
 * Shows the connection status kept in memory by the background service worker.
 */
'use strict';

const BROWSER_NAMES = {
  chrome: 'Google Chrome',
  edge: 'Microsoft Edge',
  chromium: 'Chromium',
};
const REFRESH_INTERVAL_MS = 1000;

const $ = (id) => document.getElementById(id);

function relativeTime(timestamp) {
  if (!timestamp) {
    return 'Noch keine';
  }
  const seconds = Math.max(0, Math.round((Date.now() - timestamp) / 1000));
  if (seconds < 5) return 'Gerade eben';
  if (seconds < 60) return `Vor ${seconds} s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `Vor ${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `Vor ${hours} h`;
  return new Date(timestamp).toLocaleString('de-CH');
}

function render(status) {
  const box = $('status');
  if (!status) {
    box.dataset.state = 'disconnected';
    $('status-title').textContent = 'Status nicht verfügbar';
    $('status-detail').textContent = 'Der Hintergrunddienst der Erweiterung antwortet nicht.';
    return;
  }
  box.dataset.state = status.connected ? 'connected' : 'disconnected';
  $('status-title').textContent = status.connected ? 'Verbunden mit Kairo' : 'Nicht verbunden';
  $('status-detail').textContent = status.connected
    ? 'Kairo kann Tabs lesen und bedienen.'
    : status.lastError || 'Kairo-Desktop-App starten und erneut verbinden.';
  $('browser').textContent = BROWSER_NAMES[status.browser] || status.browser || '–';
  $('last-activity').textContent = relativeTime(status.lastRequestAt);
  $('version').textContent = status.extensionVersion || '–';
}

async function refresh() {
  try {
    render(await chrome.runtime.sendMessage({ kind: 'kairo:getStatus' }));
  } catch (_) {
    render(null);
  }
}

$('reconnect').addEventListener('click', async () => {
  const button = $('reconnect');
  button.disabled = true;
  button.textContent = 'Verbinde …';
  try {
    await chrome.runtime.sendMessage({ kind: 'kairo:reconnect' });
  } catch (_) {
    // rendered as "Status nicht verfügbar" below
  }
  // Give the native host a moment to report "desktop_unavailable" or stay connected.
  setTimeout(async () => {
    await refresh();
    button.disabled = false;
    button.textContent = 'Erneut verbinden';
  }, 1200);
});

refresh();
setInterval(refresh, REFRESH_INTERVAL_MS);
