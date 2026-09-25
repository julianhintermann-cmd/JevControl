using System.Text.RegularExpressions;

namespace Kairo.Core.Security;

/// <summary>
/// Per-task vault that replaces highly sensitive values (credit card numbers, IBANs) with placeholders
/// before text is sent to an external model. The planner copies the placeholders into actions and the
/// executor substitutes the real values locally, so the numbers never leave the machine.
/// </summary>
public sealed partial class SecretVault
{
    private readonly Dictionary<string, string> _tokenToValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _valueToToken = new(StringComparer.Ordinal);
    private int _counter;

    public bool Enabled { get; init; } = true;

    public int Count => _tokenToValue.Count;

    /// <summary>Replaces sensitive numbers in <paramref name="text"/> with stable placeholders.</summary>
    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Enabled) { return text ?? ""; }

        text = CardNumber().Replace(text, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            return digits.Length is >= 13 and <= 19 && PassesLuhn(digits) ? Tokenize("karte", m.Value, digits[^4..]) : m.Value;
        });

        text = Iban().Replace(text, m =>
        {
            var compact = m.Value.Replace(" ", "").ToUpperInvariant();
            return IsValidIban(compact) ? Tokenize("iban", m.Value, compact[^4..]) : m.Value;
        });

        return text;
    }

    /// <summary>Substitutes placeholders with the real values (executor side only).</summary>
    public string Unmask(string? text)
    {
        if (string.IsNullOrEmpty(text) || _tokenToValue.Count == 0) { return text ?? ""; }
        return Placeholder().Replace(text, m => _tokenToValue.TryGetValue(m.Value, out var v) ? v : m.Value);
    }

    public bool ContainsPlaceholder(string? text) => !string.IsNullOrEmpty(text) && Placeholder().IsMatch(text);

    private string Tokenize(string kind, string original, string lastFour)
    {
        if (_valueToToken.TryGetValue(original, out var existing)) { return existing; }
        var token = $"{{{{kairo:{kind}_{++_counter}_endet_{lastFour}}}}}";
        _tokenToValue[token] = original;
        _valueToToken[original] = token;
        return token;
    }

    internal static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) { n -= 9; }
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    internal static bool IsValidIban(string iban)
    {
        if (iban.Length is < 15 or > 34) { return false; }
        var rearranged = iban[4..] + iban[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            int value;
            if (char.IsDigit(c)) { value = c - '0'; }
            else if (c is >= 'A' and <= 'Z') { value = c - 'A' + 10; }
            else { return false; }

            remainder = value >= 10 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }

    [GeneratedRegex(@"\b(?:\d[ -]?){12,18}\d\b")]
    private static partial Regex CardNumber();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]{4}){2,7}(?:[ ]?[A-Z0-9]{1,4})?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Iban();

    [GeneratedRegex(@"\{\{kairo:[a-z]+_\d+_endet_[A-Za-z0-9]{4}\}\}")]
    private static partial Regex Placeholder();
}
