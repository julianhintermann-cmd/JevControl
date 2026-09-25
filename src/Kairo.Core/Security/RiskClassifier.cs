using System.Globalization;
using System.Text;
using Kairo.Core.Models;

namespace Kairo.Core.Security;

public enum RiskLevel
{
    /// <summary>Ordinary actions (open app, navigate, fill a form). Automatic within an authorized task.</summary>
    Normal = 0,
    /// <summary>Sending messages, submitting personal data, overwriting files … – explicit approval required.</summary>
    Sensitive = 1,
    /// <summary>Payments, permanent deletions, security settings – approval required every single time.</summary>
    Irreversible = 2,
    /// <summary>Never executed (blocked apps, credential stores).</summary>
    Forbidden = 3,
}

public sealed record RiskAssessment(RiskLevel Level, IReadOnlyList<string> Reasons, bool Ambiguous)
{
    public static RiskAssessment Normal { get; } = new(RiskLevel.Normal, [], false);
}

/// <summary>
/// Deterministic, rule based risk classification of actions. Runs locally in microseconds;
/// ambiguous cases are additionally checked with Jev by the <see cref="PermissionManager"/>.
/// </summary>
public static class RiskClassifier
{
    private static readonly string[] IrreversibleWords =
    [
        "bezahlen", "zahlen", "zahlung abschließen", "jetzt kaufen", "kaufen", "kostenpflichtig bestellen", "zahlungspflichtig bestellen",
        "bestellung abschließen", "überweisen", "überweisung ausführen", "pay", "pay now", "purchase", "buy now", "buy", "place order",
        "complete order", "complete purchase", "endgültig löschen", "unwiderruflich", "dauerhaft löschen", "permanently delete",
        "delete permanently", "delete account", "konto löschen", "formatieren", "deinstallieren", "uninstall", "factory reset",
        "auf werkseinstellungen zurücksetzen", "kündigen", "cancel subscription", "transfer money", "geld senden", "spenden", "donate",
    ];

    private static readonly string[] SensitiveWords =
    [
        "senden", "absenden", "abschicken", "verschicken", "einreichen", "übermitteln", "send", "submit", "post", "posten",
        "veröffentlichen", "publish", "teilen", "share", "antworten", "reply", "weiterleiten", "forward", "unterschreiben", "signieren",
        "sign", "bestätigen", "confirm", "registrieren", "register", "sign up", "konto erstellen", "create account", "bestellen", "order",
        "buchen", "book", "reservieren", "checkout", "zur kasse", "löschen", "delete", "papierkorb leeren", "empty trash", "überschreiben",
        "overwrite", "installieren", "install", "erlauben", "allow", "zulassen", "freigeben", "grant access", "einladen", "invite",
        "anrufen", "call", "hochladen", "upload", "tweet", "kommentieren", "comment", "abonnieren", "subscribe", "zurücksetzen", "reset",
    ];

    private static readonly string[] AmbiguousLabels =
    [
        "ok", "okay", "weiter", "next", "continue", "fortfahren", "fertig", "done", "finish", "abschließen", "ja", "yes", "übernehmen",
        "apply", "speichern und weiter", "los", "go", "start", "starten", "ausführen", "run",
    ];

    private static readonly string[] MessagingApps =
    [
        "outlook", "olk", "hxoutlook", "thunderbird", "teams", "ms-teams", "slack", "discord", "whatsapp", "telegram", "signal",
        "skype", "zoom", "mailspring", "em client", "mailbird",
    ];

    private static readonly string[] SecurityContexts =
    [
        "windows-sicherheit", "windows security", "sechealthui", "firewall", "regedit", "registrierungs-editor", "registry editor",
        "gpedit", "gruppenrichtlinie", "benutzerkontensteuerung", "user account control", "defender", "bitlocker", "certmgr",
        "anmeldeinformationsverwaltung", "credential manager", "mmc", "secpol", "lusrmgr", "netplwiz", "useraccountcontrolsettings",
        "datenschutz und sicherheit", "privacy & security", "privacy and security", "systemsteuerung\\benutzerkonten",
    ];

    private static readonly string[] PaymentUrlHints =
    [
        "checkout", "payment", "/pay", "billing", "kasse", "bezahlung", "zahlung", "paypal.com", "stripe.com", "klarna", "banking", "ebanking", "e-banking",
    ];

    private static readonly string[] ScriptExtensions = [".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".scr", ".reg"];
    private static readonly string[] InstallerExtensions = [".msi", ".msix", ".appx", ".appxbundle", ".msixbundle"];
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".lnk", ".url", ".appref-ms"];

