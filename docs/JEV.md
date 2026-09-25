# Jev-Integration

## Was ist Jev?

Jev ist das erste „System One“-Modell von TypeSafe AI. Es erzeugt **keinen Text**. Du übergibst einen
Anwendungszustand (`state`) und eine oder mehrere typisierte Fragen. Jev antwortet mit typisierten
Antworten und **kalibrierten Wahrscheinlichkeiten**.

Verifiziert am 25.09.2026 anhand öffentlicher Quellen:

- OpenRouter-Modellseiten: `typesafe/jev-1.13`, Alias `~typesafe/jev-latest`
- OpenRouter-Guide „Jev Documentation“
- Referenzbeispiele `vinaychawla-ops/jev-openrouter-example` und `souvikr/jev-test`

`openrouter.ai` und `typesafe.ai` waren aus der Entwicklungsumgebung nicht direkt erreichbar. Die
Schnittstelle wurde deshalb anhand der beiden öffentlichen Referenzimplementierungen (Stand 19.09.2026)
nachgebaut, und es wurden keine undokumentierten Parameter verwendet.

| Eigenschaft | Wert |
|-------------|------|
| Endpunkt | `POST https://openrouter.ai/api/alpha/decisions`, **nicht** Chat Completions |
| Authentifizierung | `Authorization: Bearer <OpenRouter-Key>` |
| Request | `{ "model": "~typesafe/jev-latest", "state": <string\|object\|array>, "questions": { "<key>": { "type": "noul\|choice\|score", "instructions": "…", "criteria": … } } }` |
| `noul` | Ja/Nein, Antwort `{"type":"noul","noul":0.98}` = P(ja). 0.5 bedeutet „unklar“. |
| `choice` | Ein Label aus einer festen Menge, Antwort `choice`, `probabilities` (für alle Labels), `confidence` |
| `score` | Position auf einer geordneten Skala, Antwort `score` (gewichteter Mittelwert), `legend`, `probabilities`, `confidence` |
| Kriterien | String oder Objekt `{ "what": …, "not_for": …, "examples": [ … ] }` |
| Usage | `usage.input_tokens`, `usage.output_tokens`, `usage.cost` |
| Kontext | bis ca. 32 000 Tokens pro Anfrage (`jev-latest`) |
| Preis | 0.042 $ pro 1 Mio. Input-Tokens, Output kostenlos |
| Latenz | typisch 70–500 ms (in den Referenzmessungen Median ~450 ms inklusive Netzwerk) |
| Fehler | 400, 401, 402, 404, 413, 429, 5xx |

Mehrere Fragen in **einer** Anfrage werden von Jev parallel ausgewertet. Kairo bündelt deshalb seine
Entscheidungen.

## Rolle von Jev in Kairo

Jev ist kein Computer-Use-Modell. Es plant nicht und schreibt nicht. In Kairo ist Jev die schnelle
**Entscheidungsengine**, die unter mehreren **von Kairo vorher als gültig erkannten** Aktionen die richtige
auswählt.

```
Planer (generativ) ──► „Trage den Namen ein“ (target_label "Vorname", Hinweis-ID 12)
Automation Engine ──► gültige Aktionen im aktuellen Zustand:
                       e12: In Feld „Vorname“ tippen   e14: In Feld „E-Mail“ tippen
                       e15: In Feld „Nachricht“ tippen  scroll: erst scrollen   none: blockiert melden
Jev (choice)      ──► e12 (p = 0.96, confidence 0.93)
Kairo             ──► prüft Übereinstimmung mit dem Plan, Berechtigung, führt aus, verifiziert
```

### 1. Zielauflösung (`TargetResolver`, Zweck `resolve`)

Für jeden Element-Schritt eines Plans (`set_value`, `click`, `select_option`, `set_checked`, `focus`) läuft
folgender Ablauf:

1. Kairo bildet lokal die Kandidatenmenge: höchstens 8 **kompatible** Elemente des aktuellen Snapshots.
   - Bei Texteingaben nur beschreibbare Felder, bei Klicks nur klickbare Elemente usw.
   - Die Kandidaten sind nach lexikalischer Ähnlichkeit sortiert. Deutsch/Englisch-Synonyme wie
     „E-Mail“ = „email“ und „Telefon“ = „phone“ sind berücksichtigt.
   - Dazu kommen `scroll` (falls Elemente außerhalb des sichtbaren Bereichs liegen) und `none`.
2. Gibt es genau ein kompatibles Element und passt es zum Plan, braucht es **keinen** Jev-Aufruf.
3. Alle übrigen Schritte gehen als `choice`-Fragen in **einer** Decisions-Anfrage an Jev (ab 16 Fragen
   aufgeteilt und parallel gesendet).
   - `state`: Benutzerziel, Anwendung, Fenstertitel, URL, kompakte Elementliste und sichtbare Texte.
     Alle Werte laufen vorher durch den Secret-Vault.
