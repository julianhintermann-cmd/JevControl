# Kairo-Installer (MSI)

Der Installer ist ein **Windows-Installer-Paket (MSI) pro Benutzer** für Windows x64, gebaut mit
**WiX Toolset v5.0.2**. Für die Installation sind **keine Administratorrechte** nötig.

| | |
|---|---|
| Installationsordner | `%LOCALAPPDATA%\Programs\Kairo` (im Assistenten änderbar) |
| Datei | `Kairo-<Version>-x64.msi` |
| Architektur | nur x64 |
| Sprache der Oberfläche | Deutsch (WixUI, Kultur `de-DE`) |
| Signatur | standardmäßig **nicht signiert** (siehe unten) |

## Dateien in diesem Ordner

| Datei | Zweck |
|-------|-------|
| `Kairo.Installer.wixproj` | WiX-Projekt (`WixToolset.Sdk/5.0.2`, UI- und Util-Erweiterung). **Nicht** Teil von `Kairo.sln`. |
| `Package.wxs` | Paket, Upgrade-Regeln, Verzeichnisse, Verknüpfungen, Registry, Custom Actions |
| `KairoUI.wxs` | Assistent (Kopie von WixUI_InstallDir mit zusätzlicher Seite „Optionen“) |
| `License.rtf` | Lizenztext (MIT, deutsch) für die Lizenzseite |
| `Directory.Build.props` | trennt das WiX-Projekt von den .NET-Einstellungen im Repository-Stamm (zentrale Paketverwaltung) |
| `build.ps1` | veröffentlicht die App und baut das MSI |
| `test-install.ps1` | automatischer Installations-/Deinstallationstest (CI) |

## Bauen

Voraussetzungen: Windows, .NET 10 SDK, Internetzugang zu nuget.org (für das WiX SDK) und
PowerShell 7 oder Windows PowerShell 5.1.

```powershell
.\installer\build.ps1 -Configuration Release -Version 1.2.3
```

Das Skript

1. veröffentlicht `src\Kairo.App` (self-contained, `win-x64`, ReadyToRun) und `src\Kairo.BrowserHost`
   (self-contained, `win-x64`) gemeinsam nach `artifacts\publish\Kairo` (der Ordner wird vorher geleert),
2. kopiert `extension\` (ohne `tools\`) nach `artifacts\publish\Kairo\extension`,
3. prüft, dass `Kairo.exe`, `Kairo.BrowserHost.exe`, `com.kairo.bridge.json` (mit relativem `path`),
   `Assets\kairo.ico` und `extension\manifest.json` vorhanden sind,
4. baut `Kairo.Installer.wixproj` und gibt Pfad und Größe des MSI aus:
   `artifacts\installer\Kairo-1.2.3-x64.msi`.

| Parameter | Standard | Bedeutung |
|-----------|----------|-----------|
| `-Configuration` | `Release` | `Release` oder `Debug` |
| `-Version` | `1.0.0` | `Major.Minor.Build` (Major/Minor ≤ 255, Build ≤ 65535). Wird als Assembly-/Dateiversion und als MSI-Version verwendet. |
| `-OutputDir` | `..\artifacts` (relativ zum Skript) | Ausgabeordner; ein explizit angegebener relativer Pfad gilt relativ zum aktuellen Verzeichnis |
| `-SkipPublish` | – | `dotnet publish` überspringen und den vorhandenen Publish-Ordner verwenden (z. B. nach dem Signieren der EXE-Dateien) |

Direkt mit MSBuild (wenn der Publish-Ordner schon existiert):

```powershell
dotnet build installer\Kairo.Installer.wixproj -c Release -p:ProductVersion=1.2.3 -p:PublishDir=C:\pfad\zu\publish\Kairo\
```

Das MSI enthält **alles**, was im Publish-Ordner liegt (WiX-`Files`-Element). Das MSI wird nicht
signiert und beim Bauen mit den ICE-Regeln geprüft (einige für Pro-Benutzer-Pakete typische ICEs sind
im Projekt begründet unterdrückt).

## Was installiert wird

* alle Dateien aus dem Publish-Ordner nach `%LOCALAPPDATA%\Programs\Kairo\`
  (`Kairo.exe`, `Kairo.BrowserHost.exe`, .NET-Laufzeit, `com.kairo.bridge.json`, `Assets\`, `extension\`)
* Startmenü-Verknüpfung „Kairo“ (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Kairo.lnk`,
  Beschreibung „KI-Computersteuerung für Windows“, Symbol aus `Kairo.exe`)
* optional Desktop-Verknüpfung „Kairo“
* optional Autostart: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, Wert `Kairo` =
  `"<Installationsordner>\Kairo.exe" --background`
* Native Messaging für die Browser-Erweiterung (immer), Standardwert jeweils
  `<Installationsordner>\com.kairo.bridge.json`:
  * `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.kairo.bridge`
  * `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.kairo.bridge`
