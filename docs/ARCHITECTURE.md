# Architekturübersicht

Kairo ist eine native Windows-Desktop-Anwendung (.NET 10, WPF, MVVM). Du beschreibst einem Agenten auf
Deutsch oder Englisch, was du erledigen willst. Der Agent erfüllt die Aufgabe dann mit echten Windows-Aktionen.
Diese Seite beschreibt die Bausteine und wie eine Aufgabe durch sie fließt.

## Projekte

| Projekt | Ziel-Framework | Aufgabe |
|---------|----------------|---------|
| `src/Kairo.Core` | `net10.0` (plattformunabhängig) | Agent-Schleife, Planer, Jev-Client, OpenRouter-Client, Permission Manager, Prompt-Injection-Schutz, Datei-Leser, Einstellungen, Verlauf, Telemetrie |
| `src/Kairo.Windows` | `net10.0-windows10.0.22621.0` | UI Automation, Browser-Bridge, SendInput, Windows Graphics Capture, DPAPI, globale Hotkeys, App-Start, Papierkorb, Zwischenablage, `KairoRuntime` (Composition Root) |
| `src/Kairo.App` | WPF, `WinExe` (`Kairo.exe`) | Overlay, Onboarding, Einstellungen, Tray, Theme/Mica, Befehlszeilenmodi |
| `src/Kairo.BrowserHost` | Konsole (`Kairo.BrowserHost.exe`) | Native-Messaging-Host: leitet Nachrichten zwischen Erweiterung und Kairo über eine Named Pipe weiter |
| `extension/` | Chrome/Edge MV3 | DOM-Wahrnehmung und DOM-Aktionen in Tabs |
| `installer/` | WiX v5 | MSI für x64 (Installation pro Benutzer) |
| `tests/*` | xUnit, Node | Unit-, Integrations- und End-to-End-Tests |

`Kairo.Core` referenziert keine Windows-APIs und läuft deshalb auch unter Linux (CI und Unit-Tests).
Alles Plattformspezifische ist über Schnittstellen abstrahiert (`IPerceptionProvider`, `IActionExecutor`,
`IWindowService`, `IScreenCapture`, `ISecretStore`, `IUserInteraction`, `IRecycleBin`, `IDataProtector`).

## Ablauf einer Aufgabe

```
Strg+Alt+K ──► Overlay öffnet sofort (vorab erzeugt, nur ein-/ausgeblendet)
             ├─ merkt sich das Vordergrundfenster (= Zielfenster)
             ├─ Prefetch: UI-Snapshot des Zielfensters startet im Hintergrund (lokal, nichts wird gesendet)
             └─ TLS-Verbindung zu OpenRouter wird vorgewärmt
Enter ──────► TaskManager.Start ─► AgentRunner.RunAsync
```

```
                ┌──────────────── Kontext parallel sammeln ────────────────┐
 Anweisung ───► │ Snapshot (Prefetch)    Dateien aus der Anweisung (lokal)  │
                └─────────────────────────────┬─────────────────────────────┘
                                              ▼
                    Planer (OpenRouter Chat, JSON-Schema) ─► Plan: Schritte-Batch + after_steps
                                              ▼
       Element-Schritte ─► TargetResolver: EINE Jev-Anfrage (choice je Schritt, nur gültige Aktionen)
                                              ▼
                  PermissionManager (Regeln + Jev-noul bei mehrdeutigen Buttons) ─► ggf. Freigabe im Overlay
                                              ▼
       Ausführung: DOM-Batch (Erweiterung) │ UIA-Patterns │ SendInput-Fallback │ Vision-Koordinaten
                                              ▼
       Verifikation: gezieltes Rücklesen der geänderten Felder ─► Korrektur mit simulierter Eingabe
                     (vor jedem Klick/Absenden und am Ende der Runde – nie wird mit falschen Werten abgesendet)
                                              ▼
       after_steps = verify_and_finish ─► Jev-noul „Ziel erreicht?“ ─► Completed
       after_steps = replan / Strukturwechsel ─► neuer Snapshot (nur falls invalidiert) ─► Planer
```

