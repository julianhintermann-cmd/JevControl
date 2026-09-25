# Kairo Browser Bridge (Chrome/Edge-Erweiterung)

Die Erweiterung verbindet Google Chrome und Microsoft Edge mit der Kairo-Desktop-App. Kairo kann damit
den DOM geöffneter Tabs gezielt auslesen (Formularfelder, Buttons, Links, Überschriften) und bedienen
(ausfüllen, klicken, auswählen, scrollen, navigieren). Das ist genauer und schneller als
UI Automation oder Screenshots.

Das Protokoll ist in [`docs/BROWSER-BRIDGE.md`](../docs/BROWSER-BRIDGE.md) beschrieben. Diese Datei
beschreibt Installation, Berechtigungen, Sicherheit und die Punkte, die die Spezifikation offen lässt.

```
Erweiterung (MV3) ──Native Messaging──► Kairo.BrowserHost.exe ──Named Pipe──► Kairo.exe
```

## Dateien

| Datei | Zweck |
|-------|-------|
| `manifest.json` | Manifest V3, feste ID über `key`, keine permanenten Content Scripts |
| `background.js` | Service Worker: Native-Messaging-Verbindung, Methoden, Events, Wiederverbinden |
| `content.js` | Agent pro Frame, wird **nur bei Bedarf** injiziert (Snapshot, Aktionen, Werte, Scrollen) |
| `popup.html`, `popup.css`, `popup.js` | Status-Popup („Verbunden mit Kairo" / „Nicht verbunden", „Erneut verbinden") |
| `icons/` | Symbole 16/32/48/128 px, erzeugt mit `tools/make_icons.py` |

Die Erweiterung hat keine Laufzeit-Abhängigkeiten und keinen Build-Schritt.

## Installation (entpackt)

Der Kairo-Installer legt die Erweiterung unter `%LOCALAPPDATA%\Programs\Kairo\extension` ab.

**Google Chrome**

1. `chrome://extensions` öffnen.
2. Oben rechts den **Entwicklermodus** einschalten.
3. **Entpackte Erweiterung laden** wählen und den Ordner
   `%LOCALAPPDATA%\Programs\Kairo\extension` auswählen.

**Microsoft Edge**

1. `edge://extensions` öffnen.
2. Links den **Entwicklermodus** einschalten.
3. **Entpackt laden** (bzw. „Entpackte Erweiterung laden") wählen und denselben Ordner auswählen.

Danach erscheint „Kairo Browser Bridge" mit der ID **`fjdcafkellelfdkneebdlmoggkhkilmh`**.
Ein Klick auf das Kairo-Symbol in der Symbolleiste zeigt den Verbindungsstatus.

### Feste Erweiterungs-ID

Die ID `fjdcafkellelfdkneebdlmoggkhkilmh` ergibt sich aus dem öffentlichen Schlüssel im Feld `key`
von `manifest.json`. Sie ist dadurch auf jedem Rechner und in Chrome wie Edge identisch. Der
Native-Messaging-Host erlaubt nur diese ID:

```json
{
  "name": "com.kairo.bridge",
  "description": "Kairo Browser Bridge",
  "path": "Kairo.BrowserHost.exe",
  "type": "stdio",
  "allowed_origins": ["chrome-extension://fjdcafkellelfdkneebdlmoggkhkilmh/"]
}
```

Registriert wird das Host-Manifest vom Installer unter
`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.kairo.bridge` und
`HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.kairo.bridge` (Standardwert = Pfad zur JSON-Datei).

### Lokale Dateien (`file://`)

Damit Kairo auch `file://`-Seiten lesen kann, muss in den Details der Erweiterung
**„Zugriff auf Datei-URLs zulassen"** aktiviert werden. Ohne diese Option antwortet die Erweiterung
bei solchen Seiten mit `restricted_page`.

## Berechtigungen

| Berechtigung | Wofür |
|--------------|-------|
| `nativeMessaging` | Verbindung zu `Kairo.BrowserHost.exe` (`com.kairo.bridge`) – der einzige Befehlskanal |
| `scripting` | Content Script bei Bedarf injizieren und Funktionen darin aufrufen |
| `tabs` | URL und Titel der Tabs lesen (`listWindows`, Events), Tabs öffnen/aktivieren/schließen |
| `webNavigation` | Frame-Struktur (für Iframe-Koordinaten) und zuverlässiges Warten auf das Laden einer Seite |
| `alarms` | Einmal pro Minute erneut verbinden, wenn Kairo nicht erreichbar war |
| `storage` | Nur `chrome.storage.session`: Zeitpunkt der letzten Anfrage und letzter Verbindungsfehler für das Popup (wird beim Beenden des Browsers gelöscht) |
| Host `<all_urls>` | Kairo soll auf beliebigen Websites lesen und ausfüllen können |

## Sicherheit

* **Ein einziger Befehlskanal:** Befehle werden ausschließlich über den Native-Messaging-Port
  angenommen. Es gibt kein `externally_connectable`, keine `web_accessible_resources` und keine
  Nachrichten von Webseiten.
* **Interne Nachrichten** (`chrome.runtime.onMessage`) werden nur von der eigenen Erweiterung
  akzeptiert (`sender.id`): Statusabfragen nur vom Popup (Absender ohne Tab, URL = `popup.html`),
  und aus Content Scripts ausschließlich die Meldung „DOM hat sich geändert". Beides kann nichts
  auslösen.
* **Isolierte Welt:** Das Content Script läuft in der isolierten Welt der Erweiterung. Seitenskripte
  sehen und verändern den Agenten nicht; überschriebene Prototypen der Seite wirken sich nicht aus.
* **Passwörter** verlassen die Seite nie: `value` ist `"••••"` (gefüllt) oder `""`, auch in
  `act`-, `actBatch`- und `readValues`-Ergebnissen. `input[type=hidden]` wird nie gemeldet.
* **Seiteninhalte sind Daten**, keine Anweisungen. Die Attribute `data-kairo-id` sind für die Seite
  sichtbar und könnten von ihr verändert werden; Kairo behandelt alle Seitendaten als nicht
  vertrauenswürdig. Doppelte IDs (z. B. durch geklonte Knoten) werden beim nächsten Snapshot neu vergeben.
* **Geschützte Seiten** (`chrome://`, `edge://`, `about:`, `view-source:`, andere Erweiterungen,
  Chrome Web Store, Edge-Add-ons, Fehlerseiten) werden nicht angefasst → `restricted_page`.
* **Navigation** nur zu `http:`, `https:` und `file:` (bei `openTab` zusätzlich `about:blank`).
  `javascript:`, `data:` usw. werden mit `bad_request` abgelehnt.
* **Nichts Sensibles wird gespeichert.** Die Erweiterung speichert keine Seiteninhalte, Werte oder URLs.
* Sensible Aktionen (Absenden, Bezahlen …) gibt der Permission Manager in Kairo frei, **bevor** ein
  `act` gesendet wird. Die Erweiterung selbst führt nur aus.

## Verbindung und Wiederverbinden

* Beim Start des Service Workers wird `chrome.runtime.connectNative("com.kairo.bridge")` aufgerufen und
  sofort `hello` gesendet (`protocol: 1`, `extensionVersion`, `browser`, `userAgent`).
* Bei Verbindungsabbruch oder `{"type":"status","status":"desktop_unavailable"}` gilt die Bridge als
  getrennt. Neue Versuche: per `chrome.alarms` jede Minute, zusätzlich bei Tab-Wechsel oder
  Fensterfokus (höchstens alle 10 s) und über „Erneut verbinden" im Popup.
* Solange der Port offen ist, hält Chrome den Service Worker am Leben.

## Präzisierungen zur Spezifikation

Wo `docs/BROWSER-BRIDGE.md` Spielraum lässt, verhält sich die Erweiterung so:

### Allgemein

* Unbekannte Methode → `not_supported`; ungültige Parameter → `bad_request`.
  Request-`id` muss String oder Zahl sein (sonst Antwort mit `id: null` und `bad_request`).
* Jede Methode hat ein Zeitbudget (z. B. `snapshot` 20 s, `act` 30 s, `actBatch` 90 s,
  `navigate`/`openTab`/`goBack`/`reload` 25 s). Wird es überschritten → `timeout`.
* Allgemeine Browserfehler ohne eigenen Code werden als `script_error` gemeldet.
* `browser` in `hello`: `edge` (Marke „Microsoft Edge" oder `Edg/` im User-Agent), `chrome`
  (Marke „Google Chrome" oder keine Client Hints), sonst `chromium` (z. B. Chromium, Brave).
* Standard-Tab: aktiver Tab des zuletzt fokussierten *normalen* Fensters.

### `snapshot`

* `maxElements`: Standard 300, Werte außerhalb von 1–2000 werden begrenzt.
* Das Limit gilt über alle Frames: zuerst sichtbare Elemente (Hauptframe zuerst, dann Iframes),
  danach unsichtbare, solange Platz ist. Ausgabe nach Frame, dann Dokumentreihenfolge.
* `frames` = Anzahl Frames, die geantwortet haben. Frames, in die nicht injiziert werden kann
  (Fehlerseiten, geschützte Inhalte), werden stillschweigend übersprungen.
* `url`/`title` stammen aus dem Dokument des Hauptframes (`location.href`, `document.title`).
* `rect`: CSS-Pixel relativ zum **Viewport des Hauptframes**. Rechtecke aus Iframes werden
  umgerechnet (inkl. Rahmen und Innenabstand des Iframes, auch bei Cross-Origin-Iframes). Kann die
  Position eines Frames nicht bestimmt werden, bleibt das Rechteck relativ zum Frame.
  Elemente in unsichtbaren Iframes erhalten `visible: false`.
* Zusätzlich erfasste Rollen: `searchbox`, `spinbutton`, `listbox`, `menuitemcheckbox`, `menuitemradio`.
  Verschachtelte `contenteditable`-Bereiche und `contenteditable="false"` werden nicht einzeln gemeldet.
  Offene (und über `chrome.dom` auch geschlossene) Shadow Roots werden durchsucht.
* Sichtbarkeit: `visible: false` bei `display:none` (auch geerbt), `visibility:hidden/collapse`,
  Breite oder Höhe 0 oder `content-visibility:hidden`. `opacity: 0` gilt als sichtbar.
* `role`: Ein explizites `role`-Attribut hat Vorrang (`searchbox`→`textbox`, `spinbutton`→`slider`,
  `menuitemcheckbox`→`checkbox`, `menuitemradio`→`radio`, `textbox` mit `aria-multiline="true"`→`textarea`).
  Sonst: `select`→`combobox` (bzw. `listbox` bei `multiple`/`size>1`), `input[list]`→`combobox`,
  Eingabe-Buttons→`button`, `range`→`slider`, `file`→`file`, übrige `input`→`textbox`,
  `textarea`→`textarea`, `contenteditable`→`textarea` (außer `aria-multiline="false"`).
  `multiline` ist genau dann `true`, wenn `role` = `textarea`.
* `type`: `input.type`, `button.type`, `select-one`/`select-multiple`, `textarea`, sonst `null`.
* `label`: Reihenfolge wie spezifiziert. Ergänzungen: Buttons, Links, Tabs, Menüeinträge und Optionen
  verwenden nach `title` ihren eigenen Text (vor `placeholder`, ohne Umgebungstext); bei Checkboxen,
  Radios und Schaltern wird der *nachfolgende* Text vor dem vorangehenden geprüft. Der Umgebungstext
  wird höchstens drei Ebenen nach oben gesucht und endet an anderen Feldern sowie an `form`/`fieldset`.
  Labels werden bereinigt (Leerraum zusammengefasst, `*` und `:` am Ende entfernt, max. 120 Zeichen).
* `value`: Textfelder → Wert (max. 2000 Zeichen); Checkbox/Radio → `value`-Attribut (z. B. `"on"`)
  plus `checked`; `select` → `value` der gewählten Option (bei `multiple` mit `", "` verbunden);
  `contenteditable` und ARIA-Textfelder/-Comboboxen → sichtbarer Text; ARIA-Slider →
  `aria-valuetext`/`aria-valuenow`; Datei-Felder → Dateinamen; Buttons und Links → `null`.
* `checked`: native Checkbox/Radio, sonst `aria-checked`, `aria-pressed` oder `aria-selected`
  (Tabs, Optionen); sonst `null`.
* `options`: `select` und `datalist` sowie `role=option`-Kinder einer Listbox bzw. des Popups einer
  Combobox (`aria-controls`/`aria-owns`), je `{ value, text, selected, disabled }`, max. 100.
* `section`: `legend` des nächsten `fieldset` → `aria-label`/`aria-labelledby` des nächsten
  `form`/`section`/`dialog`/`[role=form|region|group|dialog|search]` → nächste vorangehende sichtbare
  Überschrift (`h1`–`h6`, `role=heading`).
* `text`: für Rollen `button`, `link`, `tab`, `menuitem`, `option` (sonst `null`), max. 80 Zeichen.
  `href`: absolute URL, max. 200 Zeichen.
* `texts`: Dokumentreihenfolge, nur sichtbare Einträge, ohne Duplikate; Labels mit zugehörigem Feld
  zählen nicht.

### `act` / `actBatch`

* `fill`: zusätzlich wird `maxlength` beachtet (wie beim Tippen) und bei Datumsfeldern das deutsche
  Format `TT.MM.JJJJ` umgewandelt. Auf Checkbox/Radio: `"true"`, `"1"`, `"on"`, `"ja"`, `"yes"`, `"x"`
  → anhaken, sonst abhaken. Auf `select` wie `select`. Zeigt die `kid` auf einen Container mit genau
  einem Eingabefeld, wird dieses gefüllt. Deaktivierte/schreibgeschützte Felder und Datei-Felder
  → `not_supported`.
* `click`: vor `click()` wird eine realistische Folge aus `pointerdown`/`mousedown`/`pointerup`/`mouseup`
  ausgelöst (viele UI-Bibliotheken reagieren nur darauf). Deaktivierte Elemente → `not_supported`.
* `check`/`uncheck`: Ändert ein Klick den Zustand nicht, wird das zugehörige Label geklickt; bleibt der
  Zustand falsch → `script_error`. Radio-Buttons lassen sich nicht abhaken → `not_supported`.
* `select`: Native `select` wie spezifiziert (deaktivierte Optionen werden ignoriert, bei `multiple`
  ist auch ein Array erlaubt). Eigene Widgets: Listbox → passende Option klicken; Combobox → öffnen,
  bis 1,5 s auf Optionen warten, passende Option klicken; editierbare Combobox (Autocomplete) → Wert
  tippen, auf Vorschläge warten und passenden klicken (ohne Vorschläge bleibt der getippte Text).
  Keine passende Option → `element_not_found`.
* `pressEnter`: Nach `keydown`/`keypress`/`keyup` (sofern `keydown` nicht abgebrochen wurde) wird wie
  bei einer echten Enter-Taste der Standard-Absende-Button des Formulars geklickt; gibt es keinen,
  wird `form.requestSubmit()` aufgerufen. Die Formularvalidierung greift in beiden Fällen.
* Lädt die Seite durch `click` oder `pressEnter` neu, bevor ein Ergebnis zurückkommt, lautet die
  Antwort `{ "done": true, "value": null, "checked": null }`.
* `actBatch`: Ungültige Einträge (falsche `kid`, unbekannte Aktion) ergeben pro Eintrag `bad_request`.
  Aufeinanderfolgende Aktionen desselben Frames laufen in einem Aufruf; zwischen den Aktionen gibt der
  Agent kurz an die Ereignisschleife ab, damit React/Vue ihre Updates anwenden können. Max. 500 Aktionen.
* `readValues`: Ungültige oder nicht mehr vorhandene `kid`s → `exists: false`.

### Navigation und Tabs

* `navigate` antwortet mit `{ "tabId", "complete" }`: `complete: false`, wenn die Seite nach 15 s noch
  lädt (kein Fehler). Kann die Seite nicht geladen werden, enthält die Antwort zusätzlich `loadError`
  (z. B. `"net::ERR_NAME_NOT_RESOLVED"`). Navigationen innerhalb des Dokuments (Anker, `pushState`)
  gelten als abgeschlossen.
* `openTab`, `goBack` und `reload` warten ebenfalls bis zu 15 s auf das Laden. `openTab` öffnet ein
  neues Fenster, wenn keines offen ist. `goBack` ohne Verlauf → `not_supported`.
* `scroll` wirkt im Hauptframe. Kann das Dokument selbst nicht scrollen (typisch für Web-Apps), wird
  der scrollbare Container in der Mitte des Viewports verwendet; `scrollY`/`scrollHeight` beziehen
  sich dann auf ihn.

### Events

Events werden nur gesendet, solange die Verbindung besteht:

| `event` | Felder | Auslöser |
|---------|--------|----------|
| `tabActivated` | `tabId`, `windowId`, `url`, `title` | Tab-Wechsel |
| `tabUpdated` | `tabId`, `windowId`, `url`, `title` | nur bei `status: complete` oder Titeländerung |
| `tabRemoved` | `tabId`, `windowId` | Tab geschlossen |
| `windowFocusChanged` | `tabId` (aktiver Tab oder `null`), `windowId` (`-1` = kein Browserfenster fokussiert), `url`, `title` | Fensterfokus |
| `navigationCompleted` | `tabId`, `windowId`, `url`, `title` | Hauptframe fertig geladen, `pushState` oder Anker-Navigation |
| `domChanged` | `tabId`, `windowId`, `frameId`, `url` | erste DOM-Änderung nach einem Snapshot (siehe unten) |

`domChanged`: Nach jedem Snapshot beobachtet der Agent jedes Frames den DOM **einmal**. Bei der ersten
Änderung (Knoten, Text oder die Attribute `hidden`, `style`, `class`, `disabled`, `open`,
`aria-hidden`, `aria-expanded`) meldet er sich nach 300 ms, danach erst wieder nach dem nächsten
Snapshot. So erhält Kairo höchstens ein Event pro Snapshot und Frame.

## Tests

Die Tests liegen in `tests/extension/` (Node ≥ 20 und Playwright mit Chromium):

```bash
cd tests/extension
npm test
```

* `content.test.cjs` – Agent gegen ein deutsches Kontaktformular (`fixtures/contact-form.html`).
* `popup.test.cjs` – Popup mit simuliertem Hintergrunddienst.
* `extension.e2e.test.cjs` – lädt die echte Erweiterung in Chromium (Headless), registriert
  `stub-host.cjs` als Native-Messaging-Host `com.kairo.bridge` und spricht über ihn wie die Desktop-App
  mit der Erweiterung.

## Symbole neu erzeugen

```bash
python3 extension/tools/make_icons.py   # benötigt Pillow
```
