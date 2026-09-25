/*
 * Kairo Browser Bridge – background script (Manifest V3, classic script). Chrome/Edge run it as
 * service worker, Firefox/Zen as event page (manifest "background.scripts"); the code only uses
 * the chrome.* APIs both engines implement.
 *
 * Connects to the Kairo desktop app through the native messaging host "com.kairo.bridge"
 * (Kairo.BrowserHost.exe, a pure relay), answers `request` messages with `response`
 * messages and forwards tab/window/navigation events as `event` messages.
 *
 * Protocol: docs/BROWSER-BRIDGE.md. Commands are accepted ONLY from the native messaging
 * port. There is no externally_connectable and no message handling for web pages; the only
 * runtime messages accepted are status queries from our own popup and the one-shot
 * "domChanged" notification from our own content script.
 */
'use strict';

// -----------------------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------------------

const HOST_NAME = 'com.kairo.bridge';
const PROTOCOL_VERSION = 1;
const RECONNECT_ALARM = 'kairo-reconnect';
const OPPORTUNISTIC_RECONNECT_MIN_INTERVAL_MS = 10_000;
const NAVIGATION_TIMEOUT_MS = 15_000;
const DEFAULT_MAX_ELEMENTS = 300;
const MAX_ELEMENTS_LIMIT = 2000;
const MAX_TEXTS = 60;
const MAX_BATCH_ACTIONS = 500;
const POPUP_URL = chrome.runtime.getURL('popup.html');

/** Overall time budget per method; exceeding it yields error code `timeout`. */
const METHOD_TIMEOUTS_MS = {
  ping: 5_000,
  listWindows: 10_000,
  snapshot: 20_000,
  act: 30_000,
  actBatch: 90_000,
  readValues: 15_000,
  scroll: 10_000,
  navigate: NAVIGATION_TIMEOUT_MS + 10_000,
  openTab: NAVIGATION_TIMEOUT_MS + 10_000,
  activateTab: 10_000,
  closeTab: 10_000,
  goBack: NAVIGATION_TIMEOUT_MS + 10_000,
  reload: NAVIGATION_TIMEOUT_MS + 10_000,
};

const ACTIONS = new Set([
  'fill', 'clear', 'click', 'check', 'uncheck', 'select', 'focus', 'scrollIntoView', 'pressEnter',
]);
/** Actions that may legitimately unload the page before a result comes back. */
const NAVIGATING_ACTIONS = new Set(['click', 'pressEnter']);
const SCROLL_DIRECTIONS = new Set(['up', 'down', 'top', 'bottom']);
const KID_RE = /^(\d+):(k\d+)$/;

const NAVIGABLE_PROTOCOLS = new Set(['http:', 'https:', 'file:']);
const RESTRICTED_PROTOCOLS = new Set([
  'chrome:', 'edge:', 'chrome-extension:', 'extension:', 'chrome-untrusted:', 'chrome-search:',
  'chrome-error:', 'devtools:', 'view-source:', 'about:', 'edge-extension:',
  'moz-extension:', 'resource:', 'jar:',
]);
/** Store pages (all browsers) and the domains Firefox never lets extensions script. */
const RESTRICTED_SITES = [
  { host: 'chromewebstore.google.com', path: '/' },
  { host: 'chrome.google.com', path: '/webstore' },
  { host: 'microsoftedge.microsoft.com', path: '/addons' },
  { host: 'addons.mozilla.org', path: '/' },
  { host: 'support.mozilla.org', path: '/' },
  { host: 'accounts.firefox.com', path: '/' },
];

// -----------------------------------------------------------------------------------------
// State (in memory only; nothing sensitive is persisted)
// -----------------------------------------------------------------------------------------

const state = {
  port: null,
  connected: false,
  browser: detectBrowser(),
  /** Gecko only: product name from runtime.getBrowserInfo() ("Firefox", "Zen", …); lets Kairo tell forks apart. */
  product: null,
  lastError: null,
  lastRequestAt: null,
  lastConnectAttemptAt: 0,
  connectedAt: null,
};

class BridgeError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

/** True in Firefox and its forks (Zen, LibreWolf, Floorp, Waterfox): Gecko exposes `browser`. */
function isGecko() {
  return typeof globalThis.browser === 'object' && globalThis.browser !== null &&
    typeof globalThis.browser.runtime?.getBrowserInfo === 'function';
}

function detectBrowser() {
  const uaData = self.navigator && self.navigator.userAgentData;
  const brands = ((uaData && uaData.brands) || []).map((b) => String(b.brand));
  const ua = (self.navigator && self.navigator.userAgent) || '';
  if (isGecko() || /\bFirefox\//.test(ua)) {
    return 'firefox';
  }
  if (brands.some((b) => /Microsoft Edge/i.test(b)) || /\bEdg\//.test(ua)) {
    return 'edge';
  }
  if (brands.some((b) => /Google Chrome/i.test(b))) {
    return 'chrome';
  }
  if (brands.some((b) => /Chromium/i.test(b))) {
    return 'chromium';
  }
  return 'chrome';
}

function errorMessage(e) {
  return String((e && e.message) || e || 'Unknown error');
}