4. Entscheidungsregeln mit dem **Planer-Hinweis**:

   | Situation | Ergebnis |
   |-----------|----------|
   | Jev bestätigt das Element des Planers | ausführen (`JevAgreesWithPlanner`) |
   | Jev wählt ein anderes Element mit p ≥ 0.8 und confidence ≥ 0.6 | Jev-Element ausführen (`JevOverride`) |
   | Planer-Element hat bei Jev p ≥ 0.2 | Planer-Element (`PlannerPreferred`) |
   | Jev wählt `scroll` | scrollen, neuer Snapshot, erneute Auflösung |
   | Sonst | unaufgelöst → Neuplanung mit Begründung |

   **Ohne Hinweis:** ausführen, wenn p ≥ Schwelle (Einstellung, Standard 0.7) oder wenn Jev bei p ≥ 0.5 mit
   dem lokalen Abgleich übereinstimmt.
5. Konflikte: Zwei Schritte dürfen nicht dasselbe Feld überschreiben. Der wahrscheinlichere gewinnt.
6. Fällt Jev aus (Netzwerk, 5xx), nutzt Kairo den Planer-Hinweis oder einen eindeutigen lokalen Treffer. Das
   ist im Protokoll sichtbar.

Nur Elemente aus der Kandidatenmenge können je ausgeführt werden. Jev kann keine Aktion „erfinden“.

### 2. Risiko-Zweitprüfung (`PermissionManager`, Zweck `risk`)

Die meisten Aktionen stuft die lokale Regelbasis ein. Mehrdeutige Schaltflächen („OK“, „Weiter“,
unbeschriftet) oder Klicks auf Zahlungsseiten bekommen zusätzlich eine `noul`-Frage:

> „Würde das Aktivieren dieses Elements Daten oder eine Nachricht an eine andere Person oder Organisation
> senden, ein Formular absenden, einen Kauf oder eine Zahlung auslösen, Daten löschen oder
> Sicherheitseinstellungen ändern?“

Das geschieht mit `true`/`false`-Kriterien. Ab p ≥ 0.6 wird die Aktion als sensibel behandelt und braucht
eine Freigabe.

### 3. Zielprüfung (`Verifier`, Zweck `verify`)

Am Ende einer Aufgabe geht eine `noul`-Frage an Jev: Wurde das Benutzerziel laut aktuellem UI-Zustand und
ausgeführten Schritten vollständig erreicht?
- Bei p < 0.5 plant Kairo **einmal** gezielt nach.
- Bleibt der Wert niedrig, meldet Kairo das Ergebnis mit dem Hinweis, es zu prüfen.

Werte in Feldern werden dagegen **ohne Modell** verifiziert: gezieltes Rücklesen und toleranter Vergleich
(Leerzeichen, Telefonformatierung, Groß- und Kleinschreibung bei E-Mail).

### 4. Verbindungsprüfung

Onboarding und Einstellungen senden eine winzige echte `noul`-Anfrage (`JevClient.ProbeAsync`, Kosten rund
0.00001 $). So wird die Verfügbarkeit des gewählten Jev-Modells **tatsächlich** geprüft und nicht nur
angenommen.

## Warum diese Aufteilung schnell ist

- Die generative Planung erstellt pro Bildschirmzustand **einen** Batch aller bekannten Schritte, zum
  Beispiel alle Formularfelder.
- Jev löst den ganzen Batch in einem Aufruf von einigen hundert Millisekunden auf.
- Die Ausführung erfolgt lokal und im Batch.
- Es gibt keinen Modellaufruf pro Feld oder Tastendruck.

Beim Ausfüllen eines Formulars mit fünf Feldern entstehen typischerweise diese Aufrufe:
- 1 Planeraufruf,
- 1 Jev-Aufruf für die Auflösung,
- 0 bis 1 Jev-Aufruf für eine Risikoprüfung,
- 1 Jev-Aufruf für die Zielprüfung.

## Konfiguration

Unter *Einstellungen → API und Modelle*:
- Entscheidungsmodell: `~typesafe/jev-latest` (Standard) oder `typesafe/jev-1.13`
- Jev-Entscheidungen an/aus
- Schwelle für Jev-Entscheidungen ohne Planer-Bestätigung

Unter *Einstellungen → Sicherheit* lässt sich die Risiko-Zweitprüfung mit Jev abschalten.

## Code

| Datei | Inhalt |
|-------|--------|
| `src/Kairo.Core/AI/Jev/JevModels.cs` | Fragen, Antworten, Konfidenzbänder (> 0.9 handeln, 0.5–0.9 bestätigen, < 0.5 eskalieren) |
| `src/Kairo.Core/AI/Jev/JevClient.cs` | Decisions-API-Client, Aufteilung großer Batches, Probe |
| `src/Kairo.Core/Agent/TargetResolver.cs` | Kandidatenbildung und Entscheidungsregeln |
| `src/Kairo.Core/Security/PermissionManager.cs` | Risiko-Zweitprüfung |
| `src/Kairo.Core/Agent/Verifier.cs` | Zielprüfung |
| `tests/Kairo.Core.Tests/JevClientTests.cs` | Request-/Response-Format, Retry, Fehler, Usage |
| `tests/Kairo.Core.Tests/TargetResolverTests.cs` | Entscheidungen anhand gültiger Aktionen |