### Zustände (`AgentTaskState`)

`Pending → Planning → Executing ⇄ Verifying → Completed`
Dazu kommen `WaitingForApproval` (Freigabe oder Rückfrage), `Failed` und `Cancelled`. Endzustände lassen sich
nicht mehr verlassen.

### Abbruch

Jede reale Aktion läuft durch das `ExecutionGate`:
- Abbrechen im Overlay, der Nothalt-Hotkey (`Strg+Alt+Umschalt+K`) oder „Computersteuerung pausieren“
  schließen das Gate endgültig.
- Der Executor prüft das Gate vor jedem Schritt, zwischen Batch-Elementen und während des Tippens
  (Zeichenblöcke).
- Gleichzeitig wird das `CancellationToken` ausgelöst. Es bricht HTTP-Aufrufe, Wartezeiten und
  UIA-Aufrufe ab.

### Schleifenschutz

`LoopGuard` begrenzt dreifach:
- identische Aktionen, die sich wiederholen,
- Planungsrunden ohne Fortschritt,
- die Gesamtzahl der Aktionen (`MaxActionsPerTask`).

Zusätzlich gibt es eine Obergrenze für Planungsrunden (`MaxPlanningRounds`).

## Wahrnehmung (Screen Perception Engine)

`PerceptionService` wählt je Fenster den besten Provider:

1. **Browser-DOM** (Priorität 20): für Chrome und Edge, wenn die Kairo-Erweiterung verbunden ist.
   - Liest Labels, `autocomplete`-Hinweise, Pflichtfelder, Optionen und Sektionen direkt aus dem DOM.
   - Erfasst auch iframes.
2. **Windows UI Automation** (Priorität 10): für jede Anwendung.
   - Holt den gesamten Control-View-Baum in **einem** prozessübergreifenden Aufruf
     (`ElementFromHandleBuildCache`, `TreeScope_Subtree`, rund 35 gecachte Properties).
   - Beschriftet unbeschriftete Felder über `LabeledBy` oder den vorangehenden Text.
   - Trennt bei Chromium den Webinhalt von der Browser-Oberfläche.
3. **Vision** (nur auf Anforderung durch den Planer, `request_vision`):
   - Windows Graphics Capture des Zielfensters, mit GDI als Fallback.
   - Passwortfelder werden geschwärzt und das Bild verkleinert.
   - Das Vision-Modell liefert Elemente mit Bounding-Boxes. Daraus entsteht ein normaler Snapshot mit
     Bildschirmkoordinaten.
   - Die Einwilligung ist konfigurierbar (anzeigen, fragen, nie).

Snapshots sind kompakt (`[12] edit "E-Mail" value="" hint=email required (Kontaktdaten)`) und werden pro
Fenster gecacht. Der Cache wird **ereignisbasiert** verworfen:
- UIA-`StructureChanged` (entprellt),
- Browser-Ereignisse (`navigationCompleted`, `domChanged`, `tabActivated`),
- eigene strukturverändernde Aktionen wie Klicks.

Eigene Werteänderungen werden direkt im Cache nachgetragen (`ApplyLocalChange`). Eine unveränderte
Oberfläche wird deshalb nicht erneut ausgelesen.

## Ausführung (Automation Engine)