function toBridgeError(e, fallbackCode = 'script_error') {
  if (e instanceof BridgeError) {
    return e;
  }
  if (e && typeof e.code === 'string') {
    return new BridgeError(e.code, errorMessage(e));
  }
  return new BridgeError(fallbackCode, errorMessage(e));
}

// -----------------------------------------------------------------------------------------
// Native messaging connection
// -----------------------------------------------------------------------------------------

/** Translates chrome.runtime.lastError messages of the native port into German UI text. */
function describeDisconnect(message) {
  const m = String(message || '');
  // Chrome: "Specified native messaging host not found." / Firefox: "No such native application …".
  if (/not found|no such native application/i.test(m)) {
    return 'Native-Messaging-Host nicht gefunden – ist Kairo installiert?';
  }
  // Chrome: "Access to the specified native messaging host is forbidden." /
  // Firefox: "This extension does not have permission to use native application …".
  if (/forbidden|does not have permission/i.test(m)) {
    return 'Zugriff auf den Kairo-Host verweigert (Erweiterungs-ID nicht freigegeben).';
  }
  if (/exited/i.test(m)) {
    return 'Der Kairo-Host wurde beendet.';
  }
  if (/communicating/i.test(m)) {
    return 'Fehler bei der Kommunikation mit dem Kairo-Host.';
  }
  return m || 'Verbindung zu Kairo getrennt.';
}

function connect() {
  if (state.port) {
    return;
  }
  state.lastConnectAttemptAt = Date.now();
  let port;
  try {
    port = chrome.runtime.connectNative(HOST_NAME);
  } catch (e) {
    markDisconnected(describeDisconnect(errorMessage(e)));
    return;
  }
  state.port = port;
  state.connected = true;
  state.connectedAt = Date.now();
  state.lastError = null;

  port.onMessage.addListener((msg) => onPortMessage(port, msg));
  port.onDisconnect.addListener((p) => {
    // Chrome reports the reason in runtime.lastError (must be read to avoid "unchecked lastError"),
    // Firefox in port.error.
    const err = chrome.runtime.lastError || p.error;
    if (state.port !== p) {
      return;
    }
    state.port = null;
    markDisconnected(state.lastError || describeDisconnect(err && err.message));
  });

  sendHello(port);
  updateActionTitle();
}

/** Sends `hello` (synchronously in Chromium; Gecko first resolves the product name once). */
async function sendHello(port) {
  if (isGecko() && state.product === null) {
    try {
      state.product = String((await globalThis.browser.runtime.getBrowserInfo()).name || '');
    } catch (_) {
      state.product = '';
    }
    if (state.port !== port) {
      return;
    }
  }
  postMessage({
    type: 'hello',
    protocol: PROTOCOL_VERSION,
    extensionVersion: chrome.runtime.getManifest().version,
    browser: state.browser,
    ...(state.product ? { product: state.product } : {}),
    userAgent: self.navigator.userAgent,
  });
}

function markDisconnected(reason) {
  state.connected = false;
  state.connectedAt = null;
  state.lastError = reason || null;
  updateActionTitle();
  persistStatus();
}

function disconnect(reason) {
  const port = state.port;
  state.port = null;
  if (port) {
    try {
      port.disconnect();
    } catch (_) {
      // already closed
    }
  }
  markDisconnected(reason);
}

/** Tab activation / window focus: reconnect opportunistically, at most once per 10 s. */
function maybeReconnect() {
  if (state.port) {
    return;
  }
  if (Date.now() - state.lastConnectAttemptAt < OPPORTUNISTIC_RECONNECT_MIN_INTERVAL_MS) {
    return;
  }
  connect();
}

function postMessage(msg) {
  const port = state.port;
  if (!port) {
    return false;
  }
  try {
    port.postMessage(msg);
    return true;
  } catch (e) {
    if (state.port === port) {
      state.port = null;
      markDisconnected(describeDisconnect(errorMessage(e)));
    }
    return false;
  }
}

function onPortMessage(port, msg) {
  if (state.port !== port || !msg || typeof msg !== 'object') {
    return;
  }
  if (msg.type === 'status') {
    if (msg.status === 'desktop_unavailable') {
      disconnect('Kairo-Desktop-App ist nicht erreichbar.');
    } else {
      state.connected = true;
      updateActionTitle();
    }
    return;
  }
  if (msg.type === 'request') {
    handleRequest(msg);
  }
  // Other message types are ignored on purpose.
}

function updateActionTitle() {
  const title = state.connected
    ? 'Kairo Browser Bridge – verbunden'
    : 'Kairo Browser Bridge – nicht verbunden';
  try {
    chrome.action.setTitle({ title }).catch(() => {});
  } catch (_) {
    // action API unavailable – ignore
  }
}

/** Only non-sensitive timestamps/texts, kept in session storage for the popup. */
function persistStatus() {
  try {
    chrome.storage.session
      .set({ kairoStatus: { lastRequestAt: state.lastRequestAt, lastError: state.lastError } })
      .catch(() => {});
  } catch (_) {
    // storage unavailable – ignore
  }
}

