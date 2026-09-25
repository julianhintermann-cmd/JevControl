# Sicherheitsdokumentation

Kairo steuert deinen Computer. Deshalb gilt: **Kairo handelt nur im Rahmen deiner Anweisung, fragt vor
folgenreichen Aktionen und behandelt fremde Inhalte niemals als Befehle.**

## 1. API-Schlüssel

- Gespeichert mit **Windows DPAPI**, `DataProtectionScope.CurrentUser`, plus zufälliger Entropie pro
  Installation. Pfad: `%LOCALAPPDATA%\Kairo\secrets\openrouter-api-key.dpapi`.
- Die Datei ist nur mit deinem Windows-Konto auf diesem Rechner entschlüsselbar.
- Nie im Klartext in `settings.json`, in Logs, im Verlauf, in Fehlermeldungen oder in Telemetrie. Alle
  Protokollzeilen laufen durch den `Redactor`, der `sk-or-…`, Bearer-Tokens und `api_key=…` entfernt.
- Übertragung nur im `Authorization`-Header, nur per HTTPS (TLS 1.2/1.3). Andere Schemata werden
  abgelehnt (`OpenRouterHttp`).
- Beim Ersetzen oder Entfernen wird die alte Datei mit Zufallsdaten überschrieben und gelöscht.
- Kairo sendet keine Telemetrie an Dritte.

Warum nicht der Windows Credential Manager? DPAPI-Dateien sind ebenso an das Benutzerkonto gebunden,
lassen sich sicher überschreiben und beim Deinstallieren gezielt entfernen. Außerdem erscheinen sie nicht in
der Anmeldeinformationsverwaltung, wo sie versehentlich exportiert oder synchronisiert werden könnten.

## 2. Permission Manager

Jede Aktion läuft vor der Ausführung durch `PermissionManager.EvaluateAsync`.

| Stufe | Beispiele | Verhalten |
|-------|-----------|-----------|
| **Gewöhnlich** | Programme öffnen, navigieren, Felder ausfüllen, lesen, suchen | automatisch innerhalb der autorisierten Aufgabe |
| **Sensibel** | Senden, Absenden, Veröffentlichen, Bestätigen, Registrieren; Enter bzw. Strg+Enter in Messaging-Apps; Dateien löschen (Papierkorb); Dateien überschreiben; Pfade außerhalb der freigegebenen Ordner; Passwortfelder; Kommandozeilen starten; Zahlungsseiten | **Freigabe im Overlay**: einmalig oder „für diese Aufgabe“ |
| **Nicht umkehrbar** | Bezahlen, Kaufen, endgültig löschen, Konto löschen, Deinstallieren, Skripte oder Installer ausführen, Umschalt+Entf, jede Änderung in Sicherheitseinstellungen (Windows-Sicherheit, Firewall, Registrierung, UAC, BitLocker …), Ordner oder mehrere Dateien löschen | Freigabe **bei jeder einzelnen** Aktion |
| **Gesperrt** | gesperrte Anwendungen (Passwortmanager, Anmeldedialoge), Schlüssel- und Passwortdatenbank-Dateien, Browserprofile, `.ssh`, Anmeldeinformationen, Systemordner (schreibend), mehr als N Dateien löschen | wird nie ausgeführt, Kairo plant neu |

Vor einer Freigabe zeigt das Overlay im Klartext, was geschieht (zum Beispiel „Kairo klickt auf „Absenden“ in
chrome“), die Risikostufe und die Gründe.
- **Ablehnen** beendet die Aufgabe, ohne die Aktion auszuführen.
- **Esc** bedeutet Ablehnen.
- Wird die Aufgabe während der Freigabe abgebrochen, gilt das ebenfalls als Ablehnen.

Mehrdeutige Schaltflächen („OK“, „Weiter“) prüft zusätzlich Jev (siehe [JEV.md](JEV.md)). Mit der Einstellung
„Bei jeder Aktion fragen“ lässt sich die Schwelle noch weiter senken.

Grundsätze:
- Kairo löscht **nie endgültig**, nur über den Papierkorb.
- Ohne Papierkorb wird das Löschen verweigert.
- Ausführbare Dateien und Skripte schreibt Kairo nie.

