namespace Kairo.Core.Security;

public enum FileAccessKind
{
    Read,
    Write,
}

public enum FileAccessVerdict
{
    /// <summary>Inside the allowed roots.</summary>
    Allowed,
    /// <summary>Outside the allowed roots – possible after explicit user approval.</summary>
    NeedsApproval,
    /// <summary>Never allowed (system folders, credential stores, Kairo's own secrets).</summary>
    Denied,
}

public sealed record FileAccessDecision(FileAccessVerdict Verdict, string NormalizedPath, string Reason);

/// <summary>
/// Traceable boundaries for local file access. Paths are normalized (GetFullPath) and links are
/// resolved before they are compared, so "..\" tricks and junctions cannot escape the roots.
/// </summary>
public sealed class FileAccessPolicy
{
    private readonly List<string> _readRoots;
    private readonly List<string> _writeRoots;
    private readonly List<string> _deniedPrefixes;
    private readonly HashSet<string> _sessionGrants = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] DeniedSegments =
    [
        @"\.ssh\", @"\.gnupg\", @"\.aws\", @"\.azure\", @"\.kube\",
        @"\AppData\Roaming\Microsoft\Credentials\", @"\AppData\Local\Microsoft\Credentials\",
        @"\AppData\Roaming\Microsoft\Protect\", @"\AppData\Roaming\Microsoft\Crypto\",
        @"\AppData\Local\Google\Chrome\User Data\", @"\AppData\Local\Microsoft\Edge\User Data\",
        @"\AppData\Roaming\Mozilla\Firefox\Profiles\", @"\AppData\Local\BraveSoftware\",
    ];

    private static readonly string[] DeniedExtensions = [".kdbx", ".kdb", ".pfx", ".p12", ".pem", ".key", ".ppk", ".wallet"];

    public FileAccessPolicy(IEnumerable<string> readRoots, IEnumerable<string> writeRoots, IEnumerable<string> deniedPrefixes)
    {
        _readRoots = readRoots.Select(NormalizeDirectory).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _writeRoots = writeRoots.Select(NormalizeDirectory).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _deniedPrefixes = deniedPrefixes.Select(NormalizeDirectory).Where(p => p.Length > 0).ToList();
    }

    public IReadOnlyList<string> ReadRoots => _readRoots;
    public IReadOnlyList<string> WriteRoots => _writeRoots;

    /// <summary>Default policy for the current user: standard library folders; system and secret folders denied.</summary>
    public static FileAccessPolicy CreateDefault(IEnumerable<string>? extraReadRoots, IEnumerable<string>? extraWriteRoots, string kairoDataDirectory)
    {
        var profileFolders = StandardUserFolders().ToList();
        var read = profileFolders.Concat(extraReadRoots ?? []).ToList();
        var write = profileFolders.Concat(extraWriteRoots ?? []).ToList();

        var denied = new List<string> { kairoDataDirectory };
        void AddDenied(Environment.SpecialFolder folder)
        {
            var p = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrEmpty(p)) { denied.Add(p); }
        }
        AddDenied(Environment.SpecialFolder.Windows);
        AddDenied(Environment.SpecialFolder.System);
        AddDenied(Environment.SpecialFolder.ProgramFiles);
        AddDenied(Environment.SpecialFolder.ProgramFilesX86);
        AddDenied(Environment.SpecialFolder.CommonApplicationData);
        return new FileAccessPolicy(read, write, denied);
    }

    public static IEnumerable<string> StandardUserFolders()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyVideos,
        };
        foreach (var f in folders)
        {
            var p = Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
            if (!string.IsNullOrEmpty(p)) { yield return p; }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrEmpty(profile))
        {
            yield return Path.Combine(profile, "Downloads");
            var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrEmpty(oneDrive)) { yield return oneDrive; }
        }
    }

    /// <summary>Grants access to a path the user explicitly named in the instruction (for this task).</summary>
    public void GrantForSession(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length > 0) { _sessionGrants.Add(normalized); }
    }

    public void ClearSessionGrants() => _sessionGrants.Clear();

    public FileAccessDecision Evaluate(string path, FileAccessKind kind)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0)
        {
            return new(FileAccessVerdict.Denied, path, "Ungültiger Pfad.");
        }

        var resolved = ResolveLinks(normalized);
        foreach (var candidate in new[] { normalized, resolved }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsDenied(candidate, kind, out var reason))
            {
                return new(FileAccessVerdict.Denied, normalized, reason);
            }
        }

        var roots = kind == FileAccessKind.Read ? _readRoots : _writeRoots;
        if (roots.Any(r => IsUnder(resolved, r)) && roots.Any(r => IsUnder(normalized, r)))
        {
            return new(FileAccessVerdict.Allowed, normalized, "Im freigegebenen Bereich.");
        }

        if (_sessionGrants.Any(g => string.Equals(g, normalized, StringComparison.OrdinalIgnoreCase) || IsUnder(normalized, g)))
        {
            return new(FileAccessVerdict.Allowed, normalized, "Vom Benutzer in der Anweisung genannt.");
        }

        return new(FileAccessVerdict.NeedsApproval, normalized, "Pfad liegt außerhalb der freigegebenen Ordner.");
    }

    private bool IsDenied(string path, FileAccessKind kind, out string reason)
    {
        var backslashed = path.Replace('/', '\\');
        var withSlash = backslashed.EndsWith('\\') ? backslashed : backslashed + "\\";
        foreach (var segment in DeniedSegments)
        {
            if (withSlash.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Geschützter Ordner mit Zugangsdaten oder Browserprofilen.";
                return true;
            }
        }

        var ext = Path.GetExtension(path);
        if (DeniedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            reason = "Schlüssel- oder Passwortdatenbank-Dateien sind gesperrt.";
            return true;
        }

        foreach (var prefix in _deniedPrefixes)
        {
            // Program Files etc. may be read (e.g. to open a document there) but never written.
            if (IsUnder(path, prefix) && (kind == FileAccessKind.Write || prefix.Contains("Kairo", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "System- oder Programmordner sind für Kairo gesperrt.";
                return true;
            }
        }

        reason = "";
        return false;
    }

    internal static bool IsUnder(string path, string root)
    {
        if (root.Length == 0) { return false; }
        var r = root.TrimEnd('\\', '/');
        return path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return ""; }
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (expanded.StartsWith('~'))
            {
                expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify) + expanded[1..];
            }
            var full = Path.GetFullPath(expanded);
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    private static string NormalizeDirectory(string path) => NormalizePath(path);

    /// <summary>Resolves symbolic links / junctions of the path and its existing parents.</summary>
    private static string ResolveLinks(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) { return NormalizePath(target.FullName); }
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && parent != path)
            {
                var resolvedParent = ResolveLinks(parent);
                if (!string.Equals(resolvedParent, parent, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(resolvedParent, Path.GetFileName(path));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        return path;
    }
}
