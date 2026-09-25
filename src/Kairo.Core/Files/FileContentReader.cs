using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Kairo.Core.Files;

public enum FileKind
{
    Text,
    Pdf,
    Docx,
    Xlsx,
    Csv,
    Json,
    Html,
    Unsupported,
}

/// <summary>Text extracted locally from a file.</summary>
public sealed record FileContent
{
    public required string Path { get; init; }
    public required FileKind Kind { get; init; }
    public required string Text { get; init; }
    public long SizeBytes { get; init; }
    public int? Pages { get; init; }
    public IReadOnlyList<string>? Sheets { get; init; }
    public string? Warning { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Reads TXT, PDF, DOCX, CSV, JSON, XLSX (plus MD/XML/HTML/LOG) completely locally.
/// Nothing is uploaded here; the agent later decides which excerpts go to the planner.
/// </summary>
public sealed partial class FileContentReader
{
    private const int MaxRowsPerSheet = 1000;
    private const int MaxColumns = 40;

    static FileContentReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static IReadOnlyList<string> SupportedExtensions { get; } =
        [".txt", ".md", ".log", ".xml", ".html", ".htm", ".pdf", ".docx", ".csv", ".tsv", ".json", ".xlsx", ".xlsm", ".ini", ".yaml", ".yml", ".rtf", ".vcf"];

    public static FileKind DetectKind(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => FileKind.Pdf,
        ".docx" or ".docm" => FileKind.Docx,
        ".xlsx" or ".xlsm" => FileKind.Xlsx,
        ".csv" or ".tsv" => FileKind.Csv,
        ".json" => FileKind.Json,
        ".html" or ".htm" => FileKind.Html,
        ".txt" or ".md" or ".log" or ".xml" or ".ini" or ".yaml" or ".yml" or ".rtf" or ".vcf" or ".text" or "" => FileKind.Text,
        _ => FileKind.Unsupported,
    };

    public async Task<FileContent> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"Die Datei „{path}“ wurde nicht gefunden.", path);
        }
        if (info.Length > maxBytes)
        {
            throw new IOException($"Die Datei ist zu groß ({info.Length / 1024 / 1024} MB, erlaubt sind {maxBytes / 1024 / 1024} MB).");
        }

        var kind = DetectKind(path);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return kind switch
            {
                FileKind.Pdf => ReadPdf(info),
                FileKind.Docx => ReadDocx(info),
                FileKind.Xlsx => ReadXlsx(info, cancellationToken),
                FileKind.Csv => ReadCsv(info),
                FileKind.Json => ReadJson(info),
                FileKind.Html => new FileContent { Path = info.FullName, Kind = kind, Text = HtmlToText(ReadText(info.FullName)), SizeBytes = info.Length },
                FileKind.Text => ReadPlain(info),
                _ => ReadUnknown(info),
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    private static FileContent ReadPlain(FileInfo info)
    {
        var text = ReadText(info.FullName);
        if (info.Extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase)) { text = RtfToText(text); }
        return new FileContent { Path = info.FullName, Kind = FileKind.Text, Text = text, SizeBytes = info.Length };
    }

    private static FileContent ReadUnknown(FileInfo info)
    {
        // Try as text when the file looks textual (no NUL bytes in the first 8 KB).
        using var fs = info.OpenRead();
        var buffer = new byte[Math.Min(8192, info.Length)];
        var read = fs.Read(buffer, 0, buffer.Length);
        if (buffer.AsSpan(0, read).Contains((byte)0))
        {
            throw new NotSupportedException($"Das Dateiformat „{info.Extension}“ wird nicht unterstützt.");
        }
        return ReadPlain(info);
    }

    /// <summary>Reads text with BOM detection; falls back to Windows-1252 when the bytes are not valid UTF-8.</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    private static FileContent ReadPdf(FileInfo info)
    {
        var sb = new StringBuilder();
        int pageCount;
        string? warning = null;
        try
        {
            using var document = PdfDocument.Open(info.FullName);
            pageCount = document.NumberOfPages;
            foreach (var page in document.GetPages())
            {
                string text;
                try
                {
                    text = ContentOrderTextExtractor.GetText(page);
                }
                catch (Exception)
                {
                    text = page.Text;
                }

                if (pageCount > 1) { sb.Append("--- Seite ").Append(page.Number).AppendLine(" ---"); }
                sb.AppendLine(text.Trim());
            }
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Encrypt", StringComparison.Ordinal) || ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Das PDF ist verschlüsselt oder passwortgeschützt und kann nicht gelesen werden.", ex);
        }