function publicStatus() {
  return {
    connected: state.connected,
    browser: state.browser,
    lastError: state.lastError,
    lastRequestAt: state.lastRequestAt,
    connectedAt: state.connectedAt,
    lastConnectAttemptAt: state.lastConnectAttemptAt || null,
    extensionVersion: chrome.runtime.getManifest().version,
    extensionId: chrome.runtime.id,
    product: state.product,
  };
}

// -----------------------------------------------------------------------------------------
// Request dispatch
// -----------------------------------------------------------------------------------------

function withTimeout(promise, ms, method) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new BridgeError('timeout', `${method} timed out after ${ms} ms`)), ms);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

async function handleRequest(msg) {
  const id = msg.id;
  state.lastRequestAt = Date.now();
  persistStatus();
  if (typeof id !== 'string' && typeof id !== 'number') {
    postMessage({
      type: 'response',
      id: id === undefined ? null : id,
      ok: false,
      error: { code: 'bad_request', message: 'Request id missing or invalid' },
    });
    return;
  }
  try {
    const method = msg.method;
    const handler = methodHandler(method);
    if (!handler) {
      throw new BridgeError('not_supported', `Unknown method: ${method}`);
    }
    const params = msg.params == null ? {} : msg.params;
    if (typeof params !== 'object' || Array.isArray(params)) {
      throw new BridgeError('bad_request', 'params must be an object');
    }
    const result = await withTimeout(handler(params), METHOD_TIMEOUTS_MS[method] || 20_000, method);
    postMessage({ type: 'response', id, ok: true, result });
  } catch (e) {
    const err = toBridgeError(e);
    postMessage({ type: 'response', id, ok: false, error: { code: err.code, message: err.message } });
  }
}

// -----------------------------------------------------------------------------------------
// Parameter helpers
// -----------------------------------------------------------------------------------------

function optionalInt(value, name, min, max) {
  if (value === undefined || value === null) {
    return undefined;
  }
  if (!Number.isInteger(value) || value < min || value > max) {
    throw new BridgeError('bad_request', `${name} must be an integer between ${min} and ${max}`);
  }
  return value;
}

function requireInt(value, name) {
  if (!Number.isInteger(value)) {
    throw new BridgeError('bad_request', `${name} must be an integer`);
  }
  return value;
}

function optionalBool(value, name) {
  if (value === undefined || value === null) {
    return undefined;
  }
  if (typeof value !== 'boolean') {
    throw new BridgeError('bad_request', `${name} must be a boolean`);
  }
  return value;
}

/** maxElements: default 300; numbers outside 1..2000 are clamped rather than rejected. */
function clampMaxElements(value) {
  if (value === undefined || value === null) {
    return DEFAULT_MAX_ELEMENTS;
  }
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new BridgeError('bad_request', 'maxElements must be a number');
  }
  return Math.min(MAX_ELEMENTS_LIMIT, Math.max(1, Math.floor(value)));
}

function parseKid(kid) {
  const m = typeof kid === 'string' ? KID_RE.exec(kid) : null;
  if (!m) {
    throw new BridgeError('bad_request', `Invalid kid "${kid}" (expected "<frameId>:k<n>")`);
  }
  return { kid, frameId: Number(m[1]), localId: m[2] };
}

/** Validates a navigation target: only http:, https: and file: (plus about:blank if allowed). */
function validateUrl(value, allowBlank) {
  if (typeof value !== 'string' || !value.trim()) {
    throw new BridgeError('bad_request', 'url is required');
  }
  if (allowBlank && value.trim() === 'about:blank') {
    return 'about:blank';
  }
  let u;
  try {
    u = new URL(value.trim());
  } catch (_) {
    throw new BridgeError('bad_request', `Invalid url: ${value}`);
  }
  if (!NAVIGABLE_PROTOCOLS.has(u.protocol)) {
    throw new BridgeError('bad_request', `Only http:, https: and file: URLs are allowed (got ${u.protocol})`);
  }
  return u.href;
}

// -----------------------------------------------------------------------------------------
// Tabs & scripting helpers
// -----------------------------------------------------------------------------------------

async function getTab(tabId) {
  try {
    return await chrome.tabs.get(tabId);
  } catch (_) {
    throw new BridgeError('no_tab', `No tab with id ${tabId}`);
  }
}

/** params.tabId, or the active tab of the last focused normal window. */
async function resolveTab(params) {
  const tabId = optionalInt(params.tabId, 'tabId', 0, Number.MAX_SAFE_INTEGER);
  if (tabId !== undefined) {
    return getTab(tabId);
  }
  let win;
  try {
    win = await chrome.windows.getLastFocused({ windowTypes: ['normal'] });
  } catch (_) {
    throw new BridgeError('no_tab', 'No browser window is open');
  }
  const [tab] = await chrome.tabs.query({ active: true, windowId: win.id });
  if (!tab) {
    throw new BridgeError('no_tab', 'No active tab');
  }
  return tab;
}

function restrictedReason(url) {
  if (!url) {
    return null;
  }
  let u;
  try {
    u = new URL(url);
  } catch (_) {
    return null;
  }
  if (RESTRICTED_PROTOCOLS.has(u.protocol)) {
    return `Pages with ${u.protocol} URLs cannot be accessed by extensions`;
  }
  for (const site of RESTRICTED_SITES) {
    if (u.hostname === site.host && u.pathname.startsWith(site.path)) {
      return 'Extension store pages cannot be accessed by extensions';
    }
  }
  return null;
}

