using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kairo.Core.Security;

/// <summary>
/// Wraps content from web pages, documents, UI texts etc. so the planner treats it strictly as data.
/// A random boundary per task prevents content from "closing" the block and injecting instructions.
/// </summary>
public sealed partial class UntrustedContent
{
    public UntrustedContent()
    {
        Boundary = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
    }

    /// <summary>Random per-task boundary token.</summary>
    public string Boundary { get; }

    public string Wrap(string source, string content)
    {
        var safeSource = SanitizeAttribute(source);
        var cleaned = Neutralize(content);
        var sb = new StringBuilder(cleaned.Length + 120);
        sb.Append("<untrusted_data boundary=\"").Append(Boundary).Append("\" source=\"").Append(safeSource).AppendLine("\">");
        sb.AppendLine(cleaned);
        sb.Append("</untrusted_data boundary=\"").Append(Boundary).Append("\">");
        return sb.ToString();
    }

    /// <summary>Removes anything that could terminate the data block or imitate chat role markers.</summary>
    public static string Neutralize(string content)
    {
        if (string.IsNullOrEmpty(content)) { return ""; }
        var text = UntrustedTag().Replace(content, "[tag entfernt]");
        text = RoleMarker().Replace(text, "[marker entfernt]");
        return text;
    }

    private static string SanitizeAttribute(string value) =>
        new string(value.Where(c => !char.IsControl(c) && c != '"' && c != '<' && c != '>').ToArray());

    [GeneratedRegex(@"</?\s*untrusted_data[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UntrustedTag();

    [GeneratedRegex(@"<\|(im_start|im_end|system|assistant|user|endoftext)\|>|\[/?INST\]|<</?SYS>>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleMarker();
}

public sealed record InjectionFinding(string Source, string Pattern, string Excerpt);

/// <summary>
/// Heuristic detector for prompt injection in untrusted content (German and English).
/// Findings do not block the task; they tighten the permission policy for the rest of it.
/// </summary>
public static partial class InjectionDetector
{
    private static readonly (Regex Regex, string Name, bool Strong)[] Patterns =
    [
        (IgnoreInstructionsEn(), "ignore-instructions", true),
        (IgnoreInstructionsDe(), "ignoriere-anweisungen", true),
        (NewInstructions(), "new-instructions", false),
        (RoleOverride(), "role-override", true),
        (SystemPrompt(), "system-prompt", false),
        (HideFromUser(), "hide-from-user", true),
        (Exfiltrate(), "exfiltration", true),
        (AiAddressing(), "ai-addressing", false),
        (ChatMarkers(), "chat-markers", true),
    ];

    public static IReadOnlyList<InjectionFinding> Scan(string source, string? content)
    {
        var findings = new List<InjectionFinding>();
        if (string.IsNullOrWhiteSpace(content)) { return findings; }
        var weak = 0;
        var weakFindings = new List<InjectionFinding>();
        foreach (var (regex, name, strong) in Patterns)
        {
            var match = regex.Match(content);
            if (!match.Success) { continue; }
            var start = Math.Max(0, match.Index - 30);
            var length = Math.Min(content.Length - start, match.Length + 60);
            var finding = new InjectionFinding(source, name, content.Substring(start, length).ReplaceLineEndings(" "));
            if (strong) { findings.Add(finding); }
            else { weak++; weakFindings.Add(finding); }
        }
        if (weak >= 2) { findings.AddRange(weakFindings); }
        return findings;
    }

    [GeneratedRegex(@"\b(ignore|disregard|forget|override)\b.{0,30}\b(previous|prior|above|earlier|all|your|the)\b.{0,20}\b(instructions?|prompts?|rules|guidelines|directions)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IgnoreInstructionsEn();

    [GeneratedRegex(@"\b(ignoriere|vergiss|missachte|übergehe)\b.{0,30}\b(vorherigen?|bisherigen?|obigen?|alle|deine|die)\b.{0,20}\b(anweisungen|instruktionen|befehle|regeln|vorgaben)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IgnoreInstructionsDe();

    [GeneratedRegex(@"\b(new|updated|neue|aktualisierte)\s+(instructions?|anweisungen|aufgabe|task)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewInstructions();

    [GeneratedRegex(@"\b(you are now|from now on you|act as|du bist (jetzt|ab sofort)|ab jetzt bist du|verhalte dich als)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RoleOverride();

    [GeneratedRegex(@"\b(system\s*prompt|systemprompt|developer message|entwicklermodus|jailbreak)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SystemPrompt();

    [GeneratedRegex(@"\b(do not|don't|never)\s+(tell|inform|show|mention)\s+(the\s+)?user\b|\b(sag|erzähl|zeig)e?\s+(dem|der)\s+(benutzer|nutzer|user)\w*\s+nicht", RegexOptions.IgnoreCase)]
    private static partial Regex HideFromUser();

    [GeneratedRegex(@"\b(send|upload|post|forward|email|mail|sende|schicke|übertrage|lade)\b.{0,40}\b(password|passwort|api[\s_-]?key|credentials|zugangsdaten|token|cookies?|secrets?)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Exfiltrate();

    [GeneratedRegex(@"\b(ai assistant|language model|llm|ki-assistent|sprachmodell|dear (ai|assistant|agent)|liebe[rs]? (ki|assistent|agent))\b", RegexOptions.IgnoreCase)]
    private static partial Regex AiAddressing();

    [GeneratedRegex(@"<\|(im_start|system)\|>|\[INST\]|^\s*(system|assistant)\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ChatMarkers();
}
