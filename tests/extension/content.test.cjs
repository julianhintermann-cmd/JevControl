'use strict';
/*
 * Tests for extension/content.js (the per-frame agent) against a realistic German contact
 * form. The agent is injected into the page's main world with page.addScriptTag, so these
 * tests exercise the DOM logic without the extension machinery.
 */

const { describe, test, before, after } = require('node:test');
const assert = require('node:assert/strict');
const { CONTENT_JS, FIXTURES_DIR, loadPlaywright, startStaticServer } = require('./helpers.cjs');

const { chromium } = loadPlaywright();

let browser;
let server;

before(async () => {
  server = await startStaticServer(FIXTURES_DIR);
  browser = await chromium.launch({ headless: true });
});

after(async () => {
  if (browser) await browser.close();
  if (server) await server.close();
});

async function openForm() {
  const page = await browser.newPage({ viewport: { width: 1200, height: 800 } });
  await page.goto(`${server.url}/contact-form.html`);
  await page.addScriptTag({ path: CONTENT_JS });
  return page;
}

function snapshot(target, opts = {}) {
  return target.evaluate((o) => window.__kairoAgent.snapshot(o), opts);
}

/** Calls an agent method and returns {ok, result} or {ok:false, code, message}. */
function call(target, method, ...args) {
  return target.evaluate(
    async (req) => {
      try {
        return { ok: true, result: await window.__kairoAgent[req.method](...req.args) };
      } catch (e) {
        return { ok: false, code: e.code, message: e.message };
      }
    },
    { method, args },
  );
}

async function act(target, kid, action, value) {
  const r = await call(target, 'act', kid, action, value);
  assert.ok(r.ok, `act ${action} on ${kid} failed: ${r.code} ${r.message}`);
  return r.result;
}

function byLabel(snap, label) {
  const el = snap.elements.find((e) => e.label === label);
  assert.ok(el, `element with label "${label}" not found; labels: ${snap.elements.map((e) => e.label).join(' | ')}`);
  return el;
}

function byName(snap, name) {
  const el = snap.elements.find((e) => e.name === name);
  assert.ok(el, `element with name "${name}" not found`);
  return el;
}