async function resolveScriptableTab(params) {
  const tab = await resolveTab(params);
  const reason = restrictedReason(tab.url || tab.pendingUrl);
  if (reason) {
    throw new BridgeError('restricted_page', reason);
  }
  if (tab.discarded) {
    throw new BridgeError('restricted_page', 'The tab is discarded – activate or reload it first');
  }
  return tab;
}

/** Firefox MV3 treats host permissions as grantable: without them scripting fails with this message. */
function isMissingHostPermission(msg) {
  return /missing host permission/i.test(msg);
}

function isMissingFrameError(msg) {
  return /no frame with id|frame with id \d+ (was removed|not found)|frame .*(removed|not found)/i.test(msg);
}

/** Injects content.js (idempotent) into all frames or the given frames. */
async function injectAgent(tabId, frameIds) {
  const target = frameIds ? { tabId, frameIds } : { tabId, allFrames: true };
  try {
    await chrome.scripting.executeScript({ target, files: ['content.js'] });
  } catch (e) {
    const msg = errorMessage(e);
    if (/no tab with id/i.test(msg)) {
      throw new BridgeError('no_tab', msg);
    }
    if (frameIds && isMissingFrameError(msg)) {
      throw new BridgeError('element_not_found', `Frame no longer exists: ${msg}`);
    }
    if (isMissingHostPermission(msg)) {
      throw new BridgeError('restricted_page', 'No access to websites – allow it in the Kairo extension menu');
    }
    throw new BridgeError('restricted_page', `Cannot access this page: ${msg}`);
  }
}

/**
 * Runs inside the page (isolated world). Must be self-contained: it is serialized by
 * chrome.scripting. Always resolves to {ok, result} or {ok:false, error:{code,message}}.
 */
function invokeAgent(method, args) {
  const agent = globalThis.__kairoAgent;
  const fail = (e) => ({
    ok: false,
    error: {
      code: e && typeof e.code === 'string' ? e.code : 'script_error',
      message: String((e && e.message) || e).slice(0, 500),
    },
  });
  if (!agent || typeof agent[method] !== 'function') {
    return fail({ code: 'script_error', message: 'Kairo agent is not available in this frame' });
  }
  try {
    const r = agent[method](...args);
    if (r && typeof r.then === 'function') {
      return r.then((value) => ({ ok: true, result: value }), fail);
    }
    return { ok: true, result: r };
  } catch (e) {
    return fail(e);
  }
}

async function runAgent(target, method, args) {
  try {
    const results = await chrome.scripting.executeScript({ target, func: invokeAgent, args: [method, args] });
    return results || [];
  } catch (e) {
    const msg = errorMessage(e);
    if (/no tab with id/i.test(msg)) {
      throw new BridgeError('no_tab', msg);
    }
    if (isMissingFrameError(msg)) {
      throw new BridgeError('element_not_found', `Frame no longer exists: ${msg}`);
    }
    throw new BridgeError('script_error', msg);
  }
}

/** Unwraps the single-frame result of runAgent. Returns undefined if the frame gave none. */
function unwrapFrameResult(results, frameId) {
  const r = results.find((x) => x && x.frameId === frameId) || results[0];
  if (!r || !r.result) {
    return undefined;
  }
  if (!r.result.ok) {
    const err = r.result.error || {};
    throw new BridgeError(err.code || 'script_error', err.message || 'Script error');
  }
  return r.result.result;
}

function requireFrameResult(results, frameId) {
  const v = unwrapFrameResult(results, frameId);
  if (v === undefined) {
    throw new BridgeError('script_error', 'No result from the page (it may have navigated away)');
  }
  return v;
}

/**
 * Waits until the main frame of `tabId` finished loading (or a same-document navigation
 * happened). Resolves {complete:true} or {complete:false} after `timeoutMs`; never rejects.
 * With `checkInitial` a tab that is already complete resolves immediately (new tabs).
 */
function waitForNavigation(tabId, timeoutMs, checkInitial) {
  let cleanup = () => {};
  const promise = new Promise((resolve) => {
    let sawLoading = false;
    let finished = false;
    const finish = (outcome) => {
      if (finished) return;
      finished = true;
      cleanup();
      resolve(outcome);
    };
    const isMainFrame = (d) => d.tabId === tabId && d.frameId === 0;
    const onCompleted = (d) => {
      if (isMainFrame(d)) finish({ complete: true });
    };
    const onSameDocument = (d) => {
      if (isMainFrame(d)) finish({ complete: true });
    };
    const onError = (d) => {
      if (isMainFrame(d) && d.error !== 'net::ERR_ABORTED') finish({ complete: true, loadError: d.error });
    };
    const onUpdated = (id, info) => {
      if (id !== tabId) return;
      if (info.status === 'loading') sawLoading = true;
      else if (info.status === 'complete' && sawLoading) finish({ complete: true });
    };
    const onRemoved = (id) => {
      if (id === tabId) finish({ complete: false, removed: true });
    };
    const timer = setTimeout(() => finish({ complete: false }), timeoutMs);
    cleanup = () => {
      clearTimeout(timer);
      chrome.webNavigation.onCompleted.removeListener(onCompleted);
      chrome.webNavigation.onHistoryStateUpdated.removeListener(onSameDocument);
      chrome.webNavigation.onReferenceFragmentUpdated.removeListener(onSameDocument);
      chrome.webNavigation.onErrorOccurred.removeListener(onError);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.tabs.onRemoved.removeListener(onRemoved);
    };
    chrome.webNavigation.onCompleted.addListener(onCompleted);
    chrome.webNavigation.onHistoryStateUpdated.addListener(onSameDocument);
    chrome.webNavigation.onReferenceFragmentUpdated.addListener(onSameDocument);
    chrome.webNavigation.onErrorOccurred.addListener(onError);
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.tabs.onRemoved.addListener(onRemoved);
    if (checkInitial) {
      chrome.tabs
        .get(tabId)
        .then((t) => {
          if (t.status === 'complete' && !t.pendingUrl) finish({ complete: true });
        })
        .catch(() => finish({ complete: false, removed: true }));
    }
  });
  return { promise, cancel: () => cleanup() };
}

