using System.Globalization;
using System.Text;
using Kairo.Core.Models;

namespace Kairo.Core.Perception;

/// <summary>
/// Fast local lexical matching between a planned target ("E-Mail") and snapshot elements.
/// Used to pre-rank Jev candidates and to re-find elements after the UI changed.
/// </summary>
public static class ElementMatcher
{
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["email"] = ["e-mail", "email", "mail", "e-mail-adresse", "emailadresse", "courriel"],
        ["phone"] = ["telefon", "telefonnummer", "phone", "tel", "mobile", "mobil", "handy", "rufnummer", "mobilnummer"],
        ["firstname"] = ["vorname", "first name", "firstname", "given name", "given-name", "prénom"],
        ["lastname"] = ["nachname", "last name", "lastname", "surname", "family name", "family-name", "familienname", "name"],
        ["fullname"] = ["name", "vollständiger name", "full name", "ihr name", "your name", "vor- und nachname"],
        ["company"] = ["firma", "unternehmen", "company", "organisation", "organization", "arbeitgeber"],
        ["street"] = ["straße", "strasse", "street", "adresse", "address", "anschrift", "address-line1"],
        ["zip"] = ["plz", "postleitzahl", "zip", "postal code", "postal-code", "postcode"],
        ["city"] = ["ort", "stadt", "city", "wohnort", "address-level2"],
        ["country"] = ["land", "country", "staat"],
        ["message"] = ["nachricht", "message", "anliegen", "mitteilung", "kommentar", "comment", "ihre nachricht", "beschreibung"],
        ["subject"] = ["betreff", "subject", "thema"],
        ["birthday"] = ["geburtsdatum", "birthday", "date of birth", "bday", "geburtstag"],
        ["submit"] = ["senden", "absenden", "abschicken", "submit", "send", "weiter", "next"],
    };

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return ""; }
        var lower = text.ToLowerInvariant().Replace('ß', 's').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) { continue; }
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Similarity in [0,1] between a wanted label and an element.</summary>
    public static double Similarity(string? wanted, UiElement element)
    {
        var w = Normalize(wanted);
        if (w.Length == 0) { return 0; }
        var candidates = new[] { element.Name, element.Placeholder, element.HelpText, element.AutomationId, element.InputHint }
            .Select(Normalize).Where(s => s.Length > 0).ToList();
        if (candidates.Count == 0) { return 0; }

        var best = 0.0;
        foreach (var c in candidates)
        {
            best = Math.Max(best, TextSimilarity(w, c));
        }

        // Semantic synonym groups (E-Mail ↔ email, Telefon ↔ phone …)
        foreach (var group in Synonyms.Values)
        {
            var wantedIn = group.Any(s => ContainsPhrase(w, Normalize(s)));
            if (!wantedIn) { continue; }
            if (candidates.Any(c => group.Any(s => ContainsPhrase(c, Normalize(s))))) { best = Math.Max(best, 0.8); }
        }
        return best;
    }

    private static bool ContainsPhrase(string text, string phrase) =>
        phrase.Length > 0 && (text == phrase || text.StartsWith(phrase + " ", StringComparison.Ordinal) ||
                              text.EndsWith(" " + phrase, StringComparison.Ordinal) || text.Contains(" " + phrase + " ", StringComparison.Ordinal));

    public static double TextSimilarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) { return 0; }
        if (a == b) { return 1; }
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            return 0.75 + 0.2 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        }

        var ta = a.Split(' ').ToHashSet();
        var tb = b.Split(' ').ToHashSet();
        var jaccard = (double)ta.Intersect(tb).Count() / ta.Union(tb).Count();
        var lev = 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        return Math.Max(jaccard * 0.9, lev * 0.85);
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length > 64) { a = a[..64]; }
        if (b.Length > 64) { b = b[..64]; }
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) { prev[j] = j; }
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Whether an element can receive the given action kind.</summary>
    public static bool IsCompatible(ActionKind kind, UiElement e) => kind switch
    {
        ActionKind.SetValue => e.IsEnabled && !e.IsReadOnly && !e.IsPassword &&
                               (e.Has(ElementCapabilities.SetValue) || e.Role is ElementRole.Edit or ElementRole.Document or ElementRole.ComboBox or ElementRole.Spinner),
        ActionKind.SelectOption => e.IsEnabled && (e.Role is ElementRole.ComboBox or ElementRole.List or ElementRole.RadioButton || e.Options is { Count: > 0 } || e.Has(ElementCapabilities.ExpandCollapse)),
        ActionKind.SetChecked => e.IsEnabled && (e.Role is ElementRole.CheckBox or ElementRole.RadioButton || e.Has(ElementCapabilities.Toggle) || e.IsChecked is not null),
        ActionKind.Click => e.IsEnabled && e.IsClickable,
        ActionKind.Focus => e.IsEnabled && (e.Has(ElementCapabilities.Focus) || e.IsEditable || e.IsClickable),
        _ => false,
    };

    /// <summary>
    /// Top-k compatible elements for a planned element action, ranked by similarity to the planned label/value.
    /// The planner's hinted element (if compatible) is always included.
    /// </summary>
    public static IReadOnlyList<(UiElement Element, double Score)> RankCandidates(AgentAction action, UiSnapshot snapshot, int maxCandidates)
    {
        var hint = action.TargetId is { } id ? snapshot.Find(id) : null;
        var wanted = string.Join(' ', new[] { action.TargetLabel, action.Description }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var scored = new List<(UiElement Element, double Score)>();
        foreach (var e in snapshot.Elements)
        {
            if (!IsCompatible(action.Kind, e)) { continue; }
            var score = Math.Max(Similarity(action.TargetLabel, e), 0.6 * Similarity(wanted, e));
            score += ValueTypeAffinity(action, e);
            if (hint is not null && e.Id == hint.Id) { score += 0.5; }
            if (e.IsOffscreen) { score -= 0.05; }
            scored.Add((e, score));
        }

        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Element.Id).Take(maxCandidates).ToList();
    }

    /// <summary>Bonus when the value looks like what the field expects (email → email field).</summary>
    private static double ValueTypeAffinity(AgentAction action, UiElement e)
    {
        if (action.Kind != ActionKind.SetValue || string.IsNullOrEmpty(action.Value)) { return 0; }
        var v = action.Value;
        var fieldText = Normalize(string.Join(' ', e.Name, e.Placeholder, e.InputHint, e.AutomationId));
        if (v.Contains('@') && (fieldText.Contains("mail") || e.InputHint == "email")) { return 0.3; }
        if (v.Count(char.IsDigit) >= 7 && (fieldText.Contains("tel") || fieldText.Contains("phone") || fieldText.Contains("mobil") || fieldText.Contains("handy"))) { return 0.3; }
        if (v.Contains('\n') && e.IsMultiline) { return 0.2; }
        return 0;
    }
}
