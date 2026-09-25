using System.Text.RegularExpressions;

namespace Kairo.Core.Files;

/// <summary>
/// Finds file references in a natural language instruction ("C:\Users\Max\Documents\Kontakt.pdf",
/// "die Excel-Datei auf meinem Desktop") so Kairo can read them in parallel before planning.
/// </summary>
public static partial class PathExtractor
{
    public sealed record FolderHint(string Folder, string? Extension, string Description);

    /// <summary>Absolute Windows paths, quoted paths and %ENV% paths contained in the text.</summary>
    public static IReadOnlyList<string> ExtractPaths(string instruction)
    {
        var results = new List<string>();
        foreach (Match m in QuotedPath().Matches(instruction))
        {
            results.Add(m.Groups["p"].Value.Trim());
        }
        foreach (Match m in WindowsPath().Matches(instruction))
        {
            var p = m.Value.TrimEnd('.', ',', ';', ':', ')', '!', '?', '“', '"', '\'');
            if (!results.Any(r => r.Contains(p, StringComparison.OrdinalIgnoreCase))) { results.Add(p); }
        }
        foreach (Match m in EnvPath().Matches(instruction))
        {
            var p = m.Value.TrimEnd('.', ',', ';', ':', ')', '!', '?');
            if (!results.Contains(p, StringComparer.OrdinalIgnoreCase)) { results.Add(p); }
        }
        // Unix style absolute paths (used on non-Windows development machines and in tests).
        foreach (Match m in UnixPath().Matches(instruction))
        {
            var p = m.Groups["p"].Value;
            if (!results.Any(r => r.Contains(p, StringComparison.Ordinal))) { results.Add(p); }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Recognizes phrases like "Excel-Datei auf meinem Desktop" or "PDF in Downloads" and returns the folder
    /// plus the file type so the candidates can be listed locally before the first planner call.
    /// </summary>
    public static IReadOnlyList<FolderHint> ExtractFolderHints(string instruction)
    {
        var lower = instruction.ToLowerInvariant();
        var hints = new List<FolderHint>();
        string? extension = null;
        foreach (var (words, ext) in TypeWords)
        {
            if (words.Any(w => lower.Contains(w))) { extension = ext; break; }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        void Add(string[] words, Func<string> folder, string name)
        {
            if (words.Any(w => lower.Contains(w)))
            {
                var path = folder();
                if (!string.IsNullOrEmpty(path)) { hints.Add(new FolderHint(path, extension, name)); }
            }
        }

        Add(["desktop", "schreibtisch"], () => Environment.GetFolderPath(Environment.SpecialFolder.Desktop, Environment.SpecialFolderOption.DoNotVerify), "Desktop");
        Add(["dokumente", "documents", "eigene dateien"], () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), "Dokumente");
        Add(["downloads", "download-ordner", "heruntergeladen"], () => string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, "Downloads"), "Downloads");
        Add(["bilder", "pictures", "fotos"], () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify), "Bilder");
        return hints;
    }

    private static readonly (string[] Words, string Ext)[] TypeWords =
    [
        (["excel", "xlsx", "tabelle", "spreadsheet"], ".xlsx"),
        (["pdf"], ".pdf"),
        (["word-datei", "word datei", "worddokument", "word-dokument", "docx"], ".docx"),
        (["csv"], ".csv"),
        (["json"], ".json"),
        (["textdatei", "text-datei", ".txt", "txt-datei"], ".txt"),
    ];

    [GeneratedRegex(@"[""“„'](?<p>(?:[A-Za-z]:\\|\\\\|%[A-Za-z]+%\\)[^""“”'\r\n]+)[""”'“]")]
    private static partial Regex QuotedPath();

    // C:\folder\file name.ext – spaces are allowed inside folders; the match stops at the file extension.
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\[^\\\s]+\\)(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*?(?:\.[A-Za-z0-9]{1,5})(?=$|[\s,;:!?)""“”'.])")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"%[A-Za-z]+%(?:\\[^\\/:*?""<>|\r\n\s]+)+")]
    private static partial Regex EnvPath();

    [GeneratedRegex(@"(?:^|(?<=[\s""“„']))(?<p>/(?:[^/\s""“”']+/)+[^/\s""“”']+\.[A-Za-z0-9]{1,5})(?=$|[\s,;:!?)""“”'.])")]
    private static partial Regex UnixPath();
}