## 3. Schutz vor Prompt-Injection

Inhalte aus Webseiten, Dokumenten, Dateisuchen, der Zwischenablage und UI-Texten sind **Daten**.

1. **Rollen-Trennung:** Nur der Text in `<user_instruction>` ist eine Anweisung. Alle anderen Inhalte stehen in
   `<untrusted_data boundary="<zufällig pro Aufgabe>" source="…">`-Blöcken.
   - Der Systemprompt verbietet ausdrücklich, Anweisungen aus Daten zu befolgen.
   - Schließende Tags und Chat-Rollenmarker (`<|im_start|>`, `[INST]` …) werden aus den Daten entfernt.
2. **Aktions-Whitelist:** Der Planer kann nur Aktionen aus dem festen Katalog erzeugen.
   - Element-Aktionen werden nur auf Elemente angewendet, die Kairo im aktuellen Zustand als kompatibel
     erkannt hat. Jev wählt ausschließlich aus dieser Menge.
3. **Injection-Erkennung:** Deutsche und englische Muster werden erkannt, zum Beispiel „ignoriere alle
   vorherigen Anweisungen“, „you are now“, Exfiltrationsaufforderungen und Chatmarker. Bei einem Fund gilt für
   den Rest der Aufgabe:
   - Eine Freigabe „für diese Aufgabe“ wird aufgehoben. Jede sensible Aktion braucht wieder eine Freigabe.
   - URLs, die nicht in deiner Anweisung vorkommen, sind sensibel.
   - Freigabedialoge zeigen einen Warnhinweis.
   - Der Planer bekommt einen Hinweis.
4. Die Freigabe erfolgt immer durch dich im Overlay. Kein Seiteninhalt kann eine Freigabe erteilen.

## 4. Dateizugriff

`FileAccessPolicy`:

- **Standardfreigaben** (Lesen und Schreiben): Desktop, Dokumente, Downloads, Bilder, Musik, Videos,
  OneDrive.
  - Weitere Ordner lassen sich unter *Einstellungen → Sicherheit* freigeben.
- **Pfade aus der Anweisung:** Nennst du einen Pfad ausdrücklich, zum Beispiel
  `C:\Users\Max\Documents\Kontakt.pdf`, ist er für diese Aufgabe freigegeben.
- **Alles andere** braucht eine Freigabe (sensibel).
- **Immer gesperrt:**
  - `.ssh`, `.gnupg`, `.aws`, `.azure`, `.kube`
  - Windows-Anmeldeinformationen und -Schlüssel (`Microsoft\Credentials`, `Protect`, `Crypto`)
  - Browserprofile (Chrome, Edge, Firefox, Brave)
  - Kairos eigener Datenordner
  - `*.kdbx`, `*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.ppk`
  - Schreiben in Windows- und Programmordner