| Aktion | Bevorzugt | Fallback |
|--------|-----------|----------|
| Text setzen | DOM `fill` mit nativem Setter und `input`/`change`-Events (React-kompatibel), UIA `ValuePattern.SetValue` | Fokus + `Strg+A` + Unicode-`SendInput` |
| Klicken | DOM `click`, UIA `Invoke`/`Toggle`/`SelectionItem`/`ExpandCollapse`/`LegacyIAccessible` (mit Timeout gegen modale Blockaden) | `ScrollIntoView` + Mausklick auf den klickbaren Punkt |
| Kontrollkästchen | DOM `check`/`uncheck`, UIA `TogglePattern` | Klick |
| Auswahl | DOM `select`, UIA `ExpandCollapse` + `SelectionItem` | `Alt+↓`, Text tippen, Enter |
| System | Programme (App Paths, Startmenü, Aliasse), Fenster (Aktivieren mit Foreground-Lock-Workaround), Dateien (`ShellExecute`), URLs/Tabs (Erweiterung oder Tastenkürzel), Zwischenablage, Dateioperationen (Papierkorb statt Löschen) | – |

Mehrere Formularfelder werden als **ein Batch** ausgeführt (DOM `actBatch`: ein Round-Trip). Es gibt keinen
Modellaufruf pro Feld oder Zeichen.

Vor einem Schritt, der absenden, navigieren oder etwas öffnen kann (Klick), führt Kairo den offenen Batch aus,
liest die Werte zurück und korrigiert sie. Erst danach folgt die Freigabe. Lässt sich ein Wert nicht bestätigen,
wird nicht geklickt, sondern mit der konkreten Ursache neu geplant.

Simulierte Eingaben (SendInput) gehen nur an das Zielfenster. Lässt es sich nicht in den Vordergrund holen oder
blockiert ein Dialog das Feld, schlägt der Schritt fehl, statt „blind“ zu tippen.

## KI-Schicht

- **Planer** (`Planner`, `PlannerPrompt`):
  - OpenRouter Chat Completions mit `response_format: json_schema`.
  - Fällt bei Modellen ohne Structured Output automatisch auf `json_object` und danach auf reinen Prompt
    zurück.
  - Es wird nur der neueste UI-Zustand gesendet, ältere Runden werden gekürzt.
  - Anthropic-Modelle erhalten einen Cache-Breakpoint auf dem Systemprompt.
- **Jev** (`JevClient`, `TargetResolver`, `Verifier`, `PermissionManager`):
  - Strukturierte Entscheidungen über die OpenRouter Decisions API, siehe [JEV.md](JEV.md).
- **Vision** (`VisionAnalyzer`): optionales Bildmodell für den Screenshot-Fallback.

## Sicherheit

Siehe [SECURITY.md](SECURITY.md). Kurzfassung:
- API-Schlüssel mit DPAPI verschlüsselt.
- Permission Manager mit vier Risikostufen.
- Nicht vertrauenswürdige Inhalte sind durch zufällige Boundaries markiert und werden nur als Daten
  behandelt.
- Injection-Erkennung.
- Dateizugriffsgrenzen.
- Kartennummern und IBANs werden vor dem Versand durch Platzhalter ersetzt.

## Performance-Maßnahmen

- Das Overlay ist vorab erzeugt. Beim Öffnen startet der Snapshot spekulativ und die TLS-Verbindung wird
  vorgewärmt.
- Snapshot, Dateilesen und Ordnersuche laufen parallel.
- Ein einziger Cache-Request pro UIA-Snapshot.
- DOM-Snapshots enthalten nur interaktive Elemente und Texte.
- Alle Element-Schritte einer Runde gehen in einer Jev-Anfrage. Jev bewertet die Fragen parallel, große
  Batches werden aufgeteilt und parallel gesendet.
- Eindeutige Zuordnungen (genau ein kompatibles Element) kommen ohne Modellaufruf aus.
- Formularfelder werden als Batch ausgeführt. Die Überprüfung liest nur die geänderten Elemente.
- Die Korrektur erfolgt lokal (simulierte Eingabe) statt durch Neuplanung.
- Die Latenz jedes Schritts (`perception.*`, `planning`, `resolve`, `execute.*`, `verify.*`, `model.*`)
  wird im Aufgabenverlauf gespeichert. Dabei werden keine Inhalte protokolliert.
