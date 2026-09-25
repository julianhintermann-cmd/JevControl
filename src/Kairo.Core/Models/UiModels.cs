using System.Text.Json.Serialization;

namespace Kairo.Core.Models;

/// <summary>Rectangle in physical screen pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public static readonly ScreenRect Empty = new(0, 0, 0, 0);

    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;
    [JsonIgnore] public (int X, int Y) Center => (X + Width / 2, Y + Height / 2);

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public ScreenRect Intersect(ScreenRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? Empty : new ScreenRect(left, top, right - left, bottom - top);
    }

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>Normalized role of a UI element, independent of the perception source.</summary>
public enum ElementRole
{
    Unknown,
    Button,
    Edit,
    Document,
    Text,
    CheckBox,
    RadioButton,
    ComboBox,
    List,
    ListItem,
    Link,
    Menu,
    MenuItem,
    Tab,
    TabItem,
    Tree,
    TreeItem,
    Slider,
    Spinner,
    Table,
    DataItem,
    Group,
    Pane,
    Window,
    ToolBar,
    Image,
    Header,
    ScrollBar,
    SplitButton,
    FileInput,
}

/// <summary>Interactions an element supports.</summary>
[Flags]
public enum ElementCapabilities
{
    None = 0,
    SetValue = 1,
    Invoke = 2,
    Toggle = 4,
    ExpandCollapse = 8,
    Select = 16,
    Scroll = 32,
    Focus = 64,
    RangeValue = 128,
    ScrollIntoView = 256,
}

/// <summary>Where a snapshot came from.</summary>
public enum PerceptionSource
{
    UiAutomation,
    BrowserDom,
    Vision,
}

/// <summary>Top-level window description.</summary>
public sealed record WindowInfo
{
    public required nint Handle { get; init; }
    public required string Title { get; init; }
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
    public string? ExecutablePath { get; init; }
    public string ClassName { get; init; } = "";
    public ScreenRect Bounds { get; init; }
    public bool IsMinimized { get; init; }

    /// <summary>Chromium based browser (Chrome, Edge, Brave, ...).</summary>
    public bool IsChromiumBrowser => BrowserKind is BrowserKind.Chrome or BrowserKind.Edge or BrowserKind.OtherChromium;

    public BrowserKind BrowserKind => ProcessName.ToLowerInvariant() switch
    {
        "chrome" => BrowserKind.Chrome,
        "msedge" => BrowserKind.Edge,
        "brave" or "vivaldi" or "opera" or "chromium" => BrowserKind.OtherChromium,
        "firefox" => BrowserKind.Firefox,
        _ => BrowserKind.None,
    };

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProcessName : $"{Title} ({ProcessName})";
}

public enum BrowserKind
{
    None,
    Chrome,
    Edge,
    OtherChromium,
    Firefox,
}

/// <summary>A single perceivable UI element. Ids are only unique within one snapshot.</summary>
public sealed record UiElement
{
    /// <summary>Short numeric id used in prompts and Jev criteria ("[12]").</summary>
    public required int Id { get; init; }
    public required ElementRole Role { get; init; }
    public string Name { get; init; } = "";
    public string? Value { get; init; }
    public string? Placeholder { get; init; }
    public string? HelpText { get; init; }
    /// <summary>Surrounding group/section/fieldset label, if known.</summary>
    public string? Section { get; init; }
    /// <summary>HTML autocomplete/type hint (e.g. "email", "tel", "given-name").</summary>
    public string? InputHint { get; init; }
    public ElementCapabilities Capabilities { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsFocused { get; init; }
    public bool IsOffscreen { get; init; }
    public bool IsPassword { get; init; }
    public bool IsRequired { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsMultiline { get; init; }
    public bool? IsChecked { get; init; }
    public bool? IsExpanded { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public ScreenRect Bounds { get; init; }
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public string? Url { get; init; }
    /// <summary>Opaque provider-specific locator used to find the element again (runtime id, DOM kid, ...).</summary>
    public required string Locator { get; init; }

    public bool Has(ElementCapabilities capability) => (Capabilities & capability) == capability;

    public bool IsEditable => IsEnabled && !IsReadOnly && Has(ElementCapabilities.SetValue);

    public bool IsClickable => IsEnabled && (Has(ElementCapabilities.Invoke) || Has(ElementCapabilities.Toggle) ||
                                              Has(ElementCapabilities.Select) || Has(ElementCapabilities.ExpandCollapse) ||
                                              Role is ElementRole.Button or ElementRole.Link or ElementRole.MenuItem or
                                                  ElementRole.TabItem or ElementRole.ListItem or ElementRole.CheckBox or
                                                  ElementRole.RadioButton or ElementRole.SplitButton or ElementRole.TreeItem);

    /// <summary>Best human readable label (name, placeholder, help text, automation id).</summary>
    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) { return Name.Trim(); }
            if (!string.IsNullOrWhiteSpace(Placeholder)) { return Placeholder.Trim(); }
            if (!string.IsNullOrWhiteSpace(HelpText)) { return HelpText.Trim(); }
            if (!string.IsNullOrWhiteSpace(AutomationId)) { return AutomationId.Trim(); }
            return "";
        }
    }
}

/// <summary>Compact, structured representation of what is visible in a window.</summary>
public sealed record UiSnapshot
{
    public required string SnapshotId { get; init; }
    public required PerceptionSource Source { get; init; }
    public required WindowInfo Window { get; init; }
    public string? Url { get; init; }
    public string? PageTitle { get; init; }
    /// <summary>Browser tab id when <see cref="Source"/> is <see cref="PerceptionSource.BrowserDom"/>.</summary>
    public int? TabId { get; init; }
    public required IReadOnlyList<UiElement> Elements { get; init; }
    /// <summary>Visible static text blocks (headings, labels without controls, error messages).</summary>
    public IReadOnlyList<string> TextBlocks { get; init; } = [];
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Truncated { get; init; }
    public TimeSpan CaptureDuration { get; init; }
    /// <summary>Screenshot scale factor (vision snapshots only): image pixel * scale = screen pixel.</summary>
    public double ImageScale { get; init; } = 1.0;

    public UiElement? Find(int id)
    {
        foreach (var e in Elements)
        {
            if (e.Id == id) { return e; }
        }
        return null;
    }

    public UiElement? FindByLocator(string locator)
    {
        foreach (var e in Elements)
        {
            if (e.Locator == locator) { return e; }
        }
        return null;
    }

    public UiSnapshot WithElementUpdated(int id, Func<UiElement, UiElement> update)
    {
        var list = new List<UiElement>(Elements.Count);
        foreach (var e in Elements)
        {
            list.Add(e.Id == id ? update(e) : e);
        }
        return this with { Elements = list };
    }
}