- Pfade werden normalisiert (`GetFullPath`) und Symlinks bzw. Junctions aufgelöst. So kann `..\` die
  Grenzen nicht umgehen.

## 5. Datenminimierung beim Versand an KI-Anbieter

- **Dateien** werden lokal gelesen (TXT, PDF, DOCX, XLSX, CSV, JSON …).
  - Bei großen Dateien wählt Kairo lokal nur die aufgabenrelevanten Abschnitte aus
    (`RelevantContentSelector`), Standardlimit 16 000 Zeichen.
- **Secret-Vault:** Gültige Kreditkartennummern (Luhn) und IBANs (Mod-97) werden vor dem Versand durch
  Platzhalter ersetzt, zum Beispiel `{{kairo:iban_1_endet_5295}}`.
  - Der Planer kopiert den Platzhalter in die Aktion. Den echten Wert setzt erst der lokale Executor ein.
  - So kann Kairo eine IBAN aus einer Datei in ein Formular übertragen, ohne dass das Modell sie je sieht.
- **Passwortfelder:** Ihr Wert wird nie ausgelesen, weder über UIA noch über DOM (dort als `••••`), und in
  Screenshots geschwärzt.
- **Screenshots:**
  - Nur im Vision-Fallback, nur auf Anforderung des Planers, nur das Zielfenster, verkleinert auf 1280 px.
  - Werden immer im Overlay angezeigt („Screenshot“-Hinweis und Protokolleintrag).
  - Einstellbar: senden und anzeigen, vorher fragen, nie senden.
  - Das Kairo-Overlay ist von Aufnahmen ausgeschlossen (`WDA_EXCLUDEFROMCAPTURE`).
- **Prefetch:** Das Vorab-Auslesen beim Öffnen des Overlays ist rein lokal.

## 6. Abbruch und Kontrolle

- **Abbrechen** im Overlay, **Strg+Alt+Umschalt+K** (Nothalt, auch bei ausgeblendetem Overlay) oder
  **Computersteuerung pausieren** im Tray.
- Das `ExecutionGate` wird dabei endgültig geschlossen. Jede weitere Aktion wirft einen Abbruch, auch mitten
  im Tippen.
- Automatisierte Tests prüfen, dass nach einem Abbruch keine Eingabe mehr erfolgt.
- Endlosschleifen werden erkannt (`LoopGuard`).
- Die Zahl der Aktionen und Planungsrunden pro Aufgabe ist begrenzt.
- Kairo läuft mit normalen Benutzerrechten (`asInvoker`) und erhöht sich nie selbst. Windows (UIPI)
  blockiert simulierte Eingaben in Fenster mit Administratorrechten. Kairo meldet das statt es zu umgehen.

## 7. Browser-Bridge

Details in [BROWSER-BRIDGE.md](BROWSER-BRIDGE.md).
- Die Named Pipe ist pro Benutzer (`PipeOptions.CurrentUserOnly` und ACL nur für den aktuellen Benutzer).
- Kairo akzeptiert nur `Kairo.BrowserHost.exe` aus dem Installationsordner als Client
  (Prozesspfad-Prüfung).
- Die Host-Manifeste erlauben nur die feste Erweiterungs-ID: `allowed_origins` für Chrome/Edge,
  `allowed_extensions` (`kairo-bridge@jevcontrol`) für Firefox und Zen.
- Der Host prüft zusätzlich die Aufrufargumente: das Origin (Chromium) bzw. die Add-on-ID (Firefox/Zen).
  Eine andere Erweiterung beendet ihn sofort mit Exit-Code 2.
- In Firefox und Zen ist die Erweiterung nur dauerhaft installierbar, wenn Mozilla sie signiert hat.
  Unsigniert läuft sie nur als temporäres Add-on bis zum Neustart des Browsers.
- Die Erweiterung nimmt Befehle nur über den Native-Messaging-Port an, nicht von Webseiten.

## 8. Lokale Daten

| Ort | Inhalt | Schutz |
|-----|--------|--------|
| `%APPDATA%\Kairo\settings.json` | Einstellungen | enthält keine Geheimnisse |
| `%LOCALAPPDATA%\Kairo\secrets\` | API-Schlüssel, Entropie | DPAPI |
| `%LOCALAPPDATA%\Kairo\history\history.dat` | Aufgabenverlauf (ohne Feldwerte und Screenshots, Anweisungstext optional) | DPAPI, Aufbewahrung konfigurierbar |
| `%LOCALAPPDATA%\Kairo\logs\` | technische Protokolle (Ereignisse, Dauer, Statuscodes) | keine Inhalte, Redactor, 14 Tage |

*Einstellungen → Datenschutz → Alle Kairo-Daten sicher löschen* überschreibt und entfernt alles. Das
Deinstallationsprogramm fragt, ob die Daten erhalten oder sicher gelöscht werden sollen.

## 9. Bekannte Grenzen

- Die Injection-Erkennung ist heuristisch. Der Schutz beruht vor allem auf Rollentrennung, Whitelist und
  Freigaben, nicht auf der Erkennung.
- Anwendungen ohne Accessibility-Informationen lassen sich nur mit dem Vision-Fallback bedienen. Hier sind
  die Genauigkeit und die Risikoeinschätzung (Beschriftung aus dem Bild) geringer.
- DPAPI schützt nicht vor Schadsoftware, die bereits unter deinem Benutzerkonto läuft.