        var result = sb.ToString().Trim();
        if (result.Replace("--- Seite", "").Trim().Length < 20 && pageCount > 0)
        {
            warning = "Das PDF enthält kaum Text (vermutlich ein Scan). Für Scans wird der Bildschirm-Fallback benötigt.";
        }

        return new FileContent { Path = info.FullName, Kind = FileKind.Pdf, Text = result, SizeBytes = info.Length, Pages = pageCount, Warning = warning };
    }

    private static FileContent ReadDocx(FileInfo info)
    {
        using var doc = WordprocessingDocument.Open(info.FullName, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        var sb = new StringBuilder();
        if (body is not null)
        {
            foreach (var element in body.ChildElements)
            {
                switch (element)
                {
                    case W.Paragraph p:
                        var text = ParagraphText(p);
                        if (text.Length > 0) { sb.AppendLine(text); }
                        break;
                    case W.Table table:
                        foreach (var row in table.Elements<W.TableRow>())
                        {
                            var cells = row.Elements<W.TableCell>()
                                .Select(c => string.Join(" ", c.Elements<W.Paragraph>().Select(ParagraphText)).Trim());
                            sb.AppendLine(string.Join(" | ", cells));
                        }
                        sb.AppendLine();
                        break;
                }
            }
        }
        return new FileContent { Path = info.FullName, Kind = FileKind.Docx, Text = sb.ToString().Trim(), SizeBytes = info.Length };
    }

    private static string ParagraphText(W.Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var node in p.Descendants())
        {
            switch (node)
            {
                case W.Text t: sb.Append(t.Text); break;
                case W.TabChar: sb.Append('\t'); break;
                case W.Break: sb.Append('\n'); break;
            }
        }
        return sb.ToString().Trim();
    }

    private static FileContent ReadXlsx(FileInfo info, CancellationToken cancellationToken)
    {
        using var doc = SpreadsheetDocument.Open(info.FullName, false);
        var workbookPart = doc.WorkbookPart ?? throw new IOException("Die Excel-Datei enthält keine Arbeitsmappe.");
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
        var dateStyleIndexes = DateStyleIndexes(workbookPart);
        var sb = new StringBuilder();
        var sheetNames = new List<string>();
        var truncated = false;

        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sheet.Id?.Value is not { } relId || workbookPart.GetPartById(relId) is not WorksheetPart wsPart) { continue; }
            var name = sheet.Name?.Value ?? "Tabelle";
            sheetNames.Add(name);
            sb.Append("## Tabellenblatt: ").AppendLine(name);

            var rows = 0;
            foreach (var row in wsPart.Worksheet?.Descendants<Row>() ?? [])
            {
                if (++rows > MaxRowsPerSheet) { truncated = true; break; }
                var values = new SortedDictionary<int, string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var col = ColumnIndex(cell.CellReference?.Value);
                    if (col >= MaxColumns) { continue; }
                    var v = CellValue(cell, sharedStrings, dateStyleIndexes);
                    if (v.Length > 0) { values[col] = v; }
                }
                if (values.Count == 0) { continue; }
                var max = values.Keys.Max();
                var line = string.Join(" | ", Enumerable.Range(0, max + 1).Select(i => values.TryGetValue(i, out var s) ? s : ""));
                sb.Append(row.RowIndex?.Value.ToString(CultureInfo.InvariantCulture) ?? "?").Append(": ").AppendLine(line);
            }
            sb.AppendLine();
        }

        return new FileContent
        {
            Path = info.FullName,
            Kind = FileKind.Xlsx,
            Text = sb.ToString().Trim(),
            SizeBytes = info.Length,
            Sheets = sheetNames,
            Warning = truncated ? $"Nur die ersten {MaxRowsPerSheet} Zeilen je Blatt wurden gelesen." : null,
        };
    }

    private static HashSet<uint> DateStyleIndexes(WorkbookPart workbookPart)
    {
        var result = new HashSet<uint>();
        var formats = workbookPart.WorkbookStylesPart?.Stylesheet?.CellFormats?.Elements<CellFormat>().ToList();
        if (formats is null) { return result; }
        var customDateFormats = workbookPart.WorkbookStylesPart?.Stylesheet?.NumberingFormats?.Elements<NumberingFormat>()
            .Where(f => f.FormatCode?.Value is { } code && DateFormatCode().IsMatch(code))
            .Select(f => f.NumberFormatId?.Value ?? 0).ToHashSet() ?? [];
        for (var i = 0; i < formats.Count; i++)
        {
            var id = formats[i].NumberFormatId?.Value ?? 0;
            if (id is >= 14 and <= 22 or >= 45 and <= 47 || customDateFormats.Contains(id)) { result.Add((uint)i); }
        }
        return result;
    }

    [GeneratedRegex(@"(?<!\\)[dmyhs]", RegexOptions.IgnoreCase)]
    private static partial Regex DateFormatCode();

    private static string CellValue(Cell cell, string[] sharedStrings, HashSet<uint> dateStyles)
    {
        var raw = cell.CellValue?.Text ?? cell.InlineString?.InnerText ?? "";
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var idx) && idx >= 0 && idx < sharedStrings.Length)
        {
            return sharedStrings[idx].Trim();
        }
        if (cell.DataType?.Value == CellValues.Boolean) { return raw == "1" ? "WAHR" : "FALSCH"; }
        if (cell.DataType?.Value == CellValues.InlineString) { return (cell.InlineString?.InnerText ?? raw).Trim(); }
        if (cell.StyleIndex?.Value is { } style && dateStyles.Contains(style) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa is > 0 and < 2958465)
        {
            var date = DateTime.FromOADate(oa);
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : date.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }
        return raw.Trim();
    }

    internal static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) { return 0; }
        var index = 0;
        foreach (var c in reference)
        {
            if (!char.IsLetter(c)) { break; }
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }
        return Math.Max(0, index - 1);
    }

    private static FileContent ReadCsv(FileInfo info)
    {
        var text = ReadText(info.FullName);
        var rows = CsvParser.Parse(text, CsvParser.DetectDelimiter(text, info.Extension));
        var sb = new StringBuilder();
        var truncated = rows.Count > MaxRowsPerSheet;
        foreach (var row in rows.Take(MaxRowsPerSheet))
        {
            sb.AppendLine(string.Join(" | ", row.Take(MaxColumns)));
        }
        return new FileContent
        {
            Path = info.FullName,
            Kind = FileKind.Csv,
            Text = sb.ToString().Trim(),
            SizeBytes = info.Length,
            Warning = truncated ? $"Nur die ersten {MaxRowsPerSheet} Zeilen wurden gelesen." : null,
        };
    }

    private static FileContent ReadJson(FileInfo info)
    {
        var text = ReadText(info.FullName);
        string pretty;
        string? warning = null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (JsonException)
        {
            pretty = text;
            warning = "Die JSON-Datei ist nicht gültig; sie wurde als Text gelesen.";
        }
        return new FileContent { Path = info.FullName, Kind = FileKind.Json, Text = pretty, SizeBytes = info.Length, Warning = warning };
    }

    public static string HtmlToText(string html)
    {
        var text = ScriptOrStyle().Replace(html, " ");
        text = BlockTags().Replace(text, "\n");
        text = AnyTag().Replace(text, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = MultiSpace().Replace(text, " ");
        text = MultiNewline().Replace(text, "\n\n");
        return text.Trim();
    }

    private static string RtfToText(string rtf)
    {
        var text = RtfControl().Replace(rtf, " ");
        text = text.Replace("{", "").Replace("}", "");
        return MultiSpace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/tr|/h[1-6]|/table|/section|/article)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex MultiNewline();

    [GeneratedRegex(@"\\[a-z]+-?\d* ?|\\'[0-9a-f]{2}", RegexOptions.IgnoreCase)]
    private static partial Regex RtfControl();
}

/// <summary>RFC 4180 CSV parser with delimiter detection (comma, semicolon, tab, pipe).</summary>
public static class CsvParser
{
    public static char DetectDelimiter(string text, string extension = "")
    {
        if (extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase)) { return '\t'; }
        var firstLines = string.Join('\n', text.Split('\n').Take(5));
        char[] candidates = [';', ',', '\t', '|'];
        return candidates
            .Select(c => (Char: c, Count: firstLines.Count(ch => ch == c)))
            .OrderByDescending(x => x.Count)
            .First().Char is var best && firstLines.Contains(best) ? best : ',';
    }

    public static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else { inQuotes = false; }
                }
                else { field.Append(c); }
                continue;
            }

            if (c == '"' && field.Length == 0) { inQuotes = true; }
            else if (c == delimiter) { row.Add(field.ToString().Trim()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { i++; }
                row.Add(field.ToString().Trim());
                field.Clear();
                if (row.Any(f => f.Length > 0)) { rows.Add(row); }
                row = [];
            }
            else { field.Append(c); }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().Trim());
            if (row.Any(f => f.Length > 0)) { rows.Add(row); }
        }
        return rows;
    }
}