    public static RiskAssessment Classify(AgentAction action, UiElement? element, UiSnapshot? snapshot, IReadOnlyCollection<string> blockedApplications)
    {
        var reasons = new List<string>();
        var level = RiskLevel.Normal;
        var ambiguous = false;

        void Raise(RiskLevel l, string reason)
        {
            if (l > level) { level = l; }
            reasons.Add(reason);
        }

        var window = snapshot?.Window;
        if (window is not null && blockedApplications.Any(b => string.Equals(b, window.ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            return new RiskAssessment(RiskLevel.Forbidden, [$"Die Anwendung „{window.ProcessName}“ ist für Kairo gesperrt."], false);
        }
        if (action.Kind is ActionKind.LaunchApp or ActionKind.SwitchWindow && action.App is { } appName &&
            blockedApplications.Any(b => Normalize(appName).Contains(Normalize(b), StringComparison.Ordinal)))
        {
            return new RiskAssessment(RiskLevel.Forbidden, [$"Die Anwendung „{appName}“ ist für Kairo gesperrt."], false);
        }

        var inSecurityContext = window is not null && ContainsAny(Normalize(window.Title + " " + window.ProcessName), SecurityContexts);
        var inMessagingApp = window is not null && ContainsAny(Normalize(window.ProcessName + " " + window.Title), MessagingApps);
        var url = Normalize(snapshot?.Url ?? "");
        var inPaymentContext = url.Length > 0 && ContainsAny(url, PaymentUrlHints);

        switch (action.Kind)
        {
            case ActionKind.Click:
            case ActionKind.MouseClick:
            {
                var label = Normalize(string.Join(' ', new[] { element?.Name, element?.HelpText, action.TargetLabel }
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase)));
                var primary = Normalize(element?.Name is { Length: > 0 } n ? n : action.TargetLabel);
                if (ContainsAnyWord(label, IrreversibleWords)) { Raise(RiskLevel.Irreversible, $"Schaltfläche „{Display(element, action)}“ löst eine Zahlung, Löschung oder vergleichbar endgültige Aktion aus."); }
                else if (ContainsAnyWord(label, SensitiveWords)) { Raise(RiskLevel.Sensitive, $"Schaltfläche „{Display(element, action)}“ sendet oder bestätigt etwas."); }
                else if (element?.InputHint is "submit") { Raise(RiskLevel.Sensitive, "Die Schaltfläche sendet ein Formular ab."); }
                else if (primary.Length == 0 || AmbiguousLabels.Contains(primary)) { ambiguous = true; }

                if (inPaymentContext && element?.Role is ElementRole.Button or ElementRole.Link or null)
                {
                    Raise(RiskLevel.Sensitive, "Die Seite ist eine Zahlungs- oder Bankseite.");
                    ambiguous = true;
                }
                if (inSecurityContext) { Raise(RiskLevel.Irreversible, "Änderung an Sicherheitseinstellungen."); }
                break;
            }
            case ActionKind.SetValue:
            case ActionKind.TypeText:
                if (element?.IsPassword == true) { Raise(RiskLevel.Sensitive, "Eingabe in ein Passwortfeld."); }
                if (inSecurityContext) { Raise(RiskLevel.Irreversible, "Änderung an Sicherheitseinstellungen."); }
                if (action.Kind == ActionKind.TypeText && inMessagingApp && (action.Value?.Contains('\n') ?? false))
                {
                    Raise(RiskLevel.Sensitive, "Zeilenumbruch in einer Messaging-App kann eine Nachricht senden.");
                }
                break;
            case ActionKind.SetChecked:
            case ActionKind.SelectOption:
                if (inSecurityContext) { Raise(RiskLevel.Irreversible, "Änderung an Sicherheitseinstellungen."); }
                break;
            case ActionKind.Hotkey:
            {
                var keys = Normalize(action.Keys ?? "").Replace(" ", "");
                if (keys.Contains("shift+delete") || keys.Contains("shift+del") || keys.Contains("umschalt+entf"))
                {
                    Raise(RiskLevel.Irreversible, "Umschalt+Entf löscht endgültig ohne Papierkorb.");
                }
                else if (keys is "delete" or "del" or "entf" && window?.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Raise(RiskLevel.Sensitive, "Löschen im Datei-Explorer.");
                }
                if (keys.Contains("ctrl+enter") || keys.Contains("strg+enter") || keys.Contains("alt+s") && inMessagingApp)
                {
                    Raise(RiskLevel.Sensitive, "Tastenkombination sendet üblicherweise eine Nachricht.");
                }
                if ((keys is "enter" or "return") && inMessagingApp)
                {
                    Raise(RiskLevel.Sensitive, "Enter sendet in Messaging-Apps die Nachricht.");
                }
                if (inSecurityContext) { Raise(RiskLevel.Irreversible, "Tastenkombination in Sicherheitseinstellungen."); }
                break;
            }
            case ActionKind.LaunchApp:
            case ActionKind.OpenFile:
            {
                var target = (action.Path ?? action.App ?? "").Trim().Trim('"');
                var ext = SafeExtension(target);
                if (ScriptExtensions.Contains(ext)) { Raise(RiskLevel.Irreversible, "Ausführen eines Skripts."); }
                else if (InstallerExtensions.Contains(ext) || Normalize(Path.GetFileName(target)).Contains("setup")) { Raise(RiskLevel.Irreversible, "Ausführen eines Installationsprogramms."); }
                else if (ExecutableExtensions.Contains(ext) && LooksUntrustedLocation(target)) { Raise(RiskLevel.Sensitive, "Programm aus einem Download- oder Benutzerordner."); }
                if (ContainsAny(Normalize(target), SecurityContexts) || Normalize(target).StartsWith("ms-settings:privacy") || Normalize(target).StartsWith("windowsdefender:"))
                {
                    Raise(RiskLevel.Sensitive, "Öffnet Sicherheitseinstellungen.");
                }
                break;
            }
            case ActionKind.OpenUrl:
            {
                var u = (action.Url ?? "").Trim();
                if (!(u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    Raise(RiskLevel.Sensitive, "Nicht-Web-Adresse (Protokoll-Handler).");
                }
                break;
            }
            case ActionKind.DeleteFile:
                Raise(RiskLevel.Sensitive, "Datei wird gelöscht (Papierkorb).");
                break;
            case ActionKind.MoveFile:
            case ActionKind.RenameFile:
            case ActionKind.CopyFile:
                break;
            case ActionKind.WriteTextFile:
                break;
            case ActionKind.CloseWindow:
                break;
            case ActionKind.ClipboardGet:
                break;
        }

        return new RiskAssessment(level, reasons, ambiguous && level == RiskLevel.Normal);
    }

    private static string Display(UiElement? element, AgentAction action) =>
        element?.DisplayLabel is { Length: > 0 } l ? l : action.TargetLabel ?? "Unbenannt";

    private static bool LooksUntrustedLocation(string path)
    {
        var p = Normalize(path);
        return p.Contains("\\downloads\\") || p.Contains("\\temp\\") || p.Contains("\\appdata\\local\\temp") || p.Contains("/downloads/");
    }

    private static string SafeExtension(string path)
    {
        try { return Path.GetExtension(path).ToLowerInvariant(); }
        catch (ArgumentException) { return ""; }
    }

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var lower = text.ToLowerInvariant().Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    private static bool ContainsAny(string haystack, IEnumerable<string> needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    /// <summary>Word-boundary aware keyword matching ("send" must not match "sendung", but "absenden" matches).</summary>
    internal static bool ContainsAnyWord(string haystack, IEnumerable<string> keywords)
    {
        if (haystack.Length == 0) { return false; }
        foreach (var keyword in keywords)
        {
            var index = 0;
            while ((index = haystack.IndexOf(keyword, index, StringComparison.Ordinal)) >= 0)
            {
                var before = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
                var endIndex = index + keyword.Length;
                var after = endIndex >= haystack.Length || !char.IsLetterOrDigit(haystack[endIndex]);
                // German compounds: allow suffix inflections ("absenden" → "absenden", "Nachricht senden")
                if (before && (after || GermanInflection(haystack, endIndex)))
                {
                    return true;
                }
                index = endIndex;
            }
        }
        return false;
    }

    private static bool GermanInflection(string text, int endIndex)
    {
        // Accept short inflection suffixes such as "-en", "-e", "-n", "-t" directly after a keyword.
        var rest = text.AsSpan(endIndex);
        var len = 0;
        while (len < rest.Length && char.IsLetter(rest[len])) { len++; }
        return len <= 2;
    }

    public static string Describe(RiskLevel level) => level switch
    {
        RiskLevel.Normal => "gewöhnlich",
        RiskLevel.Sensitive => "sensibel",
        RiskLevel.Irreversible => "nicht umkehrbar",
        _ => "gesperrt",
    };

    public static string FormatPercent(double p) => p.ToString("P0", CultureInfo.GetCultureInfo("de-CH"));
}
