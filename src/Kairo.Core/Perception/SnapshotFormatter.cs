using System.Globalization;
using System.Text;
using Kairo.Core.Models;
using Kairo.Core.Security;

namespace Kairo.Core.Perception;

/// <summary>
/// Turns a snapshot into the compact line format used in prompts:
/// <c>[12] edit "E-Mail" value="" hint=email required (Kontaktdaten)</c>.
/// Values are masked through the task's secret vault; password values are never included.
/// </summary>
public static class SnapshotFormatter
{
    public static string RoleName(ElementRole role) => role switch
    {
        ElementRole.Edit => "edit",
        ElementRole.Document => "textarea",
        ElementRole.Button => "button",
        ElementRole.SplitButton => "button",
        ElementRole.CheckBox => "checkbox",
        ElementRole.RadioButton => "radio",
        ElementRole.ComboBox => "combobox",
        ElementRole.List => "list",
        ElementRole.ListItem => "listitem",
        ElementRole.Link => "link",
        ElementRole.Menu => "menu",
        ElementRole.MenuItem => "menuitem",
        ElementRole.Tab => "tablist",
        ElementRole.TabItem => "tab",
        ElementRole.Tree => "tree",
        ElementRole.TreeItem => "treeitem",
        ElementRole.Slider => "slider",
        ElementRole.Spinner => "spinner",
        ElementRole.Table => "table",
        ElementRole.DataItem => "cell",
        ElementRole.Text => "text",
        ElementRole.Header => "header",
        ElementRole.Image => "image",
        ElementRole.FileInput => "file",
        _ => role.ToString().ToLowerInvariant(),
    };

    public static string FormatElement(UiElement e, SecretVault? vault = null, int maxValueChars = 80)
    {
        var sb = new StringBuilder(96);
        sb.Append('[').Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append("] ").Append(RoleName(e.Role));
        var label = e.DisplayLabel;
        sb.Append(" \"").Append(Clip(label, 70)).Append('"');

        if (e.IsPassword)
        {
            sb.Append(" password").Append(string.IsNullOrEmpty(e.Value) ? " (leer)" : " (gefüllt)");
        }
        else if (e.Value is not null && (e.Role is ElementRole.Edit or ElementRole.Document or ElementRole.ComboBox or ElementRole.Spinner or ElementRole.Slider || e.Has(ElementCapabilities.SetValue)))
        {
            var value = vault?.Mask(e.Value) ?? e.Value;
            sb.Append(" value=\"").Append(Clip(value, maxValueChars)).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(e.Placeholder) && !string.Equals(e.Placeholder, label, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" placeholder=\"").Append(Clip(e.Placeholder, 40)).Append('"');
        }
        if (!string.IsNullOrWhiteSpace(e.InputHint)) { sb.Append(" hint=").Append(e.InputHint); }
        if (e.IsChecked is { } c) { sb.Append(c ? " checked" : " unchecked"); }
        if (e.IsExpanded is true) { sb.Append(" expanded"); }
        if (e.IsRequired) { sb.Append(" required"); }
        if (!e.IsEnabled) { sb.Append(" disabled"); }
        if (e.IsReadOnly) { sb.Append(" readonly"); }
        if (e.IsFocused) { sb.Append(" focused"); }
        if (e.IsOffscreen) { sb.Append(" offscreen"); }
        if (e.Options is { Count: > 0 } options)
        {
            sb.Append(" options=[").Append(string.Join(", ", options.Take(15).Select(o => Clip(o, 30))));
            if (options.Count > 15) { sb.Append(", …+").Append(options.Count - 15); }
            sb.Append(']');
        }
        if (!string.IsNullOrWhiteSpace(e.Section) && !string.Equals(e.Section, label, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" (").Append(Clip(e.Section, 40)).Append(')');
        }
        return sb.ToString();
    }

    /// <summary>Full compact representation for the planner (wrapped as untrusted data by the caller).</summary>
    public static string Format(UiSnapshot snapshot, SecretVault? vault = null, int maxElements = 250, int maxTexts = 40)
    {
        var sb = new StringBuilder();
        sb.Append("Fenster: \"").Append(Clip(snapshot.Window.Title, 120)).Append("\" | Anwendung: ").Append(snapshot.Window.ProcessName);
        sb.Append(" | Quelle: ").Append(snapshot.Source switch
        {
            PerceptionSource.BrowserDom => "Browser-DOM",
            PerceptionSource.Vision => "Screenshot-Analyse",
            _ => "UI Automation",
        });
        if (!string.IsNullOrEmpty(snapshot.Url)) { sb.Append(" | URL: ").Append(Clip(snapshot.Url, 160)); }
        sb.AppendLine();

        if (snapshot.TextBlocks.Count > 0)
        {
            sb.AppendLine("Sichtbare Texte:");
            foreach (var t in snapshot.TextBlocks.Take(maxTexts))
            {
                sb.Append("  • ").AppendLine(Clip(vault?.Mask(t) ?? t, 160));
            }
        }

        sb.AppendLine("Bedienelemente:");
        var count = 0;
        foreach (var e in snapshot.Elements)
        {
            if (++count > maxElements) { sb.AppendLine($"  … {snapshot.Elements.Count - maxElements} weitere Elemente ausgeblendet"); break; }
            sb.Append("  ").AppendLine(FormatElement(e, vault));
        }
        if (snapshot.Elements.Count == 0) { sb.AppendLine("  (keine bedienbaren Elemente erkannt)"); }
        if (snapshot.Truncated) { sb.AppendLine("  (Liste gekürzt – weitere Elemente evtl. durch Scrollen erreichbar)"); }
        return sb.ToString();
    }

    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var single = text.Replace('\n', ' ').Replace('\r', ' ').Replace("\"", "'");
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }
}
