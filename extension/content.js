/*
 * Kairo Browser Bridge – content agent.
 *
 * Injected on demand by background.js via chrome.scripting.executeScript into every frame
 * (there are no permanently running content scripts). The file is idempotent: injecting it
 * again into the same document keeps the existing agent (and therefore the id counter and
 * the element registry).
 *
 * The agent works per frame with LOCAL element ids ("k<n>"), stored in the attribute
 * data-kairo-id. The background script prefixes them with the frame id ("<frameId>:k<n>").
 * For convenience every method also accepts the prefixed form and ignores the prefix.
 *
 * All methods return plain, JSON-serializable objects. Failures are thrown as Error objects
 * with a string `code` property (one of the protocol error codes); background.js converts them
 * into protocol errors.
 *
 * Security: page content is data, never instructions. Real password values never leave the
 * page – password fields are always reported as "••••" (filled) or "" (empty).
 */
(() => {
  'use strict';

  if (globalThis.__kairoAgent) {
    return;
  }

  // ---------------------------------------------------------------------------------------
  // Constants
  // ---------------------------------------------------------------------------------------

  const AGENT_VERSION = 1;
  const ID_ATTR = 'data-kairo-id';
  const LOCAL_ID_RE = /^k(\d+)$/;
  const PASSWORD_MASK = '••••'; // "••••"

  const DEFAULT_MAX_ELEMENTS = 300;
  const HARD_MAX_CANDIDATES = 5000; // safety net for pathological pages
  const MAX_LABEL = 120;
  const MAX_SECTION = 120;
  const MAX_BUTTON_TEXT = 80;
  const MAX_HREF = 200;
  const MAX_VALUE = 2000;
  const MAX_OPTIONS = 100;
  const MAX_OPTION_TEXT = 100;
  const MAX_TEXT_ENTRY = 200;
  const MAX_TEXTS = 60;
  const MAX_FALLBACK_TEXT = 80;
  const OPTION_WAIT_MS = 1500;

  // Roles from the spec plus a few close relatives (searchbox, spinbutton, listbox,
  // menuitemcheckbox, menuitemradio) that are normalized onto the spec's role set.
  const ARIA_ROLES = [
    'button', 'link', 'checkbox', 'radio', 'switch', 'combobox', 'textbox', 'tab', 'menuitem',
    'option', 'slider', 'searchbox', 'spinbutton', 'listbox', 'menuitemcheckbox', 'menuitemradio',
  ];

  const CANDIDATE_SELECTOR = [
    'input',
    'textarea',
    'select',
    'button',
    'a[href]',
    '[contenteditable]',
    ...ARIA_ROLES.map((r) => `[role="${r}" i]`),
  ].join(',');

  /** Maps explicit ARIA roles onto the normalized role set of the protocol. */
  const ROLE_MAP = {
    button: 'button',
    link: 'link',
    checkbox: 'checkbox',
    menuitemcheckbox: 'checkbox',
    radio: 'radio',
    menuitemradio: 'radio',
    switch: 'switch',
    combobox: 'combobox',
    listbox: 'listbox',
    textbox: 'textbox',
    searchbox: 'textbox',
    slider: 'slider',
    spinbutton: 'slider',
    tab: 'tab',
    menuitem: 'menuitem',
    option: 'option',
  };

  /** Roles whose accessible name is their own content (text) rather than a nearby label. */
  const TEXT_ROLES = new Set(['button', 'link', 'tab', 'menuitem', 'option']);

  const BUTTON_INPUT_TYPES = new Set(['submit', 'button', 'reset', 'image']);
  const DATE_INPUT_TYPES = new Set(['date', 'datetime-local', 'month', 'week', 'time']);
  const TRUTHY_VALUES = new Set(['true', '1', 'on', 'yes', 'ja', 'checked', 'x']);

  /** Elements that are form fields in their own right (used to delimit label search). */
  const CONTROL_SELECTOR = 'input:not([type="hidden" i]),textarea,select,button,[contenteditable]';

  const HEADING_SELECTOR = 'h1,h2,h3,h4,h5,h6,[role="heading" i]';
  const TEXTS_SELECTOR = [
    HEADING_SELECTOR,
    'legend',
    'label',
    '[role="alert" i]',
    '.error',
    '.invalid-feedback',
  ].join(',');
  const REGION_SELECTOR = [
    'form', 'section', 'dialog', '[role="form" i]', '[role="region" i]', '[role="group" i]',
    '[role="dialog" i]', '[role="search" i]',
  ].join(',');

  const ACTIONS = new Set([
    'fill', 'clear', 'click', 'check', 'uncheck', 'select', 'focus', 'scrollIntoView', 'pressEnter',
  ]);

  // ---------------------------------------------------------------------------------------
  // Agent state (lives as long as the document)
  // ---------------------------------------------------------------------------------------

  let nextId = 1;
  /** localId -> WeakRef(element); speeds up lookups and survives shadow DOM. */
  const registry = new Map();
  let domObserver = null;
  let domChangeTimer = null;

  // ---------------------------------------------------------------------------------------
  // Small helpers
  // ---------------------------------------------------------------------------------------

  /** Creates an Error carrying a protocol error code (and optional extra fields). */
  function kairoError(code, message, extra) {
    const e = new Error(message);
    e.code = code;
    if (extra) {
      Object.assign(e, extra);
    }
    return e;
  }

  function clean(s) {
    return String(s == null ? '' : s).replace(/\s+/g, ' ').trim();
  }

  function truncate(s, max) {
    if (s == null) {
      return s;
    }
    const str = String(s);
    return str.length > max ? `${str.slice(0, max - 1)}…` : str;
  }

  /** Normalizes a label: collapses whitespace, strips required markers and trailing colons. */
  function cleanLabel(s) {
    const t = clean(s).replace(/^[*\s]+/, '').replace(/[\s*:]+$/, '');
    return truncate(t, MAX_LABEL);
  }

  function tagOf(el) {
    return el.tagName ? el.tagName.toLowerCase() : '';
  }

  function inputType(el) {
    return tagOf(el) === 'input' ? String(el.type || 'text').toLowerCase() : null;
  }

  function explicitRole(el) {
    const r = el.getAttribute('role');
    return r ? r.trim().toLowerCase().split(/\s+/)[0] : '';
  }

  function clampInt(v, min, max, dflt) {
    const n = Number(v);
    if (!Number.isFinite(n)) {
      return dflt;
    }
    return Math.min(max, Math.max(min, Math.floor(n)));
  }

  function round(n) {
    return Math.round(Number(n) || 0);
  }

  /** Strips an optional "<frameId>:" prefix from a kid. */
  function toLocalId(kid) {
    const s = String(kid == null ? '' : kid);
    const i = s.lastIndexOf(':');
    return i >= 0 ? s.slice(i + 1) : s;
  }

  /** Returns the (open or closed) shadow root of an element, if the extension may access it. */
  function shadowRootOf(el) {
    try {
      const dom = globalThis.chrome && globalThis.chrome.dom;
      if (dom && typeof dom.openOrClosedShadowRoot === 'function') {
        return dom.openOrClosedShadowRoot(el) || null;
      }
      // Firefox content scripts: property instead of chrome.dom.
      if ('openOrClosedShadowRoot' in el) {
        return el.openOrClosedShadowRoot || null;
      }
    } catch (_) {
      // fall through to the open shadow root
    }
    return el.shadowRoot || null;
  }

  /** Yields to the event loop via MessageChannel (not throttled in background tabs). */
  function yieldToEventLoop() {
    return new Promise((resolve) => {
      const ch = new MessageChannel();
      ch.port1.onmessage = () => {
        ch.port1.close();
        resolve();
      };
      ch.port2.postMessage(null);
    });
  }

  function isRendered(el) {
    if (!el || !el.isConnected) {
      return false;
    }
    if (el.getClientRects().length === 0) {
      return false;
    }
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.visibility !== 'collapse';
  }

  /** Visibility per spec: display:none, visibility:hidden or zero size => not visible. */
  function computeVisible(el, rect) {
    if (!el.isConnected) {
      return false;
    }
    if (!(rect.width > 0 && rect.height > 0)) {
      return false;
    }
    if (typeof el.checkVisibility === 'function' && !el.checkVisibility()) {
      return false;
    }
    const cs = getComputedStyle(el);
    return cs.visibility !== 'hidden' && cs.visibility !== 'collapse' && cs.display !== 'none';
  }

  function computeInViewport(rect, visible) {
    return (
      visible &&
      rect.bottom > 0 &&
      rect.right > 0 &&
      rect.top < window.innerHeight &&
      rect.left < window.innerWidth
    );
  }

  function isDisabled(el) {
    try {
      if (el.matches(':disabled')) {
        return true;
      }
    } catch (_) {
      // ignore
    }
    if (el.getAttribute('aria-disabled') === 'true') {
      return true;
    }
    return !!el.closest('[inert]');
  }

  function isReadOnly(el) {
    if (el.readOnly === true) {
      return true;
    }
    return el.getAttribute('aria-readonly') === 'true';
  }

  // ---------------------------------------------------------------------------------------
  // Element collection & ids
  // ---------------------------------------------------------------------------------------

  /** Native tags that are always candidates (inputs are checked separately, links need href). */
  const NATIVE_CANDIDATE_TAGS = new Set(['textarea', 'select', 'button']);

  function isCandidate(el) {
    if (!el.matches(CANDIDATE_SELECTOR)) {
      return false;
    }
    const tag = tagOf(el);
    if (tag === 'input') {
      return inputType(el) !== 'hidden';
    }
    if (NATIVE_CANDIDATE_TAGS.has(tag) || (tag === 'a' && el.hasAttribute('href'))) {
      return true;
    }
    if (ROLE_MAP[explicitRole(el)]) {
      return true;
    }
    // Matched only through [contenteditable]: keep real editing hosts, skip nested ones.
    if (!el.isContentEditable) {
      return false;
    }
    const parent = el.parentElement;
    return !(parent && parent.isContentEditable);
  }

  /** Collects candidate elements in document order, descending into shadow roots. */
  function collectCandidates(root, out) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
    let node = walker.nextNode();
    while (node) {
      if (out.length >= HARD_MAX_CANDIDATES) {
        return;
      }
      if (isCandidate(node)) {
        out.push(node);
      }
      const sr = shadowRootOf(node);
      if (sr) {
        collectCandidates(sr, out);
      }
      node = walker.nextNode();
    }
  }

  /**
   * Makes sure every candidate has a unique, stable data-kairo-id. Existing ids are kept
   * (stable across snapshots); duplicates (e.g. from cloned nodes) are re-assigned.
   */
  function ensureIds(elements) {
    const byId = new Map();
    for (const el of elements) {
      const id = el.getAttribute(ID_ATTR);
      const m = id && LOCAL_ID_RE.exec(id);
      if (m) {
        nextId = Math.max(nextId, Number(m[1]) + 1);
        if (!byId.has(id)) {
          byId.set(id, []);
        }
        byId.get(id).push(el);
      }
    }
    const keep = new Set();
    for (const [id, els] of byId) {
      const known = registry.get(id);
      const knownEl = known && known.deref();
      const keeper = els.includes(knownEl) ? knownEl : els[0];
      keep.add(keeper);
      registry.set(id, new WeakRef(keeper));
    }
    for (const el of elements) {
      if (keep.has(el)) {
        continue;
      }
      const id = `k${nextId++}`;
      el.setAttribute(ID_ATTR, id);
      registry.set(id, new WeakRef(el));
    }
  }

  function deepQuery(root, selector) {
    const direct = root.querySelector(selector);
    if (direct) {
      return direct;
    }
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
    let node = walker.nextNode();
    while (node) {
      const sr = shadowRootOf(node);
      if (sr) {
        const found = deepQuery(sr, selector);
        if (found) {
          return found;
        }
      }
      node = walker.nextNode();
    }
    return null;
  }

  /** Finds an element by its local (or prefixed) kid. Returns null when it is gone. */
  function findElement(kid) {
    const id = toLocalId(kid);
    if (!LOCAL_ID_RE.test(id)) {
      return null;
    }
    const ref = registry.get(id);
    const cached = ref && ref.deref();
    if (cached && cached.isConnected && cached.getAttribute(ID_ATTR) === id) {
      return cached;
    }
    const el = deepQuery(document, `[${ID_ATTR}="${id}"]`);
    if (el) {
      registry.set(id, new WeakRef(el));
    } else {
      registry.delete(id);
    }
    return el;
  }

  function requireElement(kid) {
    const el = findElement(kid);
    if (!el) {
      throw kairoError(
        'element_not_found',
        `Element ${kid} not found (the page may have changed – take a new snapshot)`,
      );
    }
    return el;
  }

  // ---------------------------------------------------------------------------------------
  // Roles, labels, sections
  // ---------------------------------------------------------------------------------------

  function normalizeRole(el) {
    const explicit = explicitRole(el);
    if (explicit && ROLE_MAP[explicit]) {
      const r = ROLE_MAP[explicit];
      if (r === 'textbox' && el.getAttribute('aria-multiline') === 'true') {
        return 'textarea';
      }
      return r;
    }
    switch (tagOf(el)) {
      case 'input': {
        const t = inputType(el);
        if (t === 'checkbox') return 'checkbox';
        if (t === 'radio') return 'radio';
        if (t === 'range') return 'slider';
        if (t === 'file') return 'file';
        if (BUTTON_INPUT_TYPES.has(t)) return 'button';
        if (el.getAttribute('list')) return 'combobox';
        return 'textbox';
      }
      case 'textarea':
        return 'textarea';
      case 'select':
        return el.multiple || el.size > 1 ? 'listbox' : 'combobox';
      case 'button':
        return 'button';
      case 'a':
        return 'link';
      default:
        break;
    }
    if (el.isContentEditable) {
      return el.getAttribute('aria-multiline') === 'false' ? 'textbox' : 'textarea';
    }
    return 'button';
  }

  /** Text of an element, skipping nested selects/textareas, scripts and aria-hidden parts. */
  function textWithoutControls(root) {
    let s = '';
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
      acceptNode(n) {
        if (n.nodeType === Node.TEXT_NODE) {
          return NodeFilter.FILTER_ACCEPT;
        }
        const t = n.tagName;
        if (
          t === 'SELECT' || t === 'TEXTAREA' || t === 'SCRIPT' || t === 'STYLE' ||
          t === 'DATALIST' || t === 'OPTION' || t === 'TEMPLATE' ||
          n.getAttribute('aria-hidden') === 'true'
        ) {
          return NodeFilter.FILTER_REJECT;
        }
        return NodeFilter.FILTER_SKIP;
      },
    });
    while (walker.nextNode()) {
      s += `${walker.currentNode.nodeValue} `;
    }
    return s;
  }

  function idRefText(el, attr) {
    const ids = (el.getAttribute(attr) || '').split(/\s+/).filter(Boolean);
    if (!ids.length) {
      return '';
    }
    const root = el.getRootNode();
    const parts = [];
    for (const id of ids) {
      const ref =
        (typeof root.getElementById === 'function' && root.getElementById(id)) ||
        document.getElementById(id);
      if (ref) {
        parts.push(ref.getAttribute('aria-label') || textWithoutControls(ref));
      }
    }
    return clean(parts.join(' '));
  }

  /** Own visible text of buttons/links (innerText, value of input buttons, img alt, ...). */
  function ownText(el) {
    let t = '';
    if (tagOf(el) === 'input') {
      t = inputType(el) === 'image' ? el.getAttribute('alt') || el.value : el.value;
    } else {
      t = isRendered(el) && typeof el.innerText === 'string' ? el.innerText : el.textContent;
    }
    t = clean(t);
    if (!t) {
      const img = el.querySelector && el.querySelector('img[alt]');
      t = clean(
        (img && img.getAttribute('alt')) || el.getAttribute('aria-label') || el.getAttribute('title'),
      );
    }
    return t;
  }

  function containsControl(node) {
    if (node.nodeType !== Node.ELEMENT_NODE) {
      return false;
    }
    return node.matches(CONTROL_SELECTOR) || !!node.querySelector(CONTROL_SELECTOR);
  }

  /** Label element pointing at another control – its text belongs to that control. */
  function isForeignLabel(node, el) {
    if (node.nodeType !== Node.ELEMENT_NODE || tagOf(node) !== 'label') {
      return false;
    }
    const ctl = node.control;
    return !!ctl && ctl !== el;
  }

  function siblingText(node) {
    if (node.nodeType === Node.TEXT_NODE) {
      return clean(node.nodeValue);
    }
    if (node.nodeType === Node.ELEMENT_NODE && isRendered(node)) {
      return clean(node.innerText);
    }
    return '';
  }

  /**
   * Nearest visible text before (or after) the element within the same container. Walks up
   * at most three ancestor levels and stops as soon as it meets another form control.
   */
  function adjacentText(el, forward) {
    let node = el;
    for (let depth = 0; depth < 3 && node; depth++) {
      let sib = forward ? node.nextSibling : node.previousSibling;
      while (sib) {
        if (containsControl(sib) || isForeignLabel(sib, el)) {
          return '';
        }
        const t = siblingText(sib);
        if (t) {
          return truncate(t, MAX_FALLBACK_TEXT);
        }
        sib = forward ? sib.nextSibling : sib.previousSibling;
      }
      node = node.parentElement;
      if (!node || /^(FORM|FIELDSET|BODY|HTML)$/.test(node.tagName)) {
        break;
      }
    }
    return '';
  }

  /**
   * Label resolution order (spec): aria-labelledby, aria-label, <label for>, wrapping <label>,
   * title, placeholder, preceding visible text in the same container, name.
   * Additions: buttons/links use their own text before placeholder; checkboxes/radios look at
   * the following text before the preceding text (that is where their caption usually is).
   */
  function resolveLabel(el, role) {
    let t = cleanLabel(idRefText(el, 'aria-labelledby'));
    if (t) return t;

    t = cleanLabel(el.getAttribute('aria-label'));
    if (t) return t;

    if (el.id) {
      const root = el.getRootNode();
      if (typeof root.querySelectorAll === 'function') {
        for (const lbl of root.querySelectorAll(`label[for="${CSS.escape(el.id)}"]`)) {
          t = cleanLabel(textWithoutControls(lbl));
          if (t) return t;
        }
      }
    }

    const wrapping = el.parentElement && el.parentElement.closest('label');
    if (wrapping) {
      t = cleanLabel(textWithoutControls(wrapping));
      if (t) return t;
    }

    t = cleanLabel(el.getAttribute('title'));
    if (t) return t;

    const textual = TEXT_ROLES.has(role);
    if (textual) {
      t = cleanLabel(ownText(el));
      if (t) return t;
    }

    t = cleanLabel(el.getAttribute('placeholder') || el.getAttribute('aria-placeholder'));
    if (t) return t;

    if (!textual) {
      if (role === 'checkbox' || role === 'radio' || role === 'switch') {
        t = cleanLabel(adjacentText(el, true));
        if (t) return t;
      }
      t = cleanLabel(adjacentText(el, false));
      if (t) return t;
    }

    t = cleanLabel(el.getAttribute('name'));
    return t || null;
  }

  /** Shadow-DOM aware position anchor for document-order comparisons. */
  function documentAnchor(el) {
    let node = el;
    let root = node.getRootNode();
    while (root && root !== document && root.host) {
      node = root.host;
      root = node.getRootNode();
    }
    return node;
  }

  function collectHeadings() {
    const out = [];
    for (const h of document.querySelectorAll(HEADING_SELECTOR)) {
      if (!isRendered(h)) {
        continue;
      }
      const t = truncate(clean(h.innerText), MAX_SECTION);
      if (t) {
        out.push({ el: h, text: t });
      }
    }
    return out;
  }

  /** Last heading that precedes (or contains) `el` in document order – binary search. */
  function precedingHeading(el, headings) {
    const anchor = documentAnchor(el);
    let lo = 0;
    let hi = headings.length - 1;
    let found = null;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      const h = headings[mid].el;
      const precedes = h === anchor || (h.compareDocumentPosition(anchor) & Node.DOCUMENT_POSITION_FOLLOWING);
      if (precedes) {
        found = headings[mid];
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    return found ? found.text : null;
  }

  /** Section: closest fieldset legend, aria-label(ledby) of form/section/region, heading. */
  function resolveSection(el, headings) {
    const fieldset = el.closest('fieldset');
    if (fieldset) {
      const legend = fieldset.querySelector(':scope > legend');
      const t = legend && truncate(clean(textWithoutControls(legend)), MAX_SECTION);
      if (t) return t;
    }
    for (let n = el.parentElement; n; n = n.parentElement) {
      if (n.matches(REGION_SELECTOR)) {
        const t = clean(n.getAttribute('aria-label')) || idRefText(n, 'aria-labelledby');
        if (t) return truncate(t, MAX_SECTION);
      }
    }
    return precedingHeading(el, headings);
  }

  // ---------------------------------------------------------------------------------------
  // Values & element description
  // ---------------------------------------------------------------------------------------

  function selectValue(el) {
    const selected = Array.from(el.selectedOptions || []).map((o) => o.value);
    return el.multiple ? selected.join(', ') : selected[0] !== undefined ? selected[0] : '';
  }

  function ariaChecked(el) {
    for (const attr of ['aria-checked', 'aria-pressed', 'aria-selected']) {
      const v = el.getAttribute(attr);
      if (v !== null) {
        return v === 'true';
      }
    }
    return null;
  }

  /** Current {value, checked} of an element. Passwords are always masked. */
  function readState(el) {
    const tag = tagOf(el);
    if (tag === 'input') {
      const t = inputType(el);
      if (t === 'checkbox' || t === 'radio') {
        return { value: el.value, checked: !!el.checked };
      }
      if (t === 'password') {
        return { value: el.value ? PASSWORD_MASK : '', checked: null };
      }
      if (t === 'file') {
        return { value: Array.from(el.files || []).map((f) => f.name).join(', '), checked: null };
      }
      if (BUTTON_INPUT_TYPES.has(t)) {
        return { value: null, checked: ariaChecked(el) };
      }
      return { value: truncate(el.value, MAX_VALUE), checked: null };
    }
    if (tag === 'textarea') {
      return { value: truncate(el.value, MAX_VALUE), checked: null };
    }
    if (tag === 'select') {
      return { value: selectValue(el), checked: null };
    }
    if (el.isContentEditable) {
      return { value: truncate(clean(el.innerText), MAX_VALUE), checked: null };
    }
    const role = normalizeRole(el);
    if (role === 'slider') {
      const v = el.getAttribute('aria-valuetext') || el.getAttribute('aria-valuenow');
      return { value: v, checked: null };
    }
    if (role === 'textbox' || role === 'textarea' || role === 'combobox') {
      return { value: truncate(clean(el.innerText || el.textContent), MAX_VALUE), checked: null };
    }
    return { value: null, checked: ariaChecked(el) };
  }

  function optionEntries(el, role) {
    const tag = tagOf(el);
    let list = null;
    if (tag === 'select') {
      list = Array.from(el.options).map((o) => ({
        value: o.value,
        text: truncate(clean(o.text), MAX_OPTION_TEXT),
        selected: o.selected,
        disabled: o.disabled,
      }));
    } else if (tag === 'input' && el.list) {
      list = Array.from(el.list.options || []).map((o) => ({
        value: o.value,
        text: truncate(clean(o.label || o.text || o.value), MAX_OPTION_TEXT),
        selected: o.value === el.value,
        disabled: o.disabled,
      }));
    } else if (role === 'listbox' || role === 'combobox') {
      const container = role === 'listbox' ? el : popupFor(el);
      if (container) {
        list = Array.from(container.querySelectorAll('[role="option" i]')).map((o) => ({
          value: o.getAttribute('data-value') || clean(o.textContent),
          text: truncate(clean(o.textContent), MAX_OPTION_TEXT),
          selected: o.getAttribute('aria-selected') === 'true',
          disabled: o.getAttribute('aria-disabled') === 'true',
        }));
      }
    }
    return list ? list.slice(0, MAX_OPTIONS) : null;
  }

  /** The listbox controlled/owned by a custom combobox (if present in the DOM). */
  function popupFor(el) {
    const root = el.getRootNode();
    for (const attr of ['aria-controls', 'aria-owns']) {
      for (const id of (el.getAttribute(attr) || '').split(/\s+/).filter(Boolean)) {
        const ref =
          (typeof root.getElementById === 'function' && root.getElementById(id)) ||
          document.getElementById(id);
        if (ref) return ref;
      }
    }
    return null;
  }

  /** Absolute href (SVG links expose an SVGAnimatedString instead of a string). */
  function hrefOf(el) {
    let href = typeof el.href === 'string' ? el.href : el.getAttribute('href');
    if (!href) {
      return null;
    }
    try {
      href = new URL(href, document.baseURI).href;
    } catch (_) {
      // keep the raw attribute value
    }
    return truncate(href, MAX_HREF);
  }

  function describe(el, info, headings) {
    const tag = tagOf(el);
    const role = normalizeRole(el);
    const t = inputType(el);
    const { value, checked } = readState(el);
    const rect = info.rect;
    let type = null;
    if (tag === 'input' || tag === 'button' || tag === 'select') {
      type = String(el.type || '').toLowerCase() || null;
    } else if (tag === 'textarea') {
      type = 'textarea';
    }
    return {
      kid: el.getAttribute(ID_ATTR),
      frameId: null, // filled in by the background script
      tag,
      type,
      role,
      label: resolveLabel(el, role),
      name: el.getAttribute('name') || null,
      id: el.id || null,
      placeholder: el.getAttribute('placeholder') || el.getAttribute('aria-placeholder') || null,
      autocomplete: el.getAttribute('autocomplete') || null,
      value,
      checked,
      required: el.required === true || el.getAttribute('aria-required') === 'true',
      disabled: isDisabled(el),
      readonly: isReadOnly(el),
      multiline: role === 'textarea',
      isPassword: t === 'password',
      options: optionEntries(el, role),
      section: resolveSection(el, headings),
      text: TEXT_ROLES.has(role) ? truncate(ownText(el), MAX_BUTTON_TEXT) || null : null,
      href: tag === 'a' ? hrefOf(el) : null,
      rect: { x: round(rect.left), y: round(rect.top), width: round(rect.width), height: round(rect.height) },
      visible: info.visible,
      inViewport: computeInViewport(rect, info.visible),
    };
  }

  // ---------------------------------------------------------------------------------------
  // texts, viewport, frame geometry
  // ---------------------------------------------------------------------------------------

  function collectTexts() {
    const out = [];
    const seen = new Set();
    for (const n of document.querySelectorAll(TEXTS_SELECTOR)) {
      if (out.length >= MAX_TEXTS) {
        break;
      }
      if (tagOf(n) === 'label' && n.control) {
        continue; // labels that belong to a field are reported as the field's label
      }
      if (!isRendered(n)) {
        continue;
      }
      const t = truncate(clean(n.innerText), MAX_TEXT_ENTRY);
      if (!t || seen.has(t)) {
        continue;
      }
      seen.add(t);
      out.push(t);
    }
    return out;
  }

  function scrollingElement() {
    return document.scrollingElement || document.documentElement || document.body;
  }

  function viewportInfo() {
    const se = scrollingElement();
    return {
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      outerWidth: window.outerWidth,
      outerHeight: window.outerHeight,
      screenX: window.screenX,
      screenY: window.screenY,
      devicePixelRatio: window.devicePixelRatio,
      scrollX: round(window.scrollX),
      scrollY: round(window.scrollY),
      scrollHeight: se ? se.scrollHeight : 0,
    };
  }

  /**
   * Geometry needed by the background script to translate iframe-local rects into top-level
   * viewport coordinates: this frame's index in parent.frames (works cross-origin) and the
   * content-box offsets of this document's child frames.
   */
  function frameGeometry() {
    let indexInParent = -1;
    try {
      if (window.parent && window.parent !== window) {
        const frames = window.parent.frames;
        for (let i = 0; i < frames.length; i++) {
          if (frames[i] === window) {
            indexInParent = i;
            break;
          }
        }
      }
    } catch (_) {
      indexInParent = -1;
    }
    const childFrames = [];
    let frameEls = null;
    for (let i = 0; i < window.frames.length; i++) {
      if (!frameEls) {
        frameEls = Array.from(document.querySelectorAll('iframe,frame'));
      }
      const w = window.frames[i];
      const fe = frameEls.find((f) => {
        try {
          return f.contentWindow === w;
        } catch (_) {
          return false;
        }
      });
      if (!fe) {
        continue;
      }
      const r = fe.getBoundingClientRect();
      const cs = getComputedStyle(fe);
      childFrames.push({
        index: i,
        x: r.left + fe.clientLeft + (parseFloat(cs.paddingLeft) || 0),
        y: r.top + fe.clientTop + (parseFloat(cs.paddingTop) || 0),
        visible: computeVisible(fe, r),
      });
    }
    return { indexInParent, childFrames };
  }

  // ---------------------------------------------------------------------------------------
  // domChanged notification (only when running as an extension content script)
  // ---------------------------------------------------------------------------------------

  function canMessageExtension() {
    try {
      const rt = globalThis.chrome && globalThis.chrome.runtime;
      return !!(rt && rt.id && typeof rt.sendMessage === 'function');
    } catch (_) {
      return false;
    }
  }

  /**
   * After each snapshot, watch the DOM once. On the first meaningful change, notify the
   * background (which forwards a `domChanged` event so Kairo can drop its snapshot cache) and
   * stop watching until the next snapshot. At most one message per snapshot.
   */
  function armDomObserver() {
    if (!canMessageExtension()) {
      return;
    }
    if (!domObserver) {
      domObserver = new MutationObserver(() => {
        domObserver.disconnect();
        if (domChangeTimer === null) {
          domChangeTimer = setTimeout(() => {
            domChangeTimer = null;
            try {
              const p = chrome.runtime.sendMessage({ kind: 'kairo:domChanged' });
              if (p && typeof p.catch === 'function') {
                p.catch(() => {});
              }
            } catch (_) {
              // extension was reloaded – nothing to do
            }
          }, 300);
        }
      });
    }
    domObserver.disconnect();
    domObserver.observe(document, {
      childList: true,
      subtree: true,
      characterData: true,
      attributes: true,
      attributeFilter: ['hidden', 'style', 'class', 'disabled', 'open', 'aria-hidden', 'aria-expanded'],
    });
  }

  // ---------------------------------------------------------------------------------------
  // snapshot
  // ---------------------------------------------------------------------------------------

  function snapshot(opts) {
    const o = opts || {};
    const maxElements = clampInt(o.maxElements, 1, HARD_MAX_CANDIDATES, DEFAULT_MAX_ELEMENTS);
    const includeText = o.includeText !== false;

    const candidates = [];
    collectCandidates(document, candidates);
    ensureIds(candidates);

    const infos = candidates.map((el, index) => {
      const rect = el.getBoundingClientRect();
      return { el, index, rect, visible: computeVisible(el, rect) };
    });

    // Visible elements first; invisible ones only if there is room left in the limit.
    const chosen = [];
    for (const info of infos) {
      if (info.visible && chosen.length < maxElements) chosen.push(info);
    }
    for (const info of infos) {
      if (!info.visible && chosen.length < maxElements) chosen.push(info);
    }
    chosen.sort((a, b) => a.index - b.index);

    const headings = collectHeadings();
    const elements = chosen.map((info) => describe(info.el, info, headings));

    armDomObserver();

    return {
      agentVersion: AGENT_VERSION,
      url: location.href,
      title: document.title,
      viewport: viewportInfo(),
      elements,
      texts: includeText ? collectTexts() : [],
      truncated: candidates.length > chosen.length,
      totalElements: candidates.length,
      frame: frameGeometry(),
    };
  }

  // ---------------------------------------------------------------------------------------
  // Actions
  // ---------------------------------------------------------------------------------------

  function centerOf(el) {
    const r = el.getBoundingClientRect();
    return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
  }

  function dispatchPointer(el, type, pt, buttons) {
    const init = {
      bubbles: true,
      cancelable: true,
      composed: true,
      clientX: pt.x,
      clientY: pt.y,
      button: 0,
      buttons,
      view: window,
    };
    if (type.startsWith('pointer') && typeof PointerEvent === 'function') {
      return el.dispatchEvent(
        new PointerEvent(type, { ...init, pointerId: 1, pointerType: 'mouse', isPrimary: true }),
      );
    }
    return el.dispatchEvent(new MouseEvent(type, init));
  }

  /** scrollIntoView + a realistic pointer/mouse sequence + click(). */
  function doClick(el) {
    if (isDisabled(el)) {
      throw kairoError('not_supported', 'Element is disabled');
    }
    el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
    const pt = centerOf(el);
    dispatchPointer(el, 'pointerover', pt, 0);
    dispatchPointer(el, 'mouseover', pt, 0);
    const pointerDownOk = dispatchPointer(el, 'pointerdown', pt, 1);
    const mouseDownOk = dispatchPointer(el, 'mousedown', pt, 1);
    if (pointerDownOk && mouseDownOk && typeof el.focus === 'function') {
      el.focus({ preventScroll: true });
    }
    dispatchPointer(el, 'pointerup', pt, 0);
    dispatchPointer(el, 'mouseup', pt, 0);
    if (typeof el.click === 'function') {
      el.click();
    } else {
      // SVG elements have no click() method.
      el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, composed: true, clientX: pt.x, clientY: pt.y, view: window }));
    }
  }

  function setNativeValue(el, value) {
    const tag = tagOf(el);
    const proto =
      tag === 'textarea'
        ? HTMLTextAreaElement.prototype
        : tag === 'select'
          ? HTMLSelectElement.prototype
          : HTMLInputElement.prototype;
    const desc = Object.getOwnPropertyDescriptor(proto, 'value');
    desc.set.call(el, value);
  }

  function fireInputAndChange(el, data) {
    let inputEvent;
    try {
      inputEvent = new InputEvent('input', {
        bubbles: true,
        composed: true,
        inputType: 'insertText',
        data: data == null ? null : String(data),
      });
    } catch (_) {
      inputEvent = new Event('input', { bubbles: true, composed: true });
    }
    el.dispatchEvent(inputEvent);
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }

  /** Converts German/ISO-ish dates ("25.09.2026") into the format a date input expects. */
  function normalizeDateValue(type, v) {
    const m = /^\s*(\d{1,2})\.(\d{1,2})\.(\d{4})(?:[ ,T]+(\d{1,2}):(\d{2}))?\s*$/.exec(v);
    if (!m) {
      return v;
    }
    const d = `${m[3]}-${m[2].padStart(2, '0')}-${m[1].padStart(2, '0')}`;
    if (type === 'date') return d;
    if (type === 'month') return d.slice(0, 7);
    if (type === 'datetime-local') return `${d}T${(m[4] || '00').padStart(2, '0')}:${m[5] || '00'}`;
    return v;
  }

  function isTextInput(el) {
    const tag = tagOf(el);
    if (tag === 'textarea') return true;
    if (tag !== 'input') return false;
    const t = inputType(el);
    return !BUTTON_INPUT_TYPES.has(t) && !['checkbox', 'radio', 'file', 'range', 'color'].includes(t);
  }

  /** The element that actually receives text: el itself or a single editable descendant. */
  function fillTarget(el) {
    if (tagOf(el) === 'input' || tagOf(el) === 'textarea' || tagOf(el) === 'select' || el.isContentEditable) {
      return el;
    }
    const inner = el.querySelectorAll('input:not([type="hidden" i]),textarea,select,[contenteditable]');
    const editable = Array.from(inner).filter((n) => tagOf(n) !== 'input' || isTextInput(n) || n.isContentEditable);
    return editable.length === 1 ? editable[0] : el;
  }

  function fillContentEditable(el, str) {
    el.focus();
    const sel = window.getSelection();
    let selected = false;
    try {
      selected = document.execCommand('selectAll', false, null);
    } catch (_) {
      selected = false;
    }
    if (!selected || !sel || sel.rangeCount === 0 || !el.contains(sel.getRangeAt(0).commonAncestorContainer)) {
      const range = document.createRange();
      range.selectNodeContents(el);
      sel.removeAllRanges();
      sel.addRange(range);
    }
    let ok = false;
    try {
      ok = str === '' ? document.execCommand('delete', false, null) : document.execCommand('insertText', false, str);
    } catch (_) {
      ok = false;
    }
    const collapse = (s) => String(s || '').replace(/\s+/g, ' ').trim();
    const current = collapse(el.innerText);
    const wanted = collapse(str);
    if (!ok || (wanted ? !current.includes(wanted) : current !== '')) {
      // Fallback for editors where execCommand is unavailable or was swallowed.
      el.textContent = str;
      fireInputAndChange(el, str);
    }
  }

  function doFill(el, value) {
    const target = fillTarget(el);
    const str = value == null ? '' : String(value);
    const tag = tagOf(target);
    const t = inputType(target);

    if (tag === 'select') {
      doSelect(target, str);
      return target;
    }
    if (t === 'checkbox' || t === 'radio') {
      setChecked(target, TRUTHY_VALUES.has(str.trim().toLowerCase()));
      return target;
    }
    if (isDisabled(target)) {
      throw kairoError('not_supported', 'Element is disabled');
    }
    if (isReadOnly(target)) {
      throw kairoError('not_supported', 'Element is read-only');
    }
    if (tag === 'input' || tag === 'textarea') {
      if (!isTextInput(target) && t !== 'range' && t !== 'color') {
        throw kairoError('not_supported', `Cannot fill an input of type "${t}"`);
      }
      let v = DATE_INPUT_TYPES.has(t) ? normalizeDateValue(t, str) : str;
      if (target.maxLength > 0 && v.length > target.maxLength) {
        v = v.slice(0, target.maxLength); // a typing user could not enter more either
      }
      target.focus({ preventScroll: false });
      setNativeValue(target, v);
      fireInputAndChange(target, v);
      return target;
    }
    if (target.isContentEditable) {
      fillContentEditable(target, str);
      return target;
    }
    throw kairoError('not_supported', 'Element is not a fillable field');
  }

  function currentChecked(el) {
    const t = inputType(el);
    if (t === 'checkbox' || t === 'radio') {
      return !!el.checked;
    }
    return ariaChecked(el);
  }

  /** A native checkbox/radio inside a label/wrapper, if the element itself is not checkable. */
  function checkTarget(el) {
    if (currentChecked(el) !== null) {
      return el;
    }
    if (tagOf(el) === 'label' && el.control) {
      return el.control;
    }
    const inner = el.querySelectorAll('input[type="checkbox" i],input[type="radio" i],[role="checkbox" i],[role="switch" i],[role="radio" i]');
    return inner.length === 1 ? inner[0] : el;
  }

  function setChecked(el, want) {
    const target = checkTarget(el);
    let cur = currentChecked(target);
    if (cur === null) {
      throw kairoError('not_supported', 'Element is not checkable');
    }
    if (cur === want) {
      return target;
    }
    if (!want && (inputType(target) === 'radio' || normalizeRole(target) === 'radio')) {
      throw kairoError('not_supported', 'A radio button cannot be unchecked – select another option instead');
    }
    doClick(target);
    cur = currentChecked(target);
    if (cur !== want && target.labels && target.labels.length) {
      // Custom-styled controls sometimes only react to clicks on their label.
      doClick(target.labels[0]);
      cur = currentChecked(target);
    }
    if (cur !== want) {
      throw kairoError('script_error', 'The checked state did not change', { checked: cur });
    }
    return target;
  }

  /** Match order: exact value, exact text, case-insensitive text/value, substring of text. */
  function matchOption(options, wanted, getValue, getText) {
    const w = String(wanted == null ? '' : wanted);
    const wc = clean(w);
    const wl = wc.toLowerCase();
    const enabled = options.filter((o) => !o.disabled);
    return (
      enabled.find((o) => getValue(o) === w) ||
      enabled.find((o) => clean(getText(o)) === wc) ||
      enabled.find((o) => clean(getText(o)).toLowerCase() === wl || String(getValue(o)).toLowerCase() === wl) ||
      (wl ? enabled.find((o) => clean(getText(o)).toLowerCase().includes(wl)) : undefined) ||
      null
    );
  }

  function doSelect(el, value) {
    if (isDisabled(el)) {
      throw kairoError('not_supported', 'Element is disabled');
    }
    const wantedList = Array.isArray(value) ? value : [value];
    const options = Array.from(el.options).map((o) => ({ o, disabled: o.disabled }));
    const matches = [];
    for (const w of wantedList) {
      const m = matchOption(options, w, (x) => x.o.value, (x) => x.o.text);
      if (!m) {
        throw kairoError('element_not_found', `No option matching "${w}"`);
      }
      matches.push(m.o);
    }
    el.focus({ preventScroll: false });
    if (el.multiple) {
      for (const o of el.options) {
        o.selected = matches.includes(o);
      }
    } else {
      setNativeValue(el, matches[0].value);
      if (el.selectedIndex !== matches[0].index) {
        el.selectedIndex = matches[0].index;
      }
    }
    fireInputAndChange(el, null);
  }

  function visibleOptions(scope) {
    return Array.from(scope.querySelectorAll('[role="option" i]')).filter(isRendered);
  }

  /** Waits (MutationObserver + deadline) until `probe()` returns a truthy value. */
  function waitFor(probe, timeoutMs) {
    const immediate = probe();
    if (immediate) {
      return Promise.resolve(immediate);
    }
    return new Promise((resolve) => {
      let done = false;
      let timer = null;
      const obs = new MutationObserver(() => {
        const v = probe();
        if (v) finish(v);
      });
      function finish(v) {
        if (done) return;
        done = true;
        obs.disconnect();
        clearTimeout(timer);
        resolve(v);
      }
      obs.observe(document, { childList: true, subtree: true, attributes: true });
      timer = setTimeout(() => finish(probe() || null), timeoutMs);
    });
  }

  /** `select` for custom ARIA widgets (listbox / combobox with role=option popups). */
  async function doSelectCustom(el, value, role) {
    const getValue = (o) => o.getAttribute('data-value') || clean(o.textContent);
    const getText = (o) => o.textContent;
    const toCandidates = (list) => list.map((o) => ({ o, disabled: o.getAttribute('aria-disabled') === 'true', v: getValue(o), t: getText(o) }));
    const pick = (list) => matchOption(toCandidates(list), value, (x) => x.v, (x) => x.t);

    if (role === 'listbox') {
      const m = pick(Array.from(el.querySelectorAll('[role="option" i]')));
      if (!m) throw kairoError('element_not_found', `No option matching "${value}"`);
      doClick(m.o);
      return;
    }
    const scope = () => {
      const p = popupFor(el);
      return p && p.isConnected ? p : document;
    };
    if (isTextInput(el)) {
      // Editable combobox (autocomplete, react-select, ...): options usually appear only
      // after typing. Type the value, then pick the matching option if a list shows up.
      doFill(el, value);
      await waitFor(() => visibleOptions(scope()).length > 0, OPTION_WAIT_MS);
      const shown = visibleOptions(scope());
      if (!shown.length) {
        return; // free-text combobox without suggestions: the typed value stands
      }
      const typed = pick(shown);
      if (!typed) {
        throw kairoError('element_not_found', `No option matching "${value}"`);
      }
      doClick(typed.o);
      return;
    }
    // Select-only combobox: open it, wait for options to render, click the matching option.
    let m = pick(visibleOptions(scope()));
    if (!m) {
      doClick(el);
      const found = await waitFor(() => pick(visibleOptions(scope())), OPTION_WAIT_MS);
      m = found || null;
    }
    if (!m) {
      throw kairoError('element_not_found', `No option matching "${value}"`);
    }
    doClick(m.o);
  }

  function defaultButton(form) {
    const isSubmit = (n) => {
      const tag = tagOf(n);
      if (tag === 'button') return (n.getAttribute('type') || 'submit').toLowerCase() === 'submit';
      return tag === 'input' && (inputType(n) === 'submit' || inputType(n) === 'image');
    };
    return Array.from(form.elements || []).find(isSubmit) || null;
  }

  function doPressEnter(el) {
    if (typeof el.focus === 'function') {
      el.focus({ preventScroll: false });
    }
    const init = {
      key: 'Enter',
      code: 'Enter',
      keyCode: 13,
      which: 13,
      bubbles: true,
      cancelable: true,
      composed: true,
      view: window,
    };
    const notPrevented = el.dispatchEvent(new KeyboardEvent('keydown', init));
    el.dispatchEvent(new KeyboardEvent('keypress', { ...init, charCode: 13 }));
    el.dispatchEvent(new KeyboardEvent('keyup', init));
    // Implicit form submission like a real Enter: click the default button if there is one,
    // otherwise requestSubmit() (runs constraint validation and the submit event).
    if (notPrevented && tagOf(el) === 'input' && isTextInput(el) && el.form) {
      const btn = defaultButton(el.form);
      if (btn) {
        if (!isDisabled(btn)) btn.click();
      } else if (typeof el.form.requestSubmit === 'function') {
        el.form.requestSubmit();
      } else {
        el.form.submit();
      }
    }
  }

  function resultFor(el) {
    const s = el && el.isConnected ? readState(el) : { value: null, checked: null };
    return { done: true, value: s.value, checked: s.checked };
  }

  async function act(kid, action, value) {
    if (!ACTIONS.has(action)) {
      throw kairoError('bad_request', `Unknown action "${action}"`);
    }
    const el = requireElement(kid);
    switch (action) {
      case 'fill':
        return resultFor(doFill(el, value));
      case 'clear':
        return resultFor(doFill(el, ''));
      case 'click':
        doClick(el);
        return resultFor(el);
      case 'check':
        return resultFor(setChecked(el, true));
      case 'uncheck':
        return resultFor(setChecked(el, false));
      case 'select': {
        if (value == null) {
          throw kairoError('bad_request', 'select requires a value');
        }
        const target = fillTarget(el);
        const tag = tagOf(target);
        const role = normalizeRole(target);
        if (tag === 'select') {
          doSelect(target, value);
        } else if (tag === 'input' && target.list) {
          const opts = Array.from(target.list.options).map((o) => ({ o, disabled: o.disabled }));
          const m = matchOption(opts, value, (x) => x.o.value, (x) => x.o.label || x.o.text || x.o.value);
          doFill(target, m ? m.o.value : value);
        } else if (role === 'listbox' || role === 'combobox') {
          await doSelectCustom(target, value, role);
        } else if (role === 'radio' || role === 'checkbox' || role === 'option' || role === 'tab') {
          doClick(target);
        } else {
          throw kairoError('not_supported', 'Element does not support select');
        }
        return resultFor(target);
      }
      case 'focus':
        el.focus({ preventScroll: false });
        return resultFor(el);
      case 'scrollIntoView':
        el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
        return resultFor(el);
      case 'pressEnter':
        doPressEnter(el);
        return resultFor(el);
      default:
        throw kairoError('bad_request', `Unknown action "${action}"`);
    }
  }

  /** Runs actions in order; a failure does not stop the batch. */
  async function actBatch(actions) {
    if (!Array.isArray(actions)) {
      throw kairoError('bad_request', 'actions must be an array');
    }
    const results = [];
    for (let i = 0; i < actions.length; i++) {
      const a = actions[i] || {};
      try {
        const r = await act(a.kid, a.action, a.value);
        results.push({ kid: a.kid, ok: true, value: r.value, checked: r.checked, error: null });
      } catch (e) {
        results.push({
          kid: a.kid,
          ok: false,
          value: e && e.value !== undefined ? e.value : null,
          checked: e && e.checked !== undefined ? e.checked : null,
          error: { code: (e && e.code) || 'script_error', message: String((e && e.message) || e) },
        });
      }
      if (i < actions.length - 1) {
        // Let frameworks (React, Vue, ...) flush their updates before the next action.
        await yieldToEventLoop();
      }
    }
    return { results };
  }

  function readValues(kids) {
    if (!Array.isArray(kids)) {
      throw kairoError('bad_request', 'kids must be an array');
    }
    const values = {};
    for (const kid of kids) {
      const el = findElement(kid);
      if (!el) {
        values[kid] = { exists: false, value: null, checked: null };
      } else {
        const s = readState(el);
        values[kid] = { exists: true, value: s.value, checked: s.checked };
        if (tagOf(el) === 'select') {
          // The option's label ("Schweiz") next to its value ("CH"): the agent selects options by label.
          values[kid].selectedText = Array.from(el.selectedOptions || []).map((o) => clean(o.textContent)).join(', ');
        }
      }
    }
    return { values };
  }

  // ---------------------------------------------------------------------------------------
  // scroll
  // ---------------------------------------------------------------------------------------

  function isScrollable(n) {
    if (!n || n.nodeType !== Node.ELEMENT_NODE) return false;
    const oy = getComputedStyle(n).overflowY;
    return (oy === 'auto' || oy === 'scroll' || oy === 'overlay') && n.scrollHeight > n.clientHeight + 1;
  }

  /** When the window itself cannot scroll, find the scroll container under the viewport center. */
  function mainScroller() {
    const se = scrollingElement();
    if (se && se.scrollHeight > window.innerHeight + 1) {
      return null;
    }
    const hits = document.elementsFromPoint(window.innerWidth / 2, window.innerHeight / 2);
    for (const hit of hits) {
      for (let n = hit; n && n !== se; n = n.parentElement) {
        if (isScrollable(n)) return n;
      }
    }
    return null;
  }

  function scroll(direction, amount) {
    const dirs = ['up', 'down', 'top', 'bottom'];
    if (!dirs.includes(direction)) {
      throw kairoError('bad_request', `direction must be one of ${dirs.join(', ')}`);
    }
    const target = mainScroller();
    const viewportHeight = target ? target.clientHeight : window.innerHeight;
    const amt = Number(amount) > 0 ? Number(amount) : Math.round(viewportHeight * 0.8);
    const scroller = target || window;
    if (direction === 'up') scroller.scrollBy({ top: -amt, behavior: 'instant' });
    if (direction === 'down') scroller.scrollBy({ top: amt, behavior: 'instant' });
    if (direction === 'top') scroller.scrollTo({ top: 0, behavior: 'instant' });
    if (direction === 'bottom') {
      const h = target ? target.scrollHeight : scrollingElement().scrollHeight;
      scroller.scrollTo({ top: h, behavior: 'instant' });
    }
    if (target) {
      return { scrollY: round(target.scrollTop), scrollHeight: target.scrollHeight };
    }
    return { scrollY: round(window.scrollY), scrollHeight: scrollingElement().scrollHeight };
  }

  // ---------------------------------------------------------------------------------------
  // Public API
  // ---------------------------------------------------------------------------------------

  Object.defineProperty(globalThis, '__kairoAgent', {
    value: Object.freeze({
      version: AGENT_VERSION,
      snapshot,
      act,
      actBatch,
      readValues,
      scroll,
    }),
    configurable: false,
    enumerable: false,
    writable: false,
  });
})();
