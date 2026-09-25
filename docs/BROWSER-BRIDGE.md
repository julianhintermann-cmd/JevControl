# Kairo Browser Bridge – Protokoll

Die Browser-Bridge verbindet die Kairo-Desktop-App mit Chrome und Edge. So kann Kairo den DOM
geöffneter Tabs kontrolliert auslesen und bedienen. Das ist genauer und schneller als
UI Automation oder Screenshots.

```
┌────────────────────┐  Native Messaging   ┌──────────────────────┐   Named Pipe (CurrentUserOnly)  ┌──────────────┐
│ Kairo-Erweiterung  │ ──────────────────► │ Kairo.BrowserHost.exe │ ──────────────────────────────► │  Kairo.exe   │
│ (MV3, Chrome/Edge) │ ◄────────────────── │  (reines Relay)       │ ◄────────────────────────────── │ BridgeServer │
└────────────────────┘  stdin/stdout       └──────────────────────┘   \\.\pipe\kairo-browser-bridge-<user>   └──────────────┘
```

## Transport

* **Erweiterung ↔ Host:** Chrome Native Messaging. Jede Nachricht besteht aus einer 4-Byte-Länge
  (Little Endian, `uint32`) und UTF-8-JSON. Host → Browser max. 1 MB, Browser → Host max. 64 MB.
* **Host ↔ Kairo:** dieselbe Rahmung (4-Byte-Länge LE + UTF-8-JSON) über die Named Pipe
  `kairo-browser-bridge-<Benutzername in Kleinbuchstaben, nur [a-z0-9]>`. Die Pipe ist mit
  `PipeOptions.CurrentUserOnly` angelegt. Kairo prüft zusätzlich, dass der verbundene Prozess
  `Kairo.BrowserHost.exe` aus dem Installationsverzeichnis ist.
* Der Host leitet Nachrichten **unverändert** weiter. Ist Kairo nicht erreichbar, sendet der Host
  `{"type":"status","status":"desktop_unavailable"}` an die Erweiterung und beendet sich.
* Name des Native-Messaging-Hosts: `com.kairo.bridge`.

## Nachrichten

Alle Nachrichten sind JSON-Objekte mit einem Feld `type`.

### Erweiterung → Kairo

| type | Felder | Bedeutung |
|------|--------|-----------|
| `hello` | `protocol` (1), `extensionVersion`, `browser` (`chrome`\|`edge`\|`chromium`), `userAgent` | wird direkt nach dem Verbinden gesendet |
| `event` | `event`, `tabId`, `windowId`, optional `url`, `title` | `tabActivated`, `tabUpdated`, `tabRemoved`, `windowFocusChanged`, `navigationCompleted`, `domChanged`. Kairo verwirft damit seinen Snapshot-Cache. |
| `response` | `id`, `ok`, `result` \| `error: {code, message}` | Antwort auf einen Request |

### Kairo → Erweiterung

```json
{ "type": "request", "id": "<eindeutige id>", "method": "<methode>", "params": { } }
```

Fehlercodes: `bad_request`, `no_tab`, `restricted_page` (z. B. `chrome://`, Web Store),
`element_not_found`, `not_supported`, `timeout`, `script_error`.

## Methoden

### `ping`
→ `{ "pong": true, "time": <ms seit Epoch> }`

### `listWindows`
→
```json
{ "windows": [ {
    "windowId": 1, "focused": true, "state": "normal", "type": "normal",
    "left": 0, "top": 0, "width": 1280, "height": 800, "activeTabId": 7,
    "tabs": [ { "tabId": 7, "index": 0, "title": "Kontakt", "url": "https://…", "active": true, "status": "complete" } ]
} ] }
```

### `snapshot`
Parameter: `tabId?` (Standard: aktiver Tab des zuletzt fokussierten Fensters), `maxElements?` (Standard 300),
`includeText?` (Standard `true`).

Das Content Script wird **bei Bedarf** mit `chrome.scripting.executeScript` in alle Frames injiziert
(`allFrames: true`). Es gibt keine permanent laufenden Content Scripts.

→
```json
{
  "tabId": 7, "windowId": 1, "url": "http://127.0.0.1:8080/form.html", "title": "Kontakt",
  "viewport": { "innerWidth": 1264, "innerHeight": 700, "outerWidth": 1280, "outerHeight": 800,
                "screenX": 0, "screenY": 0, "devicePixelRatio": 1, "scrollX": 0, "scrollY": 0, "scrollHeight": 1400 },
  "elements": [ {
    "kid": "0:k3", "frameId": 0,
    "tag": "input", "type": "email", "role": "textbox",
    "label": "E-Mail-Adresse", "name": "email", "id": "email", "placeholder": "name@example.com",
    "autocomplete": "email", "value": "", "checked": null,
    "required": true, "disabled": false, "readonly": false, "multiline": false, "isPassword": false,
    "options": null, "section": "Kontaktformular", "text": null, "href": null,
    "rect": { "x": 120, "y": 240, "width": 300, "height": 32 }, "visible": true, "inViewport": true
  } ],
  "texts": [ "Kontaktformular", "Bitte füllen Sie alle Pflichtfelder aus." ],
  "frames": 1, "truncated": false
}
```