function navigationResult(tabId, outcome) {
  const result = { tabId, complete: !!outcome.complete };
  if (outcome.loadError) {
    result.loadError = outcome.loadError;
  }
  return result;
}

// -----------------------------------------------------------------------------------------
// Snapshot merging
// -----------------------------------------------------------------------------------------

/**
 * Offsets of every frame relative to the top-level viewport, using the geometry reported
 * by the content agents (index in parent.frames + child frame content-box positions) and
 * the frame tree from webNavigation. Unknown offsets are null (rects stay frame-local).
 */
function computeFrameOffsets(frames, frameTree) {
  const parentOf = new Map((frameTree || []).map((f) => [f.frameId, f.parentFrameId]));
  const dataOf = new Map(frames.map((f) => [f.frameId, f.data]));
  const memo = new Map([[0, { x: 0, y: 0, visible: true }]]);
  const offsetOf = (frameId, depth) => {
    if (memo.has(frameId)) return memo.get(frameId);
    let result = null;
    const parentId = parentOf.get(frameId);
    const data = dataOf.get(frameId);
    const parentData = dataOf.get(parentId);
    if (depth < 32 && parentId !== undefined && parentId >= 0 && data && parentData && data.frame && parentData.frame) {
      const parentOffset = offsetOf(parentId, depth + 1);
      const child = (parentData.frame.childFrames || []).find((c) => c.index === data.frame.indexInParent);
      if (parentOffset && child) {
        result = {
          x: parentOffset.x + child.x,
          y: parentOffset.y + child.y,
          visible: parentOffset.visible && child.visible,
        };
      }
    }
    memo.set(frameId, result);
    return result;
  };
  const out = new Map();
  for (const f of frames) out.set(f.frameId, offsetOf(f.frameId, 0));
  return out;
}

function intersectsViewport(rect, viewport) {
  if (!viewport) return true;
  return (
    rect.x + rect.width > 0 &&
    rect.y + rect.height > 0 &&
    rect.x < viewport.innerWidth &&
    rect.y < viewport.innerHeight
  );
}

// -----------------------------------------------------------------------------------------
// Methods
// -----------------------------------------------------------------------------------------

async function ping() {
  return { pong: true, time: Date.now() };
}

async function listWindows() {
  const wins = await chrome.windows.getAll({ populate: true, windowTypes: ['normal', 'popup'] });
  return {
    windows: wins.map((w) => {
      const tabs = w.tabs || [];
      const active = tabs.find((t) => t.active);
      return {
        windowId: w.id,
        focused: !!w.focused,
        state: w.state,
        type: w.type,
        left: w.left,
        top: w.top,
        width: w.width,
        height: w.height,
        activeTabId: active ? active.id : null,
        tabs: tabs.map((t) => ({
          tabId: t.id,
          index: t.index,
          title: t.title || '',
          url: t.url || t.pendingUrl || '',
          active: !!t.active,
          status: t.status || 'unloaded',
        })),
      };
    }),
  };
}