describe('content agent: snapshot', () => {
  test('finds the form fields with labels from all label mechanisms', async () => {
    const page = await openForm();
    const s = await snapshot(page);

    // label[for]
    const vorname = byLabel(s, 'Vorname');
    assert.equal(vorname.tag, 'input');
    assert.equal(vorname.type, 'text');
    assert.equal(vorname.role, 'textbox');
    assert.equal(vorname.required, true);
    assert.equal(vorname.autocomplete, 'given-name');
    assert.equal(vorname.id, 'vorname');
    assert.equal(vorname.name, 'vorname');
    assert.match(vorname.kid, /^k\d+$/);
    assert.equal(vorname.visible, true);
    assert.equal(vorname.inViewport, true);
    assert.equal(vorname.multiline, false);
    assert.equal(vorname.isPassword, false);
    assert.equal(vorname.checked, null);
    assert.equal(vorname.value, '');
    assert.ok(vorname.rect.width > 0 && vorname.rect.height > 0);

    // wrapping <label>
    assert.equal(byLabel(s, 'Nachname').name, 'nachname');

    // label[for] + type=email + autocomplete + placeholder
    const email = byLabel(s, 'E-Mail-Adresse');
    assert.equal(email.type, 'email');
    assert.equal(email.role, 'textbox');
    assert.equal(email.autocomplete, 'email');
    assert.equal(email.placeholder, 'name@example.com');
    assert.equal(email.required, true);

    // aria-labelledby with two ids
    assert.equal(byLabel(s, 'Telefon (optional)').type, 'tel');

    // placeholder only
    assert.equal(byLabel(s, 'Firma').name, 'firma');

    // aria-label on a select
    const land = byLabel(s, 'Land');
    assert.equal(land.tag, 'select');
    assert.equal(land.role, 'combobox');
    assert.deepEqual(
      land.options.map((o) => o.text),
      ['Bitte wählen', 'Schweiz', 'Deutschland', 'Österreich'],
    );
    assert.equal(land.options[0].selected, true);

    // preceding visible text in the same container
    assert.equal(byLabel(s, 'Postleitzahl').name, 'plz');

    // title attribute
    assert.equal(byLabel(s, 'Webseite').type, 'url');

    // textarea
    const nachricht = byLabel(s, 'Nachricht');
    assert.equal(nachricht.role, 'textarea');
    assert.equal(nachricht.multiline, true);

    // contenteditable
    const notiz = byLabel(s, 'Interne Notiz');
    assert.equal(notiz.tag, 'div');
    assert.equal(notiz.role, 'textarea');

    // checkboxes (wrapping label / label[for])
    const ds = byLabel(s, 'Ich akzeptiere die Datenschutzerklärung');
    assert.equal(ds.role, 'checkbox');
    assert.equal(ds.required, true);
    assert.equal(ds.checked, false);
    assert.equal(byLabel(s, 'Newsletter abonnieren').checked, false);

    // disabled field
    const kunde = byLabel(s, 'Kundennummer');
    assert.equal(kunde.disabled, true);
    assert.equal(kunde.value, 'K-1001');

    // custom ARIA combobox
    const anrede = byLabel(s, 'Anrede');
    assert.equal(anrede.role, 'combobox');
    assert.equal(anrede.tag, 'div');
    assert.deepEqual(anrede.options.map((o) => o.text), ['Herr', 'Frau', 'Divers']);

    // submit button & links
    const absenden = s.elements.find((e) => e.role === 'button' && e.text === 'Absenden');
    assert.ok(absenden, 'submit button');
    assert.equal(absenden.type, 'submit');
    assert.equal(absenden.label, 'Absenden');
    const link = s.elements.find((e) => e.role === 'link' && e.text === 'Produkte');
    assert.ok(link, 'navigation link');
    assert.equal(link.href, `${server.url}/produkte`);
    assert.equal(link.section, null);

    // viewport info
    assert.equal(s.viewport.innerWidth, 1200);
    assert.equal(s.viewport.innerHeight, 800);
    assert.ok(s.viewport.scrollHeight > 1600);
    assert.equal(s.truncated, false);
    await page.close();
  });

  test('detects sections: fieldset legend, aria-label of section, preceding heading', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    assert.equal(byLabel(s, 'Vorname').section, 'Persönliche Angaben');
    assert.equal(byLabel(s, 'Telefon (optional)').section, 'Persönliche Angaben');
    assert.equal(byLabel(s, 'Firma').section, 'Firmendaten');
    assert.equal(byLabel(s, 'Land').section, 'Firmendaten');
    assert.equal(byLabel(s, 'Nachricht').section, 'Ihre Nachricht');
    assert.equal(byLabel(s, 'Passwort').section, 'Zugang');
    assert.equal(byLabel(s, 'Newsletter abonnieren').section, 'Zugang');
    await page.close();
  });

  test('excludes type=hidden, reports display:none as invisible', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    assert.equal(s.elements.find((e) => e.name === 'csrf'), undefined, 'hidden input must not be reported');
    assert.ok(!JSON.stringify(s).includes('geheimes-token-123'), 'hidden value must not leak');
    const honeypot = byName(s, 'honeypot');
    assert.equal(honeypot.visible, false);
    assert.equal(honeypot.inViewport, false);
    // The hidden custom listbox options are invisible, too.
    const herr = s.elements.find((e) => e.role === 'option' && e.text === 'Herr');
    assert.equal(herr.visible, false);
    await page.close();
  });

  test('invisible elements only fill up remaining room in maxElements', async () => {
    const page = await openForm();
    const full = await snapshot(page);
    const visibleCount = full.elements.filter((e) => e.visible).length;
    assert.ok(full.elements.some((e) => !e.visible));
    const limited = await snapshot(page, { maxElements: visibleCount });
    assert.equal(limited.elements.length, visibleCount);
    assert.ok(limited.elements.every((e) => e.visible), 'only visible elements when the limit is tight');
    assert.equal(limited.truncated, true);
    const tiny = await snapshot(page, { maxElements: 3 });
    assert.equal(tiny.elements.length, 3);
    assert.equal(tiny.truncated, true);
    await page.close();
  });

  test('masks password values', async () => {
    const page = await openForm();
    let s = await snapshot(page);
    const pw = byLabel(s, 'Passwort');
    assert.equal(pw.isPassword, true);
    assert.equal(pw.value, '');
    await page.fill('#passwort', 'Sehr-Geheim-42!');
    s = await snapshot(page);
    assert.equal(byLabel(s, 'Passwort').value, '••••');
    assert.ok(!JSON.stringify(s).includes('Sehr-Geheim-42!'), 'password must never leave the page');
    const r = await call(page, 'readValues', [pw.kid]);
    assert.equal(r.result.values[pw.kid].value, '••••');
    const filled = await act(page, pw.kid, 'fill', 'Neues-Passwort-7');
    assert.equal(filled.value, '••••');
    assert.equal(await page.inputValue('#passwort'), 'Neues-Passwort-7');
    await page.close();
  });

  test('keeps ids stable across snapshots and re-injection', async () => {
    const page = await openForm();
    const a = await snapshot(page);
    await page.addScriptTag({ path: CONTENT_JS }); // idempotent re-injection
    const b = await snapshot(page);
    assert.deepEqual(
      a.elements.map((e) => [e.kid, e.label]),
      b.elements.map((e) => [e.kid, e.label]),
    );
    const vornameKid = byLabel(a, 'Vorname').kid;
    assert.equal(await page.getAttribute('#vorname', 'data-kairo-id'), vornameKid);

    // New elements get fresh ids; existing ones keep theirs.
    await page.evaluate(() => {
      const input = document.createElement('input');
      input.setAttribute('aria-label', 'Neues Feld');
      document.querySelector('#contact').prepend(input);
    });
    const c = await snapshot(page);
    assert.equal(byLabel(c, 'Vorname').kid, vornameKid);
    const neu = byLabel(c, 'Neues Feld');
    assert.ok(!a.elements.some((e) => e.kid === neu.kid), 'new element must get a new id');
    await page.close();
  });

  test('re-assigns duplicated ids (e.g. cloned nodes)', async () => {
    const page = await openForm();
    const a = await snapshot(page);
    const vornameKid = byLabel(a, 'Vorname').kid;
    await page.evaluate(() => {
      const clone = document.querySelector('#vorname').cloneNode();
      clone.id = 'vorname-kopie';
      clone.setAttribute('aria-label', 'Kopie');
      document.querySelector('#vorname').after(clone);
    });
    const b = await snapshot(page);
    assert.equal(byLabel(b, 'Vorname').kid, vornameKid);
    assert.notEqual(byLabel(b, 'Kopie').kid, vornameKid);
    const kids = b.elements.map((e) => e.kid);
    assert.equal(new Set(kids).size, kids.length, 'kids must be unique');
    await page.close();
  });

  test('collects texts: headline, error message, headings; skips hidden ones', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    assert.ok(s.texts.includes('Kontaktformular'), s.texts.join(' | '));
    assert.ok(s.texts.includes('Bitte füllen Sie alle Pflichtfelder aus.'));
    assert.ok(s.texts.includes('Persönliche Angaben'), 'legend');
    assert.ok(s.texts.includes('Ihre Nachricht'));
    assert.ok(!s.texts.includes('Bitte geben Sie eine gültige E-Mail-Adresse ein.'), 'hidden feedback');
    assert.ok(!s.texts.some((t) => t.startsWith('Vorname')), 'labels with a field are not texts');
    assert.ok(s.texts.length <= 60);
    const noText = await snapshot(page, { includeText: false });
    assert.deepEqual(noText.texts, []);
    await page.close();
  });
});

