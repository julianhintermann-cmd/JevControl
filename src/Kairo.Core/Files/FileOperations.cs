using System.Diagnostics;
using System.Globalization;
using System.Text;
using Kairo.Core.Models;
using Kairo.Core.Security;

namespace Kairo.Core.Files;

/// <summary>Moves files to the recycle bin (Windows shell). Returns false when not possible.</summary>
public interface IRecycleBin
{
    bool MoveToRecycleBin(string path);
}

/// <summary>
/// Executes file actions (read, search, list, create, rename, move, copy, delete, write).
/// The permission manager has already checked the paths; this class re-validates them defensively.
/// </summary>
public sealed class FileOperations
{
    private readonly FileContentReader _reader;
    private readonly IRecycleBin? _recycleBin;

    public FileOperations(FileContentReader reader, IRecycleBin? recycleBin)
    {
        _reader = reader;
        _recycleBin = recycleBin;
    }

    /// <summary>Resolves "Desktop", "Dokumente", "Downloads" … and relative paths.</summary>
    public static string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return ""; }
        var p = path.Trim().Trim('"', '“', '”', '„');
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["desktop"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop, Environment.SpecialFolderOption.DoNotVerify),
            ["schreibtisch"] = Environment.GetFolderPath(Environment.SpecialFolder.Desktop, Environment.SpecialFolderOption.DoNotVerify),
            ["dokumente"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
            ["documents"] = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
            ["downloads"] = string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, "Downloads"),
            ["bilder"] = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify),
            ["pictures"] = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify),
            ["musik"] = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic, Environment.SpecialFolderOption.DoNotVerify),
            ["videos"] = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify),
        };

        var firstSegmentEnd = p.IndexOfAny(['\\', '/']);
        var first = firstSegmentEnd < 0 ? p : p[..firstSegmentEnd];
        if (known.TryGetValue(first, out var root) && !string.IsNullOrEmpty(root))
        {
            p = firstSegmentEnd < 0 ? root : Path.Combine(root, p[(firstSegmentEnd + 1)..]);
        }
        else if (!Path.IsPathRooted(p) && !p.StartsWith('%') && !p.StartsWith('~') && !string.IsNullOrEmpty(profile))
        {
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), p);
        }
        return FileAccessPolicy.NormalizePath(p);
    }

    public async Task<ActionResult> ExecuteAsync(AgentAction action, TaskSecurityContext security, long maxReadBytes, int maxChars, IReadOnlyCollection<string> relevanceHints, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = action.Kind switch
            {
                ActionKind.ReadFile => await ReadAsync(action, security, maxReadBytes, maxChars, relevanceHints, cancellationToken).ConfigureAwait(false),
                ActionKind.SearchFiles => Search(action, security, cancellationToken),
                ActionKind.ListFolder => List(action, security),
                ActionKind.CreateFolder => CreateFolder(action, security),
                ActionKind.RenameFile => Rename(action, security),
                ActionKind.MoveFile => MoveOrCopy(action, security, move: true),
                ActionKind.CopyFile => MoveOrCopy(action, security, move: false),
                ActionKind.DeleteFile => Delete(action, security),
                ActionKind.WriteTextFile => await WriteAsync(action, security, cancellationToken).ConfigureAwait(false),
                _ => ActionResult.Fail(ActionErrorKind.NotSupported, $"{action.Kind} ist keine Dateiaktion."),
            };
            return result with { Duration = sw.Elapsed };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return ActionResult.Fail(ex is UnauthorizedAccessException ? ActionErrorKind.Denied : ActionErrorKind.Failed, ex.Message) with { Duration = sw.Elapsed };
        }
    }

    private bool Allowed(string path, FileAccessKind kind, TaskSecurityContext security, out ActionResult? denied)
    {
        var decision = security.FileAccess.Evaluate(path, kind);
        // NeedsApproval was already handled by the permission manager (the user approved) → allowed.
        denied = decision.Verdict == FileAccessVerdict.Denied ? ActionResult.Fail(ActionErrorKind.Denied, decision.Reason) : null;
        return denied is null;
    }

    public async Task<(FileContent? Content, string Text, string? Error)> ReadForModelAsync(string rawPath, TaskSecurityContext security, long maxReadBytes, int maxChars, IReadOnlyCollection<string> relevanceHints, CancellationToken cancellationToken)
    {
        var path = ResolvePath(rawPath);
        if (!Allowed(path, FileAccessKind.Read, security, out var denied)) { return (null, "", denied!.Message); }
        try
        {
            var content = await _reader.ReadAsync(path, maxReadBytes, cancellationToken).ConfigureAwait(false);
            var selection = RelevantContentSelector.Select(content.Text, maxChars, relevanceHints);
            var masked = security.Vault.Mask(selection.Text);
            security.Inspect($"Datei {content.FileName}", selection.Text);
            var header = new StringBuilder();
            header.Append("Datei: ").Append(content.FileName).Append(" (").Append(content.Kind.ToString().ToUpperInvariant());
            if (content.Pages is { } pages) { header.Append(", ").Append(pages).Append(pages == 1 ? " Seite" : " Seiten"); }
            if (content.Sheets is { Count: > 0 } sheets) { header.Append(", Blätter: ").Append(string.Join(", ", sheets)); }
            header.Append(')');
            if (selection.Reduced) { header.Append($" – Auszug: nur aufgabenrelevante Teile ({selection.Text.Length} von {selection.OriginalChars} Zeichen)"); }
            if (content.Warning is not null) { header.Append("\nHinweis: ").Append(content.Warning); }
            return (content, security.Untrusted.Wrap($"file:{content.FileName}", header + "\n" + masked), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return (null, "", ex.Message);
        }
        catch (Exception ex) when (ex.GetType().Namespace?.StartsWith("UglyToad", StringComparison.Ordinal) == true ||
                                   ex.GetType().Namespace?.StartsWith("DocumentFormat", StringComparison.Ordinal) == true ||
                                   ex is System.IO.InvalidDataException or FormatException)
        {
            return (null, "", $"Die Datei konnte nicht gelesen werden: {ex.Message}");
        }
    }

    private async Task<ActionResult> ReadAsync(AgentAction action, TaskSecurityContext security, long maxReadBytes, int maxChars, IReadOnlyCollection<string> hints, CancellationToken cancellationToken)
    {
        var (content, text, error) = await ReadForModelAsync(action.Path ?? "", security, maxReadBytes, maxChars, hints, cancellationToken).ConfigureAwait(false);
        return content is null
            ? ActionResult.Fail(ActionErrorKind.Failed, error ?? "Datei konnte nicht gelesen werden.")
            : new ActionResult { Success = true, Message = $"{content.FileName} gelesen", Data = text, Strategy = "local-reader" };
    }

    private ActionResult Search(AgentAction action, TaskSecurityContext security, CancellationToken cancellationToken)
    {
        var folder = ResolvePath(string.IsNullOrWhiteSpace(action.Path) ? "Documents" : action.Path);
        if (!Allowed(folder, FileAccessKind.Read, security, out var denied)) { return denied!; }
        if (!Directory.Exists(folder)) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"Ordner „{folder}“ existiert nicht."); }

        var query = (action.Value ?? action.Option ?? "*").Trim();
        var isPattern = query.Contains('*') || query.Contains('?');
        var words = isPattern ? [] : query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
        var matches = new List<FileInfo>();
        var sw = Stopwatch.StartNew();
        var scanned = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles(isPattern ? query : "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scanned > 20_000 || sw.Elapsed > TimeSpan.FromSeconds(4)) { break; }
            if (!isPattern && !words.All(w => file.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) { continue; }
            if (security.FileAccess.Evaluate(file.FullName, FileAccessKind.Read).Verdict == FileAccessVerdict.Denied) { continue; }
            matches.Add(file);
            if (matches.Count >= 200) { break; }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Suche nach „{query}“ in {folder}: {matches.Count} Treffer");
        foreach (var f in matches.OrderByDescending(f => f.LastWriteTime).Take(50))
        {
            sb.AppendLine($"- {f.FullName} ({FormatSize(f.Length)}, geändert {f.LastWriteTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)})");
        }
        return new ActionResult { Success = true, Message = $"{matches.Count} Dateien gefunden", Data = security.Untrusted.Wrap("file-search", sb.ToString()), Strategy = "filesystem" };
    }

    private ActionResult List(AgentAction action, TaskSecurityContext security)
    {
        var folder = ResolvePath(string.IsNullOrWhiteSpace(action.Path) ? "Desktop" : action.Path);
        if (!Allowed(folder, FileAccessKind.Read, security, out var denied)) { return denied!; }
        if (!Directory.Exists(folder)) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"Ordner „{folder}“ existiert nicht."); }
        var dir = new DirectoryInfo(folder);
        var sb = new StringBuilder();
        sb.AppendLine($"Inhalt von {folder}:");
        foreach (var d in dir.EnumerateDirectories().Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden)).Take(100))
        {
            sb.AppendLine($"[Ordner] {d.Name}");
        }
        foreach (var f in dir.EnumerateFiles().Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden)).OrderByDescending(f => f.LastWriteTime).Take(150))
        {
            sb.AppendLine($"{f.Name} ({FormatSize(f.Length)}, geändert {f.LastWriteTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)})");
        }
        return new ActionResult { Success = true, Message = "Ordner gelesen", Data = security.Untrusted.Wrap($"folder:{dir.Name}", sb.ToString()), Strategy = "filesystem" };
    }

    private ActionResult CreateFolder(AgentAction action, TaskSecurityContext security)
    {
        var path = ResolvePath(action.Path);
        if (!Allowed(path, FileAccessKind.Write, security, out var denied)) { return denied!; }
        if (Directory.Exists(path)) { return ActionResult.Ok($"Ordner „{Path.GetFileName(path)}“ existiert bereits."); }
        Directory.CreateDirectory(path);
        return ActionResult.Ok($"Ordner „{Path.GetFileName(path)}“ erstellt.", "filesystem");
    }

    private ActionResult Rename(AgentAction action, TaskSecurityContext security)
    {
        var source = ResolvePath(action.Path);
        var newName = (action.Destination ?? action.Value ?? "").Trim();
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 && !Path.IsPathRooted(newName))
        {
            return ActionResult.Fail(ActionErrorKind.InvalidArguments, "Ungültiger neuer Name.");
        }
        var target = Path.IsPathRooted(newName) ? FileAccessPolicy.NormalizePath(newName) : Path.Combine(Path.GetDirectoryName(source) ?? "", newName);
        if (!Allowed(source, FileAccessKind.Write, security, out var d1)) { return d1!; }
        if (!Allowed(target, FileAccessKind.Write, security, out var d2)) { return d2!; }
        if (File.Exists(source)) { File.Move(source, target, overwrite: false); }
        else if (Directory.Exists(source)) { Directory.Move(source, target); }
        else { return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"„{source}“ existiert nicht."); }
        return ActionResult.Ok($"Umbenannt in „{Path.GetFileName(target)}“.", "filesystem");
    }

    private ActionResult MoveOrCopy(AgentAction action, TaskSecurityContext security, bool move)
    {
        var source = ResolvePath(action.Path);
        var destination = ResolvePath(action.Destination);
        if (destination.Length == 0) { return ActionResult.Fail(ActionErrorKind.InvalidArguments, "Kein Ziel angegeben."); }
        if (!Allowed(source, move ? FileAccessKind.Write : FileAccessKind.Read, security, out var d1)) { return d1!; }
        if (Directory.Exists(destination)) { destination = Path.Combine(destination, Path.GetFileName(source)); }
        if (!Allowed(destination, FileAccessKind.Write, security, out var d2)) { return d2!; }

        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (move) { File.Move(source, destination, overwrite: true); }
            else { File.Copy(source, destination, overwrite: true); }
        }
        else if (Directory.Exists(source))
        {
            if (move) { Directory.Move(source, destination); }
            else { CopyDirectory(source, destination); }
        }
        else
        {
            return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"„{source}“ existiert nicht.");
        }
        return ActionResult.Ok($"{(move ? "Verschoben" : "Kopiert")} nach „{destination}“.", "filesystem");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private ActionResult Delete(AgentAction action, TaskSecurityContext security)
    {
        var path = ResolvePath(action.Path);
        if (!Allowed(path, FileAccessKind.Write, security, out var denied)) { return denied!; }
        if (!File.Exists(path) && !Directory.Exists(path)) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"„{path}“ existiert nicht."); }
        if (_recycleBin is null || !_recycleBin.MoveToRecycleBin(path))
        {
            // Never delete permanently: without recycle bin the action is refused.
            return ActionResult.Fail(ActionErrorKind.NotSupported, "Löschen ist nur über den Papierkorb erlaubt, der hier nicht verfügbar ist.");
        }
        return ActionResult.Ok($"„{Path.GetFileName(path)}“ in den Papierkorb verschoben.", "recycle-bin");
    }

    private async Task<ActionResult> WriteAsync(AgentAction action, TaskSecurityContext security, CancellationToken cancellationToken)
    {
        var path = ResolvePath(action.Path);
        if (!Allowed(path, FileAccessKind.Write, security, out var denied)) { return denied!; }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".js" or ".lnk" or ".reg" or ".msi" or ".scr" or ".hta")
        {
            return ActionResult.Fail(ActionErrorKind.Denied, "Kairo schreibt keine ausführbaren Dateien oder Skripte.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = security.Vault.Unmask(action.Value ?? "");
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        return ActionResult.Ok($"„{Path.GetFileName(path)}“ gespeichert.", "filesystem");
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
