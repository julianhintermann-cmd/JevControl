# Installation

## Systemvoraussetzungen

- Windows 10 Version 1809 oder neuer bzw. Windows 11, **64 Bit (x64)**
- Keine Administratorrechte nötig (Installation pro Benutzer)
- **Kein .NET nötig:** Die Laufzeit ist im Installer enthalten.
- Ein [OpenRouter](https://openrouter.ai)-Konto mit API-Schlüssel und Guthaben
- Optional: Google Chrome oder Microsoft Edge für die Browser-Erweiterung

## Installieren

1. `Kairo-<Version>-x64.msi` starten. Im Assistenten:
   - **Zielordner:** Standard `%LOCALAPPDATA%\Programs\Kairo`
   - **Optionen:** „Desktop-Verknüpfung erstellen“ (vorausgewählt), „Kairo beim Windows-Start automatisch
     starten“
   - **Fertig:** „Kairo jetzt starten“
2. Kairo erscheint im Startmenü und, falls gewählt, auf dem Desktop.

> **SmartScreen-Hinweis:** Das MSI ist nur signiert, wenn beim Build ein Code-Signing-Zertifikat hinterlegt war
> (siehe [installer/README.md](../installer/README.md#signierung)). Ein unsigniertes MSI zeigt „Unbekannter
> Herausgeber“; mit *Weitere Informationen → Trotzdem ausführen* geht es weiter.

Stille Installation (z. B. für Softwareverteilung):

```powershell
msiexec /i Kairo-1.0.0-x64.msi /qn INSTALLDESKTOPSHORTCUT=0 AUTOSTART=1
```

## Ersteinrichtung

Beim ersten Start öffnet sich **„Kairo einrichten“**:

1. **Willkommen**
2. **OpenRouter verbinden:**
   - API-Schlüssel einfügen und **Verbindung testen**.
   - Kairo prüft den Schlüssel, die gewählten Modelle und ob Jev antwortet.
   - Details in [OPENROUTER.md](OPENROUTER.md).
3. **Tastenkombination:**
   - Strg+Alt+K ist vorbelegt.
   - Ist sie von einer anderen App belegt, zeigt Kairo das an und du wählst eine andere.
4. **Fertig:** Kairo läuft im Infobereich der Taskleiste (Tray).

Der Schlüssel wird mit Windows DPAPI verschlüsselt gespeichert und nie im Klartext abgelegt.

## Browser-Erweiterung

Mit der Erweiterung liest und bedient Kairo Webformulare direkt im DOM. Das ist genauer und schneller. Ohne
Erweiterung funktionieren Browser weiterhin über UI Automation.

Die Erweiterung liegt nach der Installation unter `%LOCALAPPDATA%\Programs\Kairo\extension`.

**Microsoft Edge**
1. `edge://extensions` öffnen.
2. **Entwicklermodus** einschalten (links).
3. **Entpackt laden** wählen und den Ordner `%LOCALAPPDATA%\Programs\Kairo\extension` auswählen.

**Google Chrome**
1. `chrome://extensions` öffnen.
2. **Entwicklermodus** einschalten (oben rechts).
3. **Entpackte Erweiterung laden** wählen und denselben Ordner auswählen.

Die Erweiterung „Kairo Browser Bridge“ hat die feste ID `fjdcafkellelfdkneebdlmoggkhkilmh`. Ein Klick auf ihr
Symbol zeigt „Verbunden mit Kairo“, sobald Kairo läuft. Die Verbindung zum Desktop (Native Messaging) hat der
Installer bereits registriert.

> Die Erweiterung ist nicht im Chrome Web Store bzw. bei den Edge-Add-ons veröffentlicht. Deshalb ist die
> Installation als entpackte Erweiterung nötig. Chrome kann beim Start einen Hinweis auf Erweiterungen im
> Entwicklermodus zeigen.

## Aktualisieren

Das neuere MSI einfach ausführen. Installationsordner, Desktop-Verknüpfung, Autostart, Einstellungen,
API-Schlüssel und Verlauf bleiben erhalten. Ein laufendes Kairo wird dabei automatisch beendet.

## Deinstallieren

*Einstellungen → Apps → Installierte Apps → Kairo → Deinstallieren* (oder *Programme und Features*).

Kairo fragt dabei:

> „Möchtest du auch deine Kairo-Einstellungen, den Aufgabenverlauf und den verschlüsselt gespeicherten
> OpenRouter-API-Schlüssel sicher löschen?“

- **Nein:** Die Daten bleiben für eine spätere Neuinstallation erhalten, in `%APPDATA%\Kairo` und
  `%LOCALAPPDATA%\Kairo`.
- **Ja:** Die Dateien werden mit Zufallsdaten überschrieben und dann gelöscht.

In jedem Fall entfernt werden Programmdateien, Verknüpfungen, Autostart und die
Native-Messaging-Registrierung. Die Browser-Erweiterung entfernst du selbst unter `edge://extensions` bzw.
`chrome://extensions`.

Still:

```powershell
msiexec /x Kairo-1.0.0-x64.msi /qn                     # Daten behalten
msiexec /x Kairo-1.0.0-x64.msi /qn REMOVEUSERDATA=1    # Daten sicher löschen
```

Alle Daten lassen sich auch ohne Deinstallation löschen: *Einstellungen → Datenschutz → Alle Kairo-Daten
sicher löschen*.

## Problemlösung

| Problem | Lösung |
|---------|--------|
| Strg+Alt+K reagiert nicht | *Einstellungen → Tastenkombinationen*: Konflikt prüfen und eine andere Kombination wählen. Läuft Kairo (Tray-Symbol)? |
| „Schlüssel ungültig“ | Schlüssel unter openrouter.ai/keys prüfen und neu einfügen |
| „Guthaben reicht nicht aus“ | Guthaben bei OpenRouter aufladen |
| Modell nicht verfügbar | *Einstellungen → API und Modelle → Verbindung testen* schlägt ein verfügbares Modell vor |
| Erweiterung „Nicht verbunden“ | Kairo starten. Nach einer Neuinstallation einmal „Erneut verbinden“ im Erweiterungs-Popup klicken. |
| Eingaben kommen in einer App nicht an | Läuft die App als Administrator? Windows blockiert dann Eingaben von Kairo. Die App ohne Administratorrechte starten. |
| Kairo soll nichts mehr tun | **Strg+Alt+Umschalt+K** oder Tray → *Computersteuerung pausieren* |

Protokolle ohne Inhalte liegen unter `%LOCALAPPDATA%\Kairo\logs`.