describe('content agent: actions', () => {
  test('fill uses the native setter so React-like controlled inputs update', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const user = byLabel(s, 'Benutzername');

    // Sanity check of the fixture: a naive assignment does NOT reach the state.
    await page.evaluate(() => {
      const el = document.getElementById('username');
      el.value = 'naiv';
      el.dispatchEvent(new Event('input', { bubbles: true }));
    });
    assert.equal(await page.evaluate(() => window.__reactState.username), '');

    const r = await act(page, user.kid, 'fill', 'max.muster');
    assert.deepEqual(r, { done: true, value: 'max.muster', checked: null });
    assert.equal(await page.inputValue('#username'), 'max.muster');
    assert.equal(await page.evaluate(() => window.__reactState.username), 'max.muster');
    assert.equal(await page.textContent('#state'), '{"username":"max.muster"}');

    // input + change events bubble
    await page.evaluate(() => {
      window.__events = [];
      const f = document.getElementById('vorname');
      f.form.addEventListener('input', (e) => window.__events.push(`input:${e.target.name}`));
      f.form.addEventListener('change', (e) => window.__events.push(`change:${e.target.name}`));
    });
    await act(page, byLabel(s, 'Vorname').kid, 'fill', 'Anna');
    assert.deepEqual(await page.evaluate(() => window.__events), ['input:vorname', 'change:vorname']);
    assert.equal(await page.evaluate(() => document.activeElement.id), 'vorname');

    const cleared = await act(page, byLabel(s, 'Vorname').kid, 'clear');
    assert.equal(cleared.value, '');
    assert.equal(await page.inputValue('#vorname'), '');
    await page.close();
  });

  test('fill works for textarea and contenteditable', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const text = 'Guten Tag,\nich hätte gerne eine Offerte.';
    await act(page, byLabel(s, 'Nachricht').kid, 'fill', text);
    assert.equal(await page.inputValue('#nachricht'), text);

    const r = await act(page, byLabel(s, 'Interne Notiz').kid, 'fill', 'Rückruf am Montag');
    assert.equal(r.value, 'Rückruf am Montag');
    assert.equal((await page.innerText('#notiz')).trim(), 'Rückruf am Montag');
    await act(page, byLabel(s, 'Interne Notiz').kid, 'fill', 'Neu');
    assert.equal((await page.innerText('#notiz')).trim(), 'Neu');
    await page.close();
  });

  test('select matches value, exact text, case-insensitive text and substring', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const land = byLabel(s, 'Land').kid;
    await page.evaluate(() => {
      window.__changes = 0;
      document.querySelector('select[name=land]').addEventListener('change', () => window.__changes++);
    });

    assert.equal((await act(page, land, 'select', 'CH')).value, 'CH');
    assert.equal((await act(page, land, 'select', 'Deutschland')).value, 'DE');
    assert.equal((await act(page, land, 'select', 'schweiz')).value, 'CH');
    assert.equal((await act(page, land, 'select', 'Österr')).value, 'AT');
    assert.equal(await page.inputValue('select[name=land]'), 'AT');
    assert.equal(await page.evaluate(() => window.__changes), 4);

    const missing = await call(page, 'act', land, 'select', 'Frankreich');
    assert.equal(missing.ok, false);
    assert.equal(missing.code, 'element_not_found');
    // fill on a select behaves like select
    assert.equal((await act(page, land, 'fill', 'deutschland')).value, 'DE');
    await page.close();
  });

  test('select works on a custom ARIA combobox', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const anrede = byLabel(s, 'Anrede').kid;
    const r = await act(page, anrede, 'select', 'frau');
    assert.equal(r.value, 'Frau');
    assert.equal(await page.getAttribute('#anrede', 'data-value'), 'frau');
    assert.equal(await page.isHidden('#anrede-list'), true);
    await page.close();
  });

  test('select on an editable autocomplete combobox types and picks a suggestion', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const ort = byLabel(s, 'Ort');
    assert.equal(ort.role, 'combobox');
    assert.equal(ort.tag, 'input');
    assert.equal(ort.section, 'Firmendaten');

    assert.equal((await act(page, ort.kid, 'select', 'bas')).value, 'Basel');
    assert.equal(await page.getAttribute('#ort', 'data-selected'), 'Basel');
    assert.equal(await page.isHidden('#ort-list'), true);

    // No suggestions at all: the typed free text stays.
    assert.equal((await act(page, ort.kid, 'select', 'Winterthur')).value, 'Winterthur');
    // Suggestions shown, but none matches.
    await page.evaluate(() => {
      const list = document.getElementById('ort-list');
      document.getElementById('ort').addEventListener('input', () => {
        list.innerHTML = '<li role="option">Genf</li>';
        list.hidden = false;
      });
    });
    const miss = await call(page, 'act', ort.kid, 'select', 'Chur');
    assert.equal(miss.code, 'element_not_found');
    await page.close();
  });

  test('fill respects maxlength and converts German dates for date inputs', async () => {
    const page = await openForm();
    await page.evaluate(() => {
      const d = document.createElement('input');
      d.type = 'date';
      d.setAttribute('aria-label', 'Wunschtermin');
      document.querySelector('#contact').append(d);
    });
    const s = await snapshot(page);
    assert.equal((await act(page, byLabel(s, 'Postleitzahl').kid, 'fill', '1234567')).value, '12345');
    assert.equal((await act(page, byLabel(s, 'Wunschtermin').kid, 'fill', '5.10.2026')).value, '2026-10-05');
    assert.equal((await act(page, byLabel(s, 'Wunschtermin').kid, 'fill', '2026-12-24')).value, '2026-12-24');
    await page.close();
  });

  test('check / uncheck only click when the state differs', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const ds = byLabel(s, 'Ich akzeptiere die Datenschutzerklärung').kid;
    const nl = byLabel(s, 'Newsletter abonnieren').kid;
    await page.evaluate(() => {
      window.__clicks = 0;
      document.querySelector('input[name=datenschutz]').addEventListener('click', () => window.__clicks++);
    });

    assert.equal((await act(page, ds, 'check')).checked, true);
    assert.equal((await act(page, ds, 'check')).checked, true);
    assert.equal(await page.evaluate(() => window.__clicks), 1, 'second check must not click');
    assert.equal(await page.isChecked('input[name=datenschutz]'), true);

    assert.equal((await act(page, nl, 'uncheck')).checked, false);
    assert.equal((await act(page, nl, 'check')).checked, true);
    assert.equal((await act(page, nl, 'uncheck')).checked, false);
    assert.equal(await page.isChecked('#newsletter'), false);
    await page.close();
  });

  test('click, focus, scrollIntoView and pressEnter', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    await act(page, byLabel(s, 'E-Mail-Adresse').kid, 'focus');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'email');

    await act(page, byLabel(s, 'Absenden').kid, 'scrollIntoView');
    const top = await page.evaluate(() => document.querySelector('button[type=submit]').getBoundingClientRect().top);
    assert.ok(top > 0 && top < 800);

    // Required fields are empty: Enter must not submit (constraint validation).
    await act(page, byLabel(s, 'Vorname').kid, 'pressEnter');
    assert.equal(await page.evaluate(() => window.__submitted), 0);

    const fill = (label, v) => act(page, byLabel(s, label).kid, 'fill', v);
    await fill('Vorname', 'Anna');
    await fill('Nachname', 'Muster');
    await fill('E-Mail-Adresse', 'anna@example.com');
    await fill('Nachricht', 'Hallo');
    await act(page, byLabel(s, 'Ich akzeptiere die Datenschutzerklärung').kid, 'check');
    await act(page, byLabel(s, 'Vorname').kid, 'pressEnter');
    assert.equal(await page.evaluate(() => window.__submitted), 1);

    await act(page, byLabel(s, 'Absenden').kid, 'click');
    assert.equal(await page.evaluate(() => window.__submitted), 2);
    assert.equal(await page.textContent('#submitted'), 'Gesendet');
    await page.close();
  });

  test('errors: unknown element, disabled field, bad action', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const unknown = await call(page, 'act', 'k99999', 'fill', 'x');
    assert.equal(unknown.code, 'element_not_found');
    const disabled = await call(page, 'act', byLabel(s, 'Kundennummer').kid, 'fill', 'x');
    assert.equal(disabled.code, 'not_supported');
    assert.equal(await page.inputValue('input[name=kundennummer]'), 'K-1001');
    const bad = await call(page, 'act', byLabel(s, 'Vorname').kid, 'explode');
    assert.equal(bad.code, 'bad_request');
    const radioLike = await call(page, 'act', byLabel(s, 'Vorname').kid, 'check');
    assert.equal(radioLike.code, 'not_supported');
    await page.close();
  });

  test('accepts frame-prefixed kids ("0:k3")', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const r = await act(page, `0:${byLabel(s, 'Firma').kid}`, 'fill', 'Muster AG');
    assert.equal(r.value, 'Muster AG');
    await page.close();
  });

  test('actBatch fills a whole form in one call and reports per-item results', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const k = (label) => byLabel(s, label).kid;
    const actions = [
      { kid: k('Vorname'), action: 'fill', value: 'Anna' },
      { kid: k('Nachname'), action: 'fill', value: 'Muster' },
      { kid: k('E-Mail-Adresse'), action: 'fill', value: 'anna.muster@example.com' },
      { kid: k('Telefon (optional)'), action: 'fill', value: '+41 44 123 45 67' },
      { kid: k('Firma'), action: 'fill', value: 'Muster AG' },
      { kid: k('Land'), action: 'select', value: 'deutschland' },
      { kid: 'k99999', action: 'fill', value: 'gibt es nicht' },
      { kid: k('Postleitzahl'), action: 'fill', value: '8001' },
      { kid: k('Nachricht'), action: 'fill', value: 'Bitte um Rückruf.' },
      { kid: k('Benutzername'), action: 'fill', value: 'anna.m' },
      { kid: k('Passwort'), action: 'fill', value: 'Geheim-123' },
      { kid: k('Ich akzeptiere die Datenschutzerklärung'), action: 'check' },
      { kid: k('Newsletter abonnieren'), action: 'check' },
      { kid: k('Anrede'), action: 'select', value: 'Herr' },
    ];
    const r = await call(page, 'actBatch', actions);
    assert.ok(r.ok, r.message);
    const results = r.result.results;
    assert.equal(results.length, actions.length);
    results.forEach((res, i) => {
      assert.equal(res.kid, actions[i].kid);
      if (i === 6) {
        assert.equal(res.ok, false);
        assert.equal(res.error.code, 'element_not_found');
        assert.equal(res.value, null);
      } else {
        assert.equal(res.ok, true, `item ${i}: ${JSON.stringify(res.error)}`);
        assert.equal(res.error, null);
      }
    });
    assert.equal(results[5].value, 'DE');
    assert.equal(results[10].value, '••••');
    assert.equal(results[11].checked, true);
    assert.equal(results[13].value, 'Herr');

    assert.equal(await page.inputValue('#vorname'), 'Anna');
    assert.equal(await page.inputValue('input[name=nachname]'), 'Muster');
    assert.equal(await page.inputValue('#email'), 'anna.muster@example.com');
    assert.equal(await page.inputValue('input[name=telefon]'), '+41 44 123 45 67');
    assert.equal(await page.inputValue('select[name=land]'), 'DE');
    assert.equal(await page.inputValue('input[name=plz]'), '8001');
    assert.equal(await page.inputValue('#passwort'), 'Geheim-123');
    assert.equal(await page.evaluate(() => window.__reactState.username), 'anna.m');
    assert.equal(await page.isChecked('#newsletter'), true);
    await page.close();
  });

  test('readValues reports values, checked state and missing elements', async () => {
    const page = await openForm();
    const s = await snapshot(page);
    const vorname = byLabel(s, 'Vorname').kid;
    const nl = byLabel(s, 'Newsletter abonnieren').kid;
    const land = byLabel(s, 'Land').kid;
    await page.fill('#vorname', 'Beat');
    await page.check('#newsletter');
    await page.selectOption('select[name=land]', 'AT');
    const r = await call(page, 'readValues', [vorname, nl, land, 'k424242']);
    assert.deepEqual(r.result.values, {
      [vorname]: { exists: true, value: 'Beat', checked: null },
      [nl]: { exists: true, value: 'on', checked: true },
      [land]: { exists: true, value: 'AT', checked: null },
      k424242: { exists: false, value: null, checked: null },
    });
    await page.close();
  });

  test('scroll up/down/top/bottom', async () => {
    const page = await openForm();
    const down = await call(page, 'scroll', 'down');
    assert.equal(down.result.scrollY, Math.round(800 * 0.8));
    assert.ok(down.result.scrollHeight > 1600);
    const more = await call(page, 'scroll', 'down', 100);
    assert.equal(more.result.scrollY, 740);
    const up = await call(page, 'scroll', 'up', 40);
    assert.equal(up.result.scrollY, 700);
    const bottom = await call(page, 'scroll', 'bottom');
    assert.equal(bottom.result.scrollY, bottom.result.scrollHeight - 800);
    const top = await call(page, 'scroll', 'top');
    assert.equal(top.result.scrollY, 0);
    const bad = await call(page, 'scroll', 'sideways');
    assert.equal(bad.code, 'bad_request');
    await page.close();
  });
});

