# Entwicklerdokumentation

## Voraussetzungen

| Werkzeug | Version | Wofür |
|----------|---------|-------|
| Windows 10 (1809+) oder Windows 11, x64 | – | Ausführen und alle Windows-Tests |
| .NET SDK | 10.0.100 oder neuer (`global.json`, `rollForward: latestFeature`) | Build |
| Visual Studio 2026 / Rider / VS Code + C# Dev Kit | optional | IDE |
| Microsoft Edge oder Google Chrome | aktuell | Browser-Tests, Erweiterung |
| Node.js | ≥ 20 | Tests der Erweiterung |
| WiX Toolset | v5 (wird per NuGet-SDK geladen) | MSI, siehe `installer/` |

`Kairo.Core` und die Unit-Tests bauen und laufen auch unter Linux und macOS. Die Windows-Projekte lassen sich
dort dank `EnableWindowsTargeting` kompilieren, aber nicht ausführen.

## Bauen und starten

```powershell
git clone <repo> && cd JevControl
dotnet build Kairo.sln -c Debug
dotnet run --project src/Kairo.App            # startet Kairo (Tray + Onboarding beim ersten Start)
```

Wird die App aus dem Build-Ordner gestartet, registriert sie den Native-Messaging-Host für den Build-Ordner
(`HKCU\Software\{Google\Chrome,Microsoft\Edge,…}\NativeMessagingHosts\com.kairo.bridge`). Die Erweiterung
lädst du entpackt aus `extension/` (siehe [INSTALLATION.md](INSTALLATION.md#browser-erweiterung)).

Befehlszeilenoptionen von `Kairo.exe`:

| Option | Zweck |
|--------|-------|
| `--background` | Start ohne Onboarding-Fenster (Autostart) |
| `--selftest [--selftest-out <datei>]` | Prüft Einstellungen, DPAPI, Laufzeit-Aufbau, Hotkey-Registrierung, UI Automation und den Native-Messaging-Host mit temporärem Datenordner, schreibt einen JSON-Bericht und beendet sich (Exitcode 0 = alle kritischen Prüfungen ok). Wird vom Installer-Test genutzt. |
| `--screenshots <ordner>` | Rendert alle Hauptzustände der Oberfläche (hell/dunkel) als PNG, mit temporärem Datenordner |
| `--uninstall-cleanup [--remove-data] [--quiet]` | Wird vom Deinstallationsprogramm aufgerufen: Autostart und Native-Messaging-Registrierung entfernen, optional alle Benutzerdaten sicher löschen |

## Projektstruktur

```
Kairo.sln
├─ src/
│  ├─ Kairo.Core/            plattformunabhängige Logik (net10.0)
│  │  ├─ Abstractions/       Schnittstellen zur Plattform, ExecutionGate
│  │  ├─ Agent/              AgentRunner (Schleife), TaskManager, TargetResolver, Verifier, LoopGuard
│  │  ├─ AI/OpenRouter/      HTTP-Client, Chat Completions, Modelle, Verbindungstest
│  │  ├─ AI/Jev/             Decisions-API-Client und Modelle
│  │  ├─ AI/Planning/        Planer-Prompt, JSON-Schema, Parser
│  │  ├─ AI/Vision/          Screenshot → Elemente
│  │  ├─ Files/              Datei-Leser (PDF, DOCX, XLSX, CSV, JSON, …), Dateioperationen, Pfaderkennung
│  │  ├─ Perception/         PerceptionService (Cache), SnapshotFormatter, ElementMatcher
│  │  ├─ Security/           PermissionManager, RiskClassifier, FileAccessPolicy, SecretVault, UntrustedContent
│  │  ├─ Settings/           KairoSettings, SettingsStore, KairoPaths
│  │  ├─ History/            verschlüsselter Aufgabenverlauf
│  │  └─ Telemetry/          Logger mit Redactor, Metriken, Kosten
│  ├─ Kairo.Windows/         Windows-Implementierungen (net10.0-windows)
│  │  ├─ Automation/         UI Automation: Wahrnehmung, Aktionen, Ereignisse
│  │  ├─ Browser/            Named-Pipe-Server, DOM-Wahrnehmung und -Aktionen
│  │  ├─ Capture/            Windows Graphics Capture / GDI, Maskierung
│  │  ├─ Input/              SendInput (Tastatur, Maus, Unicode)
│  │  ├─ Windows/            Fensterliste, Aktivierung
│  │  ├─ Hotkeys/            RegisterHotKey, Konflikterkennung
│  │  ├─ Apps/               App-Start (App Paths, Startmenü, Aliasse)
│  │  ├─ Security/           DPAPI, Autostart, Papierkorb, Zwischenablage, Native-Host-Registrierung
│  │  ├─ WindowsActionExecutor.cs
│  │  └─ KairoRuntime.cs     Composition Root
│  ├─ Kairo.App/             WPF-Oberfläche (Kairo.exe)
│  │  ├─ Views/              Overlay, Onboarding, Einstellungen
│  │  ├─ ViewModels/         MVVM (CommunityToolkit.Mvvm)
│  │  ├─ Themes/             Farben hell/dunkel, Styles (Fluent)
│  │  ├─ Controls/           TitleBar, HotkeyBox, Attached Properties
│  │  └─ Services/           Theme/Mica, Tray, Screenshots, Befehlszeile
│  ├─ Kairo.BrowserHost/     Native-Messaging-Host (Relay Browser ↔ Named Pipe)
│  └─ Shared/                Pipe-Protokoll (in beide Projekte verlinkt)
├─ extension/                Chrome/Edge-Erweiterung (MV3, ohne Build-Schritt)
├─ installer/                WiX-v5-Projekt, build.ps1, test-install.ps1
├─ tests/                    Unit-, Integrations-, E2E- und Erweiterungstests, Testformular
├─ docs/                     Dokumentation
└─ tools/                    Icon-Generator (Python)
```

## Zentrale Konzepte

### Composition Root
`KairoRuntime` erzeugt alle Dienste einmalig:
- Settings, Secrets, Logger, OpenRouter-HTTP, Chat-Client, Jev-Client,
- Wahrnehmung (UIA + Browser + Vision), Executor, PermissionManager, TaskManager.

`Kairo.App` bekommt nur die Runtime. Tests bauen dieselbe Runtime mit eigenen Pfaden und ersetzten Modellen
(`KairoRuntimeOptions.ChatModelOverride` / `DecisionModelOverride`).

### Eine neue Aktion hinzufügen
1. `ActionKind` plus Wire-Name in `Models/ActionModels.cs`. Die Erweiterungsmethoden (`IsElementAction`,
   `IsInformational`, `ChangesStructure`, `IsFileAction`) passend ergänzen.
2. Katalogeintrag und Felder im Planer-Prompt (`AI/Planning/PlannerPrompt.cs`). Das JSON-Schema wird aus dem
   Katalog erzeugt; `PlanningTests.Wire_names_roundtrip_for_all_actions` prüft die Vollständigkeit.
3. Risikoeinstufung in `Security/RiskClassifier.cs`.
4. Ausführung:
   - Datei- und Informationsaktionen in `Core/Files/FileOperations.cs` bzw. `AgentRunner`,
   - Systemaktionen in `WindowsActionExecutor.ExecuteSystemAsync`,
   - Elementaktionen in `UiaElementActions` und `BrowserElementActions`.
5. Tests: Unit-Test in `AgentRunnerTests` (mit `FakeDesktop`), bei Windows-Bezug zusätzlich in
   `Kairo.Windows.Tests`.

### Neue Wahrnehmungsquelle
`IPerceptionProvider` implementieren (`CanHandle`, `Priority`, `CaptureAsync`, `ReadStatesAsync`) und in
`KairoRuntime` registrieren. Elemente brauchen einen stabilen `Locator`. Nur über ihn führen Executor und
Verifier Aktionen aus.

### Regeln für Änderungen
- **Keine realen Aktionen am Gate vorbei.** Jeder Codepfad, der Eingaben sendet oder Dateien ändert, ruft vor
  dem Senden `context.Gate.ThrowIfClosed()` auf, beim Tippen zwischen den Blöcken.
- **Simulierte Eingaben nur ins Zielfenster:** Vorher `ActivateAsync` prüfen, bei Misserfolg abbrechen statt
  „blind“ tippen.
- **Keine Geheimnisse in Logs:** Nur `KairoLogger` verwenden (läuft durch `Redactor`). Niemals Feldwerte,
  Dateiinhalte oder Prompts loggen; Latenzen über `TaskMetrics`.
- **Fremde Inhalte immer über `UntrustedContent.Wrap`** an Modelle geben. Neue Quellen zusätzlich mit
  `TaskSecurityContext.Inspect` prüfen.
- **UI-Texte auf Deutsch**, Code und Kommentare auf Englisch.
- **Farben nur über die Ressourcen `Kairo.*`** aus `Themes/Colors.*.xaml` (`DynamicResource`), damit der
  Theme-Wechsel ohne Neustart funktioniert. Style-Schlüssel dürfen nicht wie Brush-Schlüssel heißen (z. B.
  `Kairo.CardStyle` für den Stil, `Kairo.Card` für die Farbe).

## Logs und Diagnose

- Technisches Log: `%LOCALAPPDATA%\Kairo\logs\kairo-YYYYMMDD.log`. Enthält Ereignisse, Dauer und Statuscodes,
  keine Inhalte.
- *Einstellungen → Über Kairo → Diagnose* zeigt die letzten Logzeilen; „Datenordner öffnen“ öffnet `%LOCALAPPDATA%\Kairo`.
- Aufgabenverlauf mit Latenz je Schritt: *Einstellungen → Aufgabenverlauf*.
- `Kairo.exe --selftest --selftest-out result.json` für eine schnelle Umgebungsprüfung.

## Continuous Integration

`.github/workflows/ci.yml`:
- `core` (Linux): Unit-Tests
- `extension` (Linux): Playwright/Chromium
- `windows` (`windows-latest`), in dieser Reihenfolge:
  1. Build
  2. Unit-, Integrations- und E2E-Tests
  3. Screenshots
  4. MSI bauen
  5. optional signieren, nur wenn das Secret `CODE_SIGNING_CERT_BASE64` gesetzt ist
  6. Installations-/Deinstallationstest

Artefakte: `Kairo-installer`, `ui-screenshots`, `windows-test-results`.

Mit dem Repository-Secret `OPENROUTER_API_KEY` laufen zusätzlich die Live-Tests gegen OpenRouter und Jev.

`.github/workflows/screenshots.yml` (manuell startbar) rendert die Oberfläche auf Windows und legt die Bilder
unter `docs/screenshots/` im jeweiligen Branch ab.

## Release

Veröffentlicht wird über den manuell startbaren Workflow `.github/workflows/release.yml` (*Actions → Release →
Run workflow*, Eingaben: Version und Vorabversion ja/nein). Er baut das MSI und testet genau dieses MSI mit
`test-install.ps1`. Anschließend erstellt er das GitHub-Release `v<Version>` mit MSI und `SHA256SUMS.txt`. Die
Release-Notes stammen aus `docs/releases/<Version>.md` (Pflicht).

Lokal:

```powershell
./installer/build.ps1 -Configuration Release -Version 1.2.0
```

Der Befehl erzeugt:
- `artifacts/publish/` (self-contained, win-x64, keine .NET-Installation beim Benutzer nötig),
- `artifacts/installer/Kairo-1.2.0-x64.msi`.

Details: [installer/README.md](../installer/README.md).