* `HKCU\Software\Kairo\Installer` – Installationsordner und gewählte Optionen (für Updates)
* Eintrag unter „Apps“ bzw. „Programme und Features“ (Hersteller „Kairo“, Symbol, Link
  https://github.com/julianhintermann-cmd/jevcontrol)

Die Erweiterung selbst wird nicht automatisch im Browser aktiviert. Sie liegt unter
`%LOCALAPPDATA%\Programs\Kairo\extension` und wird wie in `extension/README.md` beschrieben als
entpackte Erweiterung geladen.

## Assistent

Willkommen → Lizenz → Zielordner → **Optionen** → Bereit zur Installation → Fortschritt → Fertig

* **Optionen:** „Desktop-Verknüpfung erstellen“ (vorausgewählt) und „Kairo beim Windows-Start
  automatisch starten“ (nicht vorausgewählt).
* **Fertig:** Kontrollkästchen „Kairo jetzt starten“ (nur bei Erstinstallation/Update).

## Eigenschaften (Befehlszeile)

| Eigenschaft | Werte | Standard | Wirkung |
|-------------|-------|----------|---------|
| `INSTALLDESKTOPSHORTCUT` | `1` / `0` | `1` bei Erstinstallation | Desktop-Verknüpfung anlegen |
| `AUTOSTART` | `1` / `0` | `0` bei Erstinstallation | Kairo beim Windows-Start im Hintergrund starten |
| `REMOVEUSERDATA` | `1` | – | bei der Deinstallation Einstellungen, Aufgabenverlauf und verschlüsselten API-Schlüssel sicher löschen, ohne zu fragen |
| `INSTALLFOLDER` | Pfad | `%LOCALAPPDATA%\Programs\Kairo\` | Installationsordner |

Bei einem **Update** (neueres MSI über eine installierte Version) werden Installationsordner,
Desktop-Verknüpfung und Autostart vom bisherigen Stand übernommen, sofern sie nicht auf der
Befehlszeile angegeben sind. Für Autostart zählt der tatsächliche `Run`-Eintrag, also auch eine
Änderung in den Kairo-Einstellungen. Ein neu gebautes MSI mit derselben Version ersetzt die
installierte Version ebenfalls (dieselbe MSI-Datei erneut gestartet öffnet dagegen „Reparieren/Entfernen“).
Ältere Versionen lassen sich nicht über neuere installieren (Meldung „Eine neuere Version von Kairo ist
bereits installiert …“).

## Stille Installation und Deinstallation

```powershell
# Standard (Desktop-Verknüpfung, kein Autostart)
msiexec /i Kairo-1.2.3-x64.msi /qn

# ohne Desktop-Verknüpfung, mit Autostart, mit Protokoll
msiexec /i Kairo-1.2.3-x64.msi /qn INSTALLDESKTOPSHORTCUT=0 AUTOSTART=1 /l*v install.log

# anderer Installationsordner
msiexec /i Kairo-1.2.3-x64.msi /qn INSTALLFOLDER="D:\Tools\Kairo"

# deinstallieren, Benutzerdaten behalten
msiexec /x Kairo-1.2.3-x64.msi /qn

# deinstallieren und Benutzerdaten sicher löschen
msiexec /x Kairo-1.2.3-x64.msi /qn REMOVEUSERDATA=1 /l*v uninstall.log
```

Statt der MSI-Datei kann bei `/x` auch der Produktcode (`{…}`) angegeben werden. Weil das Paket pro
Benutzer installiert wird, muss die Installation und Deinstallation im Konto des jeweiligen Benutzers
laufen.

## Deinstallation

Entfernt werden: alle installierten Dateien und Ordner, beide Verknüpfungen, der Autostart-Eintrag,
die Native-Messaging-Registrierung für Chrome und Edge und `HKCU\Software\Kairo\Installer`.

Vor dem Entfernen der Dateien ruft das MSI `Kairo.exe --uninstall-cleanup` auf (nicht bei Updates):

* Mit Oberfläche (voll, reduziert oder einfach, also auch bei der Deinstallation über „Apps“ bzw.
  „Programme und Features“) fragt Kairo in einem Dialog im Vordergrund, ob Einstellungen,
  Aufgabenverlauf und der verschlüsselte API-Schlüssel (`%APPDATA%\Kairo`, `%LOCALAPPDATA%\Kairo`)
  behalten oder sicher gelöscht werden sollen.
* Nur bei einer vollständig stillen Deinstallation (`/qn`, `UILevel` = 2) wird `--quiet` angehängt: Kairo
  fragt nicht und behält die Daten, außer `REMOVEUSERDATA=1` (`--remove-data`) ist gesetzt.
* `/qb` zeigt die Frage ebenfalls an. Für unbeaufsichtigte Verteilungen `/qn` verwenden.
* Fehler dieses Schritts brechen die Deinstallation nicht ab.

## Laufendes Kairo

Vor dem Installieren, Aktualisieren oder Deinstallieren wird ein laufendes Kairo beendet: Das MSI
sendet `WM_CLOSE` (danach `WM_QUERYENDSESSION`/`WM_ENDSESSION`) an die Fenster von `Kairo.exe`, wartet
bis zu 10 Sekunden und beendet den Prozess sonst hart. `Kairo.BrowserHost.exe` (vom Browser gestartet)
wird direkt beendet; die Erweiterung verbindet sich danach selbst neu. In der vollen Oberfläche kann
Windows vorher zusätzlich den Dialog „Dateien werden verwendet“ anzeigen.

## Signierung

Das MSI wird hier **nicht** signiert. Unsignierte Pakete lösen beim Start eine
SmartScreen-/„Unbekannter Herausgeber“-Warnung aus. Die CI signiert das MSI nachträglich, wenn das
Secret `CODE_SIGNING_CERT_BASE64` (und `CODE_SIGNING_CERT_PASSWORD`) gesetzt ist. Manuell:

```powershell
signtool sign /fd SHA256 /f zertifikat.pfx /p <Passwort> /tr http://timestamp.digicert.com /td SHA256 artifacts\installer\Kairo-1.2.3-x64.msi
```

Sollen auch `Kairo.exe` und `Kairo.BrowserHost.exe` signiert werden, diese im Publish-Ordner signieren
und danach `build.ps1 -SkipPublish` ausführen.

## Test

```powershell
.\installer\test-install.ps1 -Msi artifacts\installer\Kairo-1.2.3-x64.msi
```

Der Test läuft so ab:
1. Installiert still mit `INSTALLDESKTOPSHORTCUT=1 AUTOSTART=1`.
2. Prüft Dateien, Verknüpfungen, Autostart und beide Native-Messaging-Registrierungen: Manifest vorhanden,
   `path` zeigt auf eine vorhandene EXE, Erweiterungs-ID erlaubt.
3. Führt `Kairo.exe --selftest --selftest-out selftest.json` aus und erwartet Exit-Code 0. Der Selbsttest
   lädt unter anderem die eingebetteten Tray-Symbole im echten `Kairo.exe`.
4. Startet die installierte `Kairo.exe` wie ein Benutzer, also ohne Schalter und mit dem echten Datenordner.
   Erwartet wird: kein `startup-error.txt`, der Prozess läuft weiter, das Fenster „Kairo einrichten“ erscheint.
   Danach wird Kairo beendet.
5. Legt Benutzerdaten an und deinstalliert still **ohne** `REMOVEUSERDATA`. Die Daten müssen erhalten bleiben.
6. Installiert erneut. Die Daten sind noch da.
7. Deinstalliert still **mit** `REMOVEUSERDATA=1` und prüft, dass alles entfernt wurde, auch
   `%APPDATA%\Kairo` und `%LOCALAPPDATA%\Kairo`.

Außerhalb der CI verweigert das Skript den Lauf, wenn bereits Kairo-Benutzerdaten existieren. Der letzte
Schritt würde sie löschen; mit `-AllowUserDataRemoval` läuft es trotzdem.

Die Protokolle (`install.log`, `uninstall-keep.log`, `reinstall.log`, `uninstall.log`, `selftest.json`) liegen in
`artifacts\test-install`. Bei einem Fehler ist der Exit-Code ≠ 0, und das Ende der msiexec-Protokolle wird
ausgegeben.

## Technische Hinweise

* **Oberfläche:** `WixUI_InstallDir` ist in der WiX-UI-Erweiterung dateilokal deklariert und kann nicht
  erweitert werden. `KairoUI.wxs` enthält deshalb eine Kopie der Dialogfolge aus WiX 5.0.2
  (`WixUI_InstallDir` + x64-Teil) mit der zusätzlichen Seite `KairoOptionsDlg`; die Dialoge selbst und
  ihre deutschen Texte kommen unverändert aus der Erweiterung.
* **Dateien:** Alle Dateien werden mit `<Files Include="$(var.PublishDir)**" />` übernommen (eine
  Komponente pro Datei). Neue Dateien im Publish-Ordner landen automatisch im MSI.
* **Unterdrückte ICEs:** ICE38, ICE64, ICE91, ICE57 (typisch für Pro-Benutzer-Pakete mit Dateien im
  Profil) und ICE61 (Update auf dieselbe Version erlaubt). Eigene Komponenten verwenden HKCU-Schlüssel
  als KeyPath und entfernen ihre Ordner mit `RemoveFolder`.
* **Laufendes Kairo:** `util:CloseApplication` wird direkt nach `InstallInitialize` ausgeführt
  (statt vor `InstallFiles`), damit Kairo vor dem Aufräumschritt und vor `RemoveFiles` beendet ist.
* **Fehlersuche:** immer mit `/l*v datei.log` installieren; nach `Return value 3` suchen.