async function snapshot(params) {
  const maxElements = clampMaxElements(params.maxElements);
  const includeTextParam = optionalBool(params.includeText, 'includeText');
  const includeText = includeTextParam === undefined ? true : includeTextParam;
  const tab = await resolveScriptableTab(params);

  await injectAgent(tab.id);
  const [results, frameTree] = await Promise.all([
    runAgent({ tabId: tab.id, allFrames: true }, 'snapshot', [{ maxElements, includeText }]),
    chrome.webNavigation.getAllFrames({ tabId: tab.id }).catch(() => null),
  ]);

  const frames = results
    .filter((r) => r && r.result && r.result.ok && r.result.result)
    .map((r) => ({ frameId: r.frameId, data: r.result.result }))
    .sort((a, b) => a.frameId - b.frameId);
  const top = frames.find((f) => f.frameId === 0);
  if (!top) {
    const failed = results.find((r) => r && r.frameId === 0 && r.result && r.result.error);
    if (failed) {
      throw new BridgeError(failed.result.error.code || 'script_error', failed.result.error.message);
    }
    throw new BridgeError('script_error', 'Snapshot of the top frame failed');
  }

  const viewport = top.data.viewport;
  const offsets = computeFrameOffsets(frames, frameTree);
  let truncated = false;
  const pool = [];
  frames.forEach((f, frameOrder) => {
    if (f.data.truncated) truncated = true;
    const offset = offsets.get(f.frameId);
    (f.data.elements || []).forEach((el, index) => {
      const e = { ...el, kid: `${f.frameId}:${el.kid}`, frameId: f.frameId };
      if (f.frameId !== 0 && offset) {
        e.rect = {
          x: Math.round(e.rect.x + offset.x),
          y: Math.round(e.rect.y + offset.y),
          width: e.rect.width,
          height: e.rect.height,
        };
        e.visible = e.visible && offset.visible;
        e.inViewport = e.inViewport && e.visible && intersectsViewport(e.rect, viewport);
      }
      pool.push({ e, frameOrder, index });
    });
  });

  // Overall limit: visible elements first (top frame first), then invisible ones.
  const chosen = [];
  for (const p of pool) if (p.e.visible && chosen.length < maxElements) chosen.push(p);
  for (const p of pool) if (!p.e.visible && chosen.length < maxElements) chosen.push(p);
  if (chosen.length < pool.length) truncated = true;
  chosen.sort((a, b) => a.frameOrder - b.frameOrder || a.index - b.index);

  const texts = [];
  const seen = new Set();
  for (const f of frames) {
    for (const t of f.data.texts || []) {
      if (texts.length >= MAX_TEXTS) break;
      if (!seen.has(t)) {
        seen.add(t);
        texts.push(t);
      }
    }
  }

  return {
    tabId: tab.id,
    windowId: tab.windowId,
    url: top.data.url || tab.url || '',
    title: top.data.title || tab.title || '',
    viewport,
    elements: chosen.map((p) => p.e),
    texts,
    frames: frames.length,
    truncated,
  };
}

async function act(params) {
  const { kid, frameId } = parseKid(params.kid);
  if (!ACTIONS.has(params.action)) {
    throw new BridgeError('bad_request', `Unknown action "${params.action}"`);
  }
  const tab = await resolveScriptableTab(params);
  await injectAgent(tab.id, [frameId]);
  const results = await runAgent({ tabId: tab.id, frameIds: [frameId] }, 'act', [kid, params.action, params.value]);
  const result = unwrapFrameResult(results, frameId);
  if (result === undefined) {
    if (NAVIGATING_ACTIONS.has(params.action)) {
      // The action was dispatched, but the document unloaded before it could answer.
      return { done: true, value: null, checked: null };
    }
    throw new BridgeError('script_error', 'No result from the page (it may have navigated away)');
  }
  return result;
}

function batchErrorItem(kid, err) {
  return { kid: kid === undefined ? null : kid, ok: false, value: null, checked: null, error: { code: err.code, message: err.message } };
}

async function actBatch(params) {
  if (!Array.isArray(params.actions)) {
    throw new BridgeError('bad_request', 'actions must be an array');
  }
  if (params.actions.length > MAX_BATCH_ACTIONS) {
    throw new BridgeError('bad_request', `At most ${MAX_BATCH_ACTIONS} actions per batch`);
  }
  const tab = await resolveScriptableTab(params);

  // Validate every item up front; invalid items become per-item errors.
  const items = params.actions.map((a, i) => {
    if (!a || typeof a !== 'object') {
      return { i, kid: null, error: new BridgeError('bad_request', 'Each action must be an object') };
    }
    try {
      const { frameId } = parseKid(a.kid);
      if (!ACTIONS.has(a.action)) {
        throw new BridgeError('bad_request', `Unknown action "${a.action}"`);
      }
      return { i, kid: a.kid, frameId, action: a.action, value: a.value };
    } catch (e) {
      return { i, kid: a.kid, error: toBridgeError(e, 'bad_request') };
    }
  });

  // Run consecutive actions of the same frame in one executeScript call, preserving order.
  const results = new Array(items.length);
  const injected = new Set();
  let k = 0;
  while (k < items.length) {
    const first = items[k];
    if (first.error) {
      results[first.i] = batchErrorItem(first.kid, first.error);
      k++;
      continue;
    }
    const run = [first];
    let j = k + 1;
    while (j < items.length && !items[j].error && items[j].frameId === first.frameId) {
      run.push(items[j]);
      j++;
    }
    try {
      if (!injected.has(first.frameId)) {
        await injectAgent(tab.id, [first.frameId]);
        injected.add(first.frameId);
      }
      const res = await runAgent({ tabId: tab.id, frameIds: [first.frameId] }, 'actBatch', [
        run.map((r) => ({ kid: r.kid, action: r.action, value: r.value })),
      ]);
      const out = requireFrameResult(res, first.frameId);
      run.forEach((r, idx) => {
        const item = out.results && out.results[idx];
        results[r.i] = item
          ? { kid: r.kid, ok: !!item.ok, value: item.value ?? null, checked: item.checked ?? null, error: item.error || null }
          : batchErrorItem(r.kid, new BridgeError('script_error', 'Missing result'));
      });
    } catch (e) {
      const err = toBridgeError(e);
      run.forEach((r) => {
        results[r.i] = batchErrorItem(r.kid, err);
      });
    }
    k = j;
  }
  return { results };
}

