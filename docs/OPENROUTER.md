# OpenRouter konfigurieren

Kairo braucht genau **einen** OpenRouter-API-Schlüssel. Damit laufen drei Dinge:
- das Planungsmodell über Chat Completions,
- Jev über die Decisions API,
- optional das Vision-Modell.

## 1. Schlüssel erstellen

1. Konto auf <https://openrouter.ai> anlegen und Guthaben aufladen. Jev ist sehr günstig, das Planungsmodell
   verursacht den Großteil der Kosten.
2. Unter <https://openrouter.ai/keys> einen Schlüssel erstellen. Er beginnt mit `sk-or-v1-…`.
   Empfehlung: ein eigenes **Kreditlimit** für den Kairo-Schlüssel setzen.

## 2. In Kairo hinterlegen

Beim ersten Start öffnet sich die Einrichtung (später unter *Einstellungen → API und Modelle*):

1. Schlüssel in das Passwortfeld einfügen. Mit dem Augen-Symbol kannst du ihn anzeigen.
2. **Verbindung testen**. Kairo prüft dann:
   - den Schlüssel über `GET https://openrouter.ai/api/v1/key` (inklusive verbleibendem Limit),
   - ob Planungs- und Vision-Modell für dein Konto verfügbar sind (`GET /api/v1/models`, Bildunterstützung),
   - ob Jev wirklich antwortet (eine winzige Decisions-Anfrage).

   Ist ein Modell nicht verfügbar, setzt Kairo automatisch einen Vorschlag aus einer Präferenzliste ein.
3. Mit **Weiter** oder **Speichern** wird der Schlüssel mit **Windows DPAPI** (`CurrentUser` plus Entropie
   pro Installation) verschlüsselt unter `%LOCALAPPDATA%\Kairo\secrets\` abgelegt.
   - Er steht nie in `settings.json`, in Logs oder im Verlauf.
   - Er wird nur im `Authorization`-Header per HTTPS (TLS 1.2/1.3) übertragen.

Ändern: neuen Schlüssel eingeben → *Speichern*. Entfernen: *Schlüssel entfernen*. Die verschlüsselte Datei
wird dabei vor dem Löschen mit Zufallsdaten überschrieben.

## 3. Modelle

| Rolle | Standard | Hinweise |
|-------|----------|----------|
| Planungsmodell | `anthropic/claude-sonnet-5` | Beliebiges Chat-Modell mit gutem Instruktionsverständnis. Structured Output (`json_schema`) wird genutzt, wenn verfügbar, sonst `json_object` bzw. Prompt-JSON. Präferenzliste für Vorschläge: Claude Sonnet 5 → Opus 5.5 → Sonnet 4.5 → Haiku 4.5 → weitere. |
| Entscheidungsmodell | `~typesafe/jev-latest` | Alternativ `typesafe/jev-1.13` (fixierte Version). |
| Vision-Modell | `anthropic/claude-sonnet-5` | Muss Bildeingaben unterstützen. Wird nur im Screenshot-Fallback genutzt. |

Mit *Verfügbare Modelle laden* holt Kairo die aktuelle Modellliste von OpenRouter in die Auswahllisten. In
die Felder kannst du auch direkt jede OpenRouter-Modell-ID eintragen.

*Planungstiefe* setzt optional den OpenRouter-Parameter `reasoning.effort` (low/medium/high). Standard ist
die Voreinstellung des Modells. Das ist meist am schnellsten.

## 4. Verwendete Endpunkte

| Zweck | Endpunkt |
|-------|----------|
| Planung, Vision | `POST https://openrouter.ai/api/v1/chat/completions` mit `response_format`, `usage.include=true`, bei Structured Output `provider.require_parameters=true` |
| Jev | `POST https://openrouter.ai/api/alpha/decisions` |
| Modellliste | `GET https://openrouter.ai/api/v1/models` |
| Schlüsselprüfung | `GET https://openrouter.ai/api/v1/key` |

Mitgesendete Header: `HTTP-Referer` (Projekt-URL) und `X-Title`/`X-OpenRouter-Title: Kairo` für die
App-Zuordnung bei OpenRouter.

## 5. Fehlerbehandlung

| Fall | Verhalten |
|------|-----------|
| Netzwerkfehler, Timeout, 429, 5xx | bis zu 2 Wiederholungen mit exponentiellem Backoff (respektiert `Retry-After`) |
| 400/404 wegen fehlender Structured-Output-Unterstützung | automatischer Wechsel auf `json_object` bzw. Prompt-JSON, wird pro Modell gemerkt |
| 401/403 | „Schlüssel ungültig“ – Aufgabe endet mit verständlicher Meldung |
| 402 | „Guthaben reicht nicht aus“ |
| Leere Antwort | wird als Fehler behandelt |
| Jev nicht erreichbar | Kairo fällt auf Planer-Hinweise zurück (siehe [JEV.md](JEV.md)) |

Timeouts:
- Chat: 60 s
- Jev: 15 s
- Metadaten: 15 s

Fehlermeldungen werden vor Anzeige und Protokollierung durch den `Redactor` geschickt. Schlüsselähnliche
Zeichenfolgen werden dabei entfernt.

## 6. Kosten

- OpenRouter liefert pro Aufruf `usage.cost`. Kairo summiert die Kosten je Aufgabe und zeigt sie im
  Ergebnis des Overlays und im Aufgabenverlauf.
- *Einstellungen → API und Modelle → Kosten* zeigt Sitzung, heute und die letzten 30 Tage.
- Jev berechnet nur Eingabe-Tokens (0.042 $ pro 1 Mio.). Ein Entscheidungsaufruf kostet typischerweise rund
  0.00002 $.