Regeln für das Content Script:

* Erfasst werden: `input` (außer `hidden`), `textarea`, `select`, `button`, `a[href]`,
  `[contenteditable]`, `[role=button|link|checkbox|radio|switch|combobox|textbox|tab|menuitem|option|slider]`.
* `kid` = `<frameId>:k<n>`. Die lokale Id steht im Attribut `data-kairo-id` und bleibt über mehrere
  Snapshots stabil.
* `role` wird normalisiert auf: `textbox`, `textarea`, `button`, `link`, `checkbox`, `radio`, `switch`,
  `combobox`, `listbox`, `slider`, `tab`, `menuitem`, `option`, `file`.
* Label-Auflösung in dieser Reihenfolge: `aria-labelledby`, `aria-label`, `<label for>`, umschließendes
  `<label>`, `title`, `placeholder`, vorangehender sichtbarer Text im selben Container, `name`.
* **Passwortfelder:** `value` ist `"••••"`, wenn gefüllt, sonst `""`. Echte Passwortwerte verlassen die Seite nie.
* Unsichtbare Elemente (`display:none`, `visibility:hidden`, Größe 0) bekommen `visible: false`.
  Sie werden nur aufgenommen, wenn noch Platz im Limit ist.
* `texts`: Überschriften, `legend`, Labels ohne Feld, `[role=alert]`, `.error`/`.invalid-feedback`,
  jeweils gekürzt auf 200 Zeichen, max. 60 Einträge.

### `act`
Parameter: `tabId?`, `kid`, `action`, `value?`

| action | Wirkung |
|--------|---------|
| `fill` | fokussieren, Wert über den nativen Prototype-Setter setzen, `input` + `change` auslösen (kompatibel mit React/Vue/Angular); bei `contenteditable` über `insertText` |
| `clear` | wie `fill` mit `""` |
| `click` | `scrollIntoView({block:"center"})`, dann `click()` |
| `check` / `uncheck` | nur klicken, wenn der Zustand abweicht |
| `select` | Option per `value` oder sichtbarem Text wählen (zuerst exakt, dann ohne Groß-/Kleinschreibung, dann Teilstring), `input` + `change` auslösen |
| `focus` | `focus()` |
| `scrollIntoView` | `scrollIntoView({block:"center"})` |
| `pressEnter` | `keydown`/`keypress`/`keyup` für Enter; bei Eingabefeldern in einem Formular `form.requestSubmit()` |

→ `{ "done": true, "value": "<neuer Wert>", "checked": true|false|null }`

### `actBatch`
Parameter: `tabId?`, `actions: [ { kid, action, value? } ]`. Die Aktionen laufen der Reihe nach.
Nach einem Fehler geht es mit der nächsten weiter. Ein einziger Round-Trip für ein ganzes Formular.

→ `{ "results": [ { "kid": "0:k3", "ok": true, "value": "…", "checked": null, "error": null } ] }`

### `readValues`
Parameter: `tabId?`, `kids: [ … ]` → `{ "values": { "0:k3": { "exists": true, "value": "…", "checked": null } } }`

### `scroll`
Parameter: `tabId?`, `direction` (`up`\|`down`\|`top`\|`bottom`), `amount?` (px, Standard: 80 % der Viewport-Höhe)
→ `{ "scrollY": 640, "scrollHeight": 2400 }`

### `navigate`
Parameter: `tabId?`, `url` (nur `http:`, `https:` und `file:`). Wartet bis `status == complete` (max. 15 s).
→ `{ "tabId": 7 }`

### `openTab`
Parameter: `url`, `windowId?`, `active?` (Standard `true`) → `{ "tabId": 9, "windowId": 1 }`

### `activateTab`
Parameter: `tabId`. Aktiviert den Tab und fokussiert sein Fenster → `{ "tabId": 9 }`

### `closeTab`
Parameter: `tabId` → `{ "closed": true }`

### `goBack` / `reload`
Parameter: `tabId?` → `{ "done": true }`

## Sicherheit

* Die Erweiterung nimmt Befehle **nur** über den Native-Messaging-Port an. Es gibt kein
  `externally_connectable` und keine Nachrichten von Webseiten.
* `allowed_origins` im Host-Manifest enthält nur die feste Erweiterungs-ID
  (`chrome-extension://<id>/`). Die ID ist über den `key` in `manifest.json` festgelegt.
* Seiteninhalte sind **Daten**, keine Anweisungen. Die Desktop-App kennzeichnet sie als
  nicht vertrauenswürdig (siehe `SECURITY.md`).
* Sensible Aktionen (Absenden, Senden, Bezahlen …) werden in Kairo vom Permission Manager
  freigegeben, **bevor** ein `act`-Request gesendet wird.
