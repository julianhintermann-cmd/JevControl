using System.Text;
using System.Text.RegularExpressions;

namespace Kairo.Core.Files;

/// <summary>
/// Data minimization: when a document is larger than the budget, only the chunks that are relevant to
/// the task (instruction words, form labels) are sent to the external model – selected locally.
/// </summary>
public static partial class RelevantContentSelector
{
    public sealed record Selection(string Text, bool Reduced, int OriginalChars);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "und", "oder", "ein", "eine", "einen", "dem", "den", "des", "in", "im", "auf", "aus", "mit", "von", "zu",
        "zum", "zur", "für", "ist", "sind", "bitte", "meine", "mein", "meinem", "meinen", "ich", "du", "sie", "es", "an", "am",
        "the", "and", "or", "a", "an", "to", "of", "in", "on", "for", "with", "my", "is", "are", "please", "from", "into", "this", "that",
        "datei", "file", "öffne", "open", "fülle", "fill", "trage", "ein", "aus", "dort", "hier", "gerade", "habe", "findest", "unter",
    };

    public static Selection Select(string text, int budgetChars, IEnumerable<string> queryTexts)
    {
        if (text.Length <= budgetChars)
        {
            return new Selection(text, false, text.Length);
        }

        var terms = Terms(string.Join(' ', queryTexts));
        var chunks = SplitChunks(text, 600);
        if (chunks.Count == 0) { return new Selection(text[..budgetChars], true, text.Length); }

        var scored = chunks.Select((c, i) => (Index: i, Chunk: c, Score: Score(c, terms) + (i == 0 ? 2.0 : 0.0))).ToList();
        var chosen = new SortedSet<int>();
        var used = 0;
        foreach (var item in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Index))
        {
            if (used + item.Chunk.Length + 8 > budgetChars) { continue; }
            chosen.Add(item.Index);
            used += item.Chunk.Length + 8;
        }

        var sb = new StringBuilder(used + 64);
        var last = -1;
        foreach (var i in chosen)
        {
            if (last >= 0 && i != last + 1) { sb.AppendLine("[…]"); }
            sb.AppendLine(chunks[i]);
            last = i;
        }
        if (last != chunks.Count - 1) { sb.AppendLine("[…]"); }
        return new Selection(sb.ToString().TrimEnd(), true, text.Length);
    }

    private static List<string> SplitChunks(string text, int targetSize)
    {
        var paragraphs = ParagraphSplit().Split(text).Where(p => p.Trim().Length > 0).ToList();
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var p in paragraphs)
        {
            if (p.Length > targetSize * 2)
            {
                if (current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
                foreach (var line in p.Split('\n'))
                {
                    if (current.Length + line.Length > targetSize && current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
                    current.AppendLine(line);
                }
                continue;
            }
            if (current.Length + p.Length > targetSize && current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
            current.AppendLine(p.Trim());
        }
        if (current.Length > 0) { chunks.Add(current.ToString().Trim()); }
        return chunks;
    }

    internal static HashSet<string> Terms(string text) =>
        WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static double Score(string chunk, HashSet<string> terms)
    {
        if (terms.Count == 0) { return 0; }
        var words = WordPattern().Matches(chunk.ToLowerInvariant()).Select(m => m.Value).ToList();
        if (words.Count == 0) { return 0; }
        var hits = 0.0;
        foreach (var w in words)
        {
            if (terms.Contains(w)) { hits += 1; }
            else if (w.Length >= 5 && terms.Any(t => t.Length >= 5 && (w.StartsWith(t[..5], StringComparison.Ordinal)))) { hits += 0.5; }
        }
        // Contact-like data is almost always relevant for form filling.
        if (EmailOrPhone().IsMatch(chunk)) { hits += 3; }
        return hits / Math.Sqrt(words.Count);
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphSplit();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-]*")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+|\+?\d[\d\s/()-]{7,}\d")]
    private static partial Regex EmailOrPhone();
}