async function readValues(params) {
  if (!Array.isArray(params.kids)) {
    throw new BridgeError('bad_request', 'kids must be an array');
  }
  const tab = await resolveScriptableTab(params);
  const missing = { exists: false, value: null, checked: null };
  const values = {};
  const byFrame = new Map();
  for (const kid of params.kids) {
    const m = typeof kid === 'string' ? KID_RE.exec(kid) : null;
    if (!m) {
      values[String(kid)] = { ...missing };
      continue;
    }
    const frameId = Number(m[1]);
    if (!byFrame.has(frameId)) byFrame.set(frameId, []);
    byFrame.get(frameId).push(kid);
  }
  for (const [frameId, kids] of byFrame) {
    try {
      await injectAgent(tab.id, [frameId]);
      const res = await runAgent({ tabId: tab.id, frameIds: [frameId] }, 'readValues', [kids]);
      const out = requireFrameResult(res, frameId);
      for (const kid of kids) {
        values[kid] = (out.values && out.values[kid]) || { ...missing };
      }
    } catch (e) {
      const err = toBridgeError(e);
      if (err.code === 'restricted_page' || err.code === 'no_tab') throw err;
      for (const kid of kids) values[kid] = { ...missing };
    }
  }
  return { values };
}

async function scroll(params) {
  if (!SCROLL_DIRECTIONS.has(params.direction)) {
    throw new BridgeError('bad_request', 'direction must be one of up, down, top, bottom');
  }
  if (params.amount != null && !(typeof params.amount === 'number' && params.amount > 0)) {
    throw new BridgeError('bad_request', 'amount must be a positive number (px)');
  }
  const tab = await resolveScriptableTab(params);
  await injectAgent(tab.id, [0]);
  const res = await runAgent({ tabId: tab.id, frameIds: [0] }, 'scroll', [params.direction, params.amount ?? null]);
  return requireFrameResult(res, 0);
}

async function navigate(params) {
  const url = validateUrl(params.url, false);
  const tab = await resolveTab(params);
  const waiter = waitForNavigation(tab.id, NAVIGATION_TIMEOUT_MS, false);
  try {
    await chrome.tabs.update(tab.id, { url });
  } catch (e) {
    waiter.cancel();
    const msg = errorMessage(e);
    throw new BridgeError(/no tab with id/i.test(msg) ? 'no_tab' : 'not_supported', msg);
  }
  return navigationResult(tab.id, await waiter.promise);
}

async function openTab(params) {
  const url = validateUrl(params.url, true);
  const windowId = optionalInt(params.windowId, 'windowId', 0, Number.MAX_SAFE_INTEGER);
  const activeParam = optionalBool(params.active, 'active');
  const active = activeParam === undefined ? true : activeParam;
  let tab;
  try {
    tab = await chrome.tabs.create({ url, active, ...(windowId !== undefined ? { windowId } : {}) });
  } catch (e) {
    const msg = errorMessage(e);
    if (windowId === undefined && /window/i.test(msg)) {
      // No browser window open (browser running in the background): open a new one.
      const win = await chrome.windows.create({ url, focused: active });
      tab = win.tabs && win.tabs[0];
    } else if (/no window with id/i.test(msg)) {
      throw new BridgeError('bad_request', `No window with id ${windowId}`);
    } else {
      throw new BridgeError('not_supported', msg);
    }
  }
  if (!tab) {
    throw new BridgeError('no_tab', 'Tab could not be created');
  }
  await waitForNavigation(tab.id, NAVIGATION_TIMEOUT_MS, true).promise;
  return { tabId: tab.id, windowId: tab.windowId };
}

async function activateTab(params) {
  const tabId = requireInt(params.tabId, 'tabId');
  const tab = await getTab(tabId);
  await chrome.tabs.update(tabId, { active: true });
  try {
    await chrome.windows.update(tab.windowId, { focused: true });
  } catch (_) {
    // Focusing can fail on some window managers; the tab is active anyway.
  }
  return { tabId };
}

async function closeTab(params) {
  const tabId = requireInt(params.tabId, 'tabId');
  await getTab(tabId);
  await chrome.tabs.remove(tabId);
  return { closed: true };
}

async function goBack(params) {
  const tab = await resolveTab(params);
  const waiter = waitForNavigation(tab.id, NAVIGATION_TIMEOUT_MS, false);
  try {
    await chrome.tabs.goBack(tab.id);
  } catch (e) {
    waiter.cancel();
    throw new BridgeError('not_supported', `Cannot go back: ${errorMessage(e)}`);
  }
  await waiter.promise;
  return { done: true };
}

async function reload(params) {
  const tab = await resolveTab(params);
  const waiter = waitForNavigation(tab.id, NAVIGATION_TIMEOUT_MS, false);
  try {
    await chrome.tabs.reload(tab.id);
  } catch (e) {
    waiter.cancel();
    throw new BridgeError('not_supported', errorMessage(e));
  }
  await waiter.promise;
  return { done: true };
}

const METHODS = {
  ping,
  listWindows,
  snapshot,
  act,
  actBatch,
  readValues,
  scroll,
  navigate,
  openTab,
  activateTab,
  closeTab,
  goBack,
  reload,
};