describe('content agent: iframes', () => {
  test('works per frame with its own local ids', async () => {
    const page = await openForm();
    const frame = page.frames().find((f) => f !== page.mainFrame());
    assert.ok(frame, 'srcdoc iframe');
    await frame.waitForSelector('#code');
    await frame.addScriptTag({ path: CONTENT_JS });

    const top = await snapshot(page);
    assert.ok(!top.elements.some((e) => e.label === 'Gutscheincode'), 'frame content is not part of the top snapshot');
    assert.equal(top.frame.indexInParent, -1);
    assert.equal(top.frame.childFrames.length, 1);

    const inner = await snapshot(frame);
    const code = byLabel(inner, 'Gutscheincode');
    assert.equal(code.kid, 'k1', 'ids are local per frame');
    assert.equal(code.section, 'Gutschein einlösen');
    assert.ok(inner.texts.includes('Gutschein einlösen'));
    assert.equal(inner.frame.indexInParent, 0);

    const r = await act(frame, code.kid, 'fill', 'SOMMER2026');
    assert.equal(r.value, 'SOMMER2026');
    assert.equal(await frame.inputValue('#code'), 'SOMMER2026');
    // The same local id in the top frame refers to a different element.
    assert.notEqual(await page.inputValue('[data-kairo-id="k1"]').catch(() => null), 'SOMMER2026');
    await page.close();
  });
});