function methodHandler(name) {
  return typeof name === 'string' && Object.prototype.hasOwnProperty.call(METHODS, name) ? METHODS[name] : null;
}

// -----------------------------------------------------------------------------------------
// Events → Kairo (only while connected)
// -----------------------------------------------------------------------------------------

function emitEvent(event, fields) {
  if (!state.port) {
    return;
  }
  postMessage({ type: 'event', event, ...fields });
}

/** Emits an event that needs windowId/url/title of a tab (looked up lazily). */
async function emitTabEvent(event, tabId, extra) {
  if (!state.port) {
    return;
  }
  let tab = null;
  try {
    tab = await chrome.tabs.get(tabId);
  } catch (_) {
    tab = null;
  }
  emitEvent(event, {
    tabId,
    windowId: tab ? tab.windowId : null,
    url: (extra && extra.url) || (tab && tab.url) || undefined,
    title: tab ? tab.title : undefined,
    ...(extra && extra.frameId !== undefined ? { frameId: extra.frameId } : {}),
  });
}

function isActiveMainFrame(d) {
  return d.frameId === 0 && (d.documentLifecycle === undefined || d.documentLifecycle === 'active');
}

chrome.tabs.onActivated.addListener(({ tabId, windowId }) => {
  maybeReconnect();
  if (!state.port) return;
  chrome.tabs
    .get(tabId)
    .then((tab) => emitEvent('tabActivated', { tabId, windowId, url: tab.url || tab.pendingUrl, title: tab.title }))
    .catch(() => emitEvent('tabActivated', { tabId, windowId }));
});

chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if (info.status === 'complete' || typeof info.title === 'string') {
    emitEvent('tabUpdated', { tabId, windowId: tab.windowId, url: tab.url || tab.pendingUrl, title: tab.title });
  }
});

chrome.tabs.onRemoved.addListener((tabId, info) => {
  emitEvent('tabRemoved', { tabId, windowId: info.windowId });
});

chrome.windows.onFocusChanged.addListener((windowId) => {
  if (windowId !== chrome.windows.WINDOW_ID_NONE) {
    maybeReconnect();
  }
  if (!state.port) return;
  if (windowId === chrome.windows.WINDOW_ID_NONE) {
    emitEvent('windowFocusChanged', { tabId: null, windowId });
    return;
  }
  chrome.tabs
    .query({ active: true, windowId })
    .then(([tab]) => emitEvent('windowFocusChanged', {
      tabId: tab ? tab.id : null,
      windowId,
      url: tab ? tab.url || tab.pendingUrl : undefined,
      title: tab ? tab.title : undefined,
    }))
    .catch(() => emitEvent('windowFocusChanged', { tabId: null, windowId }));
});

const onMainFrameNavigation = (d) => {
  if (isActiveMainFrame(d)) {
    emitTabEvent('navigationCompleted', d.tabId, { url: d.url });
  }
};
chrome.webNavigation.onCompleted.addListener(onMainFrameNavigation);
chrome.webNavigation.onHistoryStateUpdated.addListener(onMainFrameNavigation);
chrome.webNavigation.onReferenceFragmentUpdated.addListener(onMainFrameNavigation);

// -----------------------------------------------------------------------------------------
// Runtime messages: popup status (extension pages without a tab) + content-script domChanged
// -----------------------------------------------------------------------------------------

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || typeof msg !== 'object' || sender.id !== chrome.runtime.id) {
    return false;
  }
  if (sender.tab) {
    // Our own content script (isolated world). Only the one-shot DOM change notice is accepted.
    if (msg.kind === 'kairo:domChanged' && typeof sender.tab.id === 'number') {
      emitEvent('domChanged', {
        tabId: sender.tab.id,
        windowId: sender.tab.windowId,
        frameId: sender.frameId,
        url: sender.tab.url || sender.tab.pendingUrl,
      });
    }
    return false;
  }
  if (typeof sender.url !== 'string' || !sender.url.startsWith(POPUP_URL)) {
    return false;
  }
  if (msg.kind === 'kairo:getStatus') {
    sendResponse(publicStatus());
    return false;
  }
  if (msg.kind === 'kairo:reconnect') {
    disconnect(null);
    connect();
    sendResponse(publicStatus());
    return false;
  }
  return false;
});

// -----------------------------------------------------------------------------------------
// Lifecycle: connect on start, retry every minute via alarm
// -----------------------------------------------------------------------------------------

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === RECONNECT_ALARM && !state.port) {
    connect();
  }
});

chrome.runtime.onStartup.addListener(() => connect());
chrome.runtime.onInstalled.addListener(() => connect());

chrome.alarms
  .get(RECONNECT_ALARM)
  .then((alarm) => {
    if (!alarm) {
      chrome.alarms.create(RECONNECT_ALARM, { periodInMinutes: 1 });
    }
  })
  .catch(() => {});

chrome.storage.session
  .get('kairoStatus')
  .then((data) => {
    const saved = data && data.kairoStatus;
    if (saved && state.lastRequestAt === null && typeof saved.lastRequestAt === 'number') {
      state.lastRequestAt = saved.lastRequestAt;
    }
  })
  .catch(() => {});

connect();
