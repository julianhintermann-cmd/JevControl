using System.Diagnostics;
using Interop.UIAutomationClient;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;
using P = Interop.UIAutomationClient.UIA_PropertyIds;
using CT = Interop.UIAutomationClient.UIA_ControlTypeIds;

namespace Kairo.Windows.Automation;

/// <summary>
/// Stage 1 of the perception: reads the accessibility tree of the window via Windows UI Automation.
/// The complete control view is fetched in ONE cross-process call (cache request with TreeScope_Subtree),
/// then turned into a compact element list (role, label, value, state, bounds, supported actions).
/// </summary>
public sealed class UiaPerceptionProvider : IPerceptionProvider
{
    private const int MaxRawNodes = 4000;
    private const int MaxDepth = 60;

    private static readonly int[] CachedProperties =
    [
        P.UIA_RuntimeIdPropertyId, P.UIA_NamePropertyId, P.UIA_ControlTypePropertyId, P.UIA_AutomationIdPropertyId,
        P.UIA_ClassNamePropertyId, P.UIA_BoundingRectanglePropertyId, P.UIA_IsEnabledPropertyId, P.UIA_IsOffscreenPropertyId,
        P.UIA_IsPasswordPropertyId, P.UIA_HasKeyboardFocusPropertyId, P.UIA_IsKeyboardFocusablePropertyId, P.UIA_HelpTextPropertyId,
        P.UIA_IsRequiredForFormPropertyId, P.UIA_FrameworkIdPropertyId, P.UIA_AriaRolePropertyId, P.UIA_AriaPropertiesPropertyId,
        P.UIA_LabeledByPropertyId, P.UIA_FullDescriptionPropertyId, P.UIA_NativeWindowHandlePropertyId,
        P.UIA_IsValuePatternAvailablePropertyId, P.UIA_IsInvokePatternAvailablePropertyId, P.UIA_IsTogglePatternAvailablePropertyId,
        P.UIA_IsExpandCollapsePatternAvailablePropertyId, P.UIA_IsSelectionItemPatternAvailablePropertyId,
        P.UIA_IsScrollPatternAvailablePropertyId, P.UIA_IsRangeValuePatternAvailablePropertyId, P.UIA_IsTextPatternAvailablePropertyId,
        P.UIA_IsScrollItemPatternAvailablePropertyId, P.UIA_IsSelectionPatternAvailablePropertyId,
        P.UIA_ValueValuePropertyId, P.UIA_ValueIsReadOnlyPropertyId, P.UIA_ToggleToggleStatePropertyId,
        P.UIA_ExpandCollapseExpandCollapseStatePropertyId, P.UIA_SelectionItemIsSelectedPropertyId,
        P.UIA_RangeValueValuePropertyId,
    ];

    private readonly UiaCore _core;
    private readonly KairoLogger _log;

    public UiaPerceptionProvider(UiaCore core, KairoLogger log)
    {
        _core = core;
        _log = log;
    }

    public PerceptionSource Source => PerceptionSource.UiAutomation;

    public int Priority => 10;

    public bool CanHandle(WindowInfo window) => window.Handle != 0;

    public Task<UiSnapshot?> CaptureAsync(WindowInfo window, PerceptionRequest request, CancellationToken cancellationToken) =>
        UiaCore.RunAsync(() => Capture(window, request, cancellationToken), cancellationToken);

    private IUIAutomationCacheRequest CreateCacheRequest()
    {
        var automation = _core.Automation;
        var request = automation.CreateCacheRequest();
        foreach (var id in CachedProperties) { request.AddProperty(id); }
        request.TreeScope = TreeScope.TreeScope_Subtree;
        request.TreeFilter = automation.ControlViewCondition;
        request.AutomationElementMode = AutomationElementMode.AutomationElementMode_Full;
        return request;
    }

    private UiSnapshot? Capture(WindowInfo window, PerceptionRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var cacheRequest = CreateCacheRequest();
        var root = _core.Automation.ElementFromHandleBuildCache(window.Handle, cacheRequest);
        if (root is null) { return null; }

        var walk = Walk(root, window, cancellationToken);

        // Chromium builds its web accessibility tree lazily on the first UIA request.
        if (window.IsChromiumBrowser && walk.WebNodes < 3)
        {
            Thread.Sleep(350);
            root = _core.Automation.ElementFromHandleBuildCache(window.Handle, cacheRequest);
            walk = Walk(root, window, cancellationToken);
        }

        var ordered = walk.Web.Concat(walk.Native).ToList();
        var visible = ordered.Where(c => !c.Offscreen).ToList();
        var offscreen = ordered.Where(c => c.Offscreen).ToList();
        var chosen = visible.Take(request.MaxElements).ToList();
        if (chosen.Count < request.MaxElements && request.IncludeOffscreen)
        {
            chosen.AddRange(offscreen.Take(request.MaxElements - chosen.Count));
        }
        var chosenSet = chosen.ToHashSet();
        var final = ordered.Where(chosenSet.Contains).ToList();

        var elements = new List<UiElement>(final.Count);
        var id = 1;
        foreach (var c in final)
        {
            _core.Register(c.Locator, c.Element);
            elements.Add(c.ToElement(id++));
        }

        sw.Stop();
        _log.Debug("uia", $"snapshot nodes={walk.Nodes} elements={elements.Count} ms={sw.ElapsedMilliseconds}");
        return new UiSnapshot
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            Source = PerceptionSource.UiAutomation,
            Window = window,
            Url = walk.Url,
            Elements = elements,
            TextBlocks = walk.Texts.Distinct().Take(request.IncludeText ? 60 : 0).ToList(),
            Truncated = final.Count < ordered.Count || walk.Nodes >= MaxRawNodes,
        };
    }

    private sealed class Candidate
    {
        public required IUIAutomationElement Element { get; init; }
        public required string Locator { get; init; }
        public required ElementRole Role { get; init; }
        public string Name { get; set; } = "";
        public string? Value { get; init; }
        public string? HelpText { get; init; }
        public string? Section { get; init; }
        public string? AutomationId { get; init; }
        public string? ClassName { get; init; }
        public ElementCapabilities Caps { get; init; }
        public bool Enabled { get; init; }
        public bool Focused { get; init; }
        public bool Offscreen { get; init; }
        public bool Password { get; init; }
        public bool Required { get; init; }
        public bool ReadOnly { get; init; }
        public bool Multiline { get; init; }
        public bool? Checked { get; init; }
        public bool? Expanded { get; init; }
        public List<string>? Options { get; set; }
        public ScreenRect Bounds { get; init; }
        public string? InputHint { get; init; }

        public UiElement ToElement(int id) => new()
        {
            Id = id,
            Role = Role,
            Name = Name,
            Value = Password ? null : Value,
            Placeholder = HelpText,
            Section = Section,
            Capabilities = Caps,
            IsEnabled = Enabled,
            IsFocused = Focused,
            IsOffscreen = Offscreen,
            IsPassword = Password,
            IsRequired = Required,
            IsReadOnly = ReadOnly,
            IsMultiline = Multiline,
            IsChecked = Checked,
            IsExpanded = Expanded,
            Options = Options,
            Bounds = Bounds,
            AutomationId = string.IsNullOrWhiteSpace(AutomationId) ? null : AutomationId,
            ClassName = ClassName,
            InputHint = InputHint,
            Locator = Locator,
        };
    }

    private sealed class WalkResult
    {
        public List<Candidate> Web { get; } = [];
        public List<Candidate> Native { get; } = [];
        public List<string> Texts { get; } = [];
        public int Nodes { get; set; }
        public int WebNodes { get; set; }
        public string? Url { get; set; }
    }

    private WalkResult Walk(IUIAutomationElement root, WindowInfo window, CancellationToken cancellationToken)
    {
        var result = new WalkResult();
        var browserChromeCount = 0;
        Visit(root, 0, section: null, inWeb: false, lastText: null);
        return result;

        string? Visit(IUIAutomationElement element, int depth, string? section, bool inWeb, string? lastText)
        {
            if (result.Nodes >= MaxRawNodes || depth > MaxDepth) { return lastText; }
            cancellationToken.ThrowIfCancellationRequested();
            result.Nodes++;
            if (inWeb) { result.WebNodes++; }

            var controlType = SafeInt(element, P.UIA_ControlTypePropertyId);
            var name = Clean(SafeString(element, P.UIA_NamePropertyId));
            var framework = SafeString(element, P.UIA_FrameworkIdPropertyId);
            var className = SafeString(element, P.UIA_ClassNamePropertyId);

            // Web content of Chromium browsers is a Document (framework "Chrome") below the browser UI.
            var isWebDocument = window.IsChromiumBrowser && controlType == CT.UIA_DocumentControlTypeId &&
                                (string.Equals(framework, "Chrome", StringComparison.OrdinalIgnoreCase) || className.Contains("RenderWidget", StringComparison.Ordinal) || !inWeb);
            var childInWeb = inWeb || isWebDocument;

            string? childSection = section;
            switch (controlType)
            {
                case var t when t == CT.UIA_GroupControlTypeId || t == CT.UIA_TabControlTypeId:
                    if (name.Length is > 0 and < 80) { childSection = name; }
                    break;
                case var t when t == CT.UIA_TextControlTypeId || t == CT.UIA_HeaderControlTypeId || t == CT.UIA_HeaderItemControlTypeId:
                    if (name.Length > 0)
                    {
                        if (result.Texts.Count < 120) { result.Texts.Add(name.Length > 200 ? name[..200] : name); }
                        lastText = name;
                        if (SafeString(element, P.UIA_AriaRolePropertyId) is "heading" && name.Length < 80) { childSection = name; }
                    }
                    break;
            }

            var candidate = TryCreateCandidate(element, controlType, name, className, childSection, lastText, window, childInWeb);
            if (candidate is not null)
            {
                if (window.IsChromiumBrowser && !childInWeb)
                {
                    // Browser UI: keep only the address bar and a few navigation controls.
                    if (candidate.Role == ElementRole.Edit && result.Url is null) { result.Url = candidate.Value; }
                    if (browserChromeCount < 15 && candidate.Role is ElementRole.Edit or ElementRole.TabItem or ElementRole.Button)
                    {
                        browserChromeCount++;
                        result.Native.Add(candidate);
                    }
                }
                else if (childInWeb) { result.Web.Add(candidate); }
                else { result.Native.Add(candidate); }

                if (candidate.Role is ElementRole.Edit or ElementRole.ComboBox or ElementRole.CheckBox or ElementRole.RadioButton) { lastText = null; }
            }

            IUIAutomationElementArray? children = null;
            try { children = element.GetCachedChildren(); }
            catch (System.Runtime.InteropServices.COMException) { }
            if (children is null) { return lastText; }

            List<string>? optionNames = candidate?.Role is ElementRole.ComboBox or ElementRole.List ? [] : null;
            for (var i = 0; i < children.Length; i++)
            {
                var child = children.GetElement(i);
                if (optionNames is not null && SafeInt(child, P.UIA_ControlTypePropertyId) == CT.UIA_ListItemControlTypeId)
                {
                    var optionName = Clean(SafeString(child, P.UIA_NamePropertyId));
                    if (optionName.Length > 0 && optionNames.Count < 60) { optionNames.Add(optionName); }
                }
                lastText = Visit(child, depth + 1, childSection, childInWeb, lastText);
            }
            if (candidate is not null && optionNames is { Count: > 0 }) { candidate.Options = optionNames; }
            return lastText;
        }
    }

    private Candidate? TryCreateCandidate(IUIAutomationElement e, int controlType, string name, string className, string? section, string? lastText, WindowInfo window, bool inWeb)
    {
        var hasValue = SafeBool(e, P.UIA_IsValuePatternAvailablePropertyId);
        var hasInvoke = SafeBool(e, P.UIA_IsInvokePatternAvailablePropertyId);
        var hasToggle = SafeBool(e, P.UIA_IsTogglePatternAvailablePropertyId);
        var hasExpand = SafeBool(e, P.UIA_IsExpandCollapsePatternAvailablePropertyId);
        var hasSelectItem = SafeBool(e, P.UIA_IsSelectionItemPatternAvailablePropertyId);
        var hasScroll = SafeBool(e, P.UIA_IsScrollPatternAvailablePropertyId);
        var hasRange = SafeBool(e, P.UIA_IsRangeValuePatternAvailablePropertyId);
        var hasText = SafeBool(e, P.UIA_IsTextPatternAvailablePropertyId);
        var readOnly = hasValue && SafeBool(e, P.UIA_ValueIsReadOnlyPropertyId);
        var focusable = SafeBool(e, P.UIA_IsKeyboardFocusablePropertyId);

        var role = controlType switch
        {
            var t when t == CT.UIA_ButtonControlTypeId => ElementRole.Button,
            var t when t == CT.UIA_SplitButtonControlTypeId => ElementRole.SplitButton,
            var t when t == CT.UIA_EditControlTypeId => ElementRole.Edit,
            var t when t == CT.UIA_CheckBoxControlTypeId => ElementRole.CheckBox,
            var t when t == CT.UIA_RadioButtonControlTypeId => ElementRole.RadioButton,
            var t when t == CT.UIA_ComboBoxControlTypeId => ElementRole.ComboBox,
            var t when t == CT.UIA_ListItemControlTypeId => ElementRole.ListItem,
            var t when t == CT.UIA_HyperlinkControlTypeId => ElementRole.Link,
            var t when t == CT.UIA_MenuItemControlTypeId => ElementRole.MenuItem,
            var t when t == CT.UIA_TabItemControlTypeId => ElementRole.TabItem,
            var t when t == CT.UIA_TreeItemControlTypeId => ElementRole.TreeItem,
            var t when t == CT.UIA_SliderControlTypeId => ElementRole.Slider,
            var t when t == CT.UIA_SpinnerControlTypeId => ElementRole.Spinner,
            var t when t == CT.UIA_DataItemControlTypeId => ElementRole.DataItem,
            var t when t == CT.UIA_DocumentControlTypeId && (hasValue && !readOnly || hasText && focusable && !inWeb) => ElementRole.Document,
            var t when t == CT.UIA_ListControlTypeId && SafeBool(e, P.UIA_IsSelectionPatternAvailablePropertyId) => ElementRole.List,
            var t when t == CT.UIA_CustomControlTypeId || t == CT.UIA_GroupControlTypeId || t == CT.UIA_PaneControlTypeId || t == CT.UIA_ImageControlTypeId || t == CT.UIA_TextControlTypeId =>
                hasValue && !readOnly && focusable ? ElementRole.Edit :
                hasToggle ? ElementRole.CheckBox :
                hasInvoke && name.Length > 0 && t != CT.UIA_TextControlTypeId ? ElementRole.Button :
                hasInvoke && t == CT.UIA_TextControlTypeId && inWeb ? ElementRole.Link :
                ElementRole.Unknown,
            _ => ElementRole.Unknown,
        };
        if (role == ElementRole.Unknown) { return null; }

        var runtimeId = SafeValue(e, P.UIA_RuntimeIdPropertyId) as int[];
        var bounds = SafeRect(e);
        var offscreen = SafeBool(e, P.UIA_IsOffscreenPropertyId) || bounds.IsEmpty;
        var helpText = Clean(SafeString(e, P.UIA_HelpTextPropertyId));
        if (helpText.Length == 0) { helpText = Clean(SafeString(e, P.UIA_FullDescriptionPropertyId)); }
        var aria = SafeString(e, P.UIA_AriaPropertiesPropertyId);

        // Label resolution for unlabeled inputs: LabeledBy → preceding text → help text.
        if (name.Length == 0 && role is ElementRole.Edit or ElementRole.ComboBox or ElementRole.Document or ElementRole.Spinner)
        {
            if (SafeValue(e, P.UIA_LabeledByPropertyId) is IUIAutomationElement labeledBy)
            {
                try { name = Clean(labeledBy.CurrentName); }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            if (name.Length == 0 && !string.IsNullOrWhiteSpace(lastText) && lastText.Length < 80) { name = lastText.TrimEnd(':', '*', ' '); }
        }

        var caps = ElementCapabilities.None;
        if (hasValue && !readOnly) { caps |= ElementCapabilities.SetValue; }
        if (hasInvoke) { caps |= ElementCapabilities.Invoke; }
        if (hasToggle) { caps |= ElementCapabilities.Toggle; }
        if (hasExpand) { caps |= ElementCapabilities.ExpandCollapse; }
        if (hasSelectItem) { caps |= ElementCapabilities.Select; }
        if (hasScroll) { caps |= ElementCapabilities.Scroll; }
        if (hasRange) { caps |= ElementCapabilities.RangeValue; }
        if (focusable) { caps |= ElementCapabilities.Focus; }
        if (SafeBool(e, P.UIA_IsScrollItemPatternAvailablePropertyId)) { caps |= ElementCapabilities.ScrollIntoView; }
        if (role == ElementRole.Document && hasText && !hasValue) { caps |= ElementCapabilities.SetValue; } // via keyboard

        bool? isChecked = null;
        if (hasToggle)
        {
            var state = SafeInt(e, P.UIA_ToggleToggleStatePropertyId);
            isChecked = state switch { 1 => true, 0 => false, _ => null };
        }
        else if (role == ElementRole.RadioButton && hasSelectItem)
        {
            isChecked = SafeBool(e, P.UIA_SelectionItemIsSelectedPropertyId);
        }

        bool? expanded = hasExpand ? SafeInt(e, P.UIA_ExpandCollapseExpandCollapseStatePropertyId) is 1 or 2 : null;
        var value = hasValue ? SafeString(e, P.UIA_ValueValuePropertyId) : role == ElementRole.Slider && hasRange ? SafeValue(e, P.UIA_RangeValueValuePropertyId)?.ToString() : null;
        var password = SafeBool(e, P.UIA_IsPasswordPropertyId);
        var hint = aria.Contains("type=email", StringComparison.OrdinalIgnoreCase) ? "email" :
                   aria.Contains("type=tel", StringComparison.OrdinalIgnoreCase) ? "tel" :
                   aria.Contains("type=submit", StringComparison.OrdinalIgnoreCase) ? "submit" : null;

        return new Candidate
        {
            Element = e,
            Locator = UiaCore.LocatorFor(runtimeId),
            Role = role,
            Name = name,
            Value = value is { Length: > 500 } ? value[..500] : value,
            HelpText = helpText.Length == 0 || string.Equals(helpText, name, StringComparison.OrdinalIgnoreCase) ? null : helpText,
            Section = section,
            AutomationId = SafeString(e, P.UIA_AutomationIdPropertyId),
            ClassName = className,
            Caps = caps,
            Enabled = SafeBool(e, P.UIA_IsEnabledPropertyId),
            Focused = SafeBool(e, P.UIA_HasKeyboardFocusPropertyId),
            Offscreen = offscreen,
            Password = password,
            Required = SafeBool(e, P.UIA_IsRequiredForFormPropertyId) || aria.Contains("required=true", StringComparison.OrdinalIgnoreCase),
            ReadOnly = readOnly,
            Multiline = IsMultiline(e, role, className, aria, bounds, window),
            Checked = isChecked,
            Expanded = expanded,
            Bounds = bounds,
            InputHint = hint,
        };
    }

    /// <summary>
    /// Multi-line fields accept Enter as a line break; in single-line fields Enter could submit a form, so
    /// newlines are only typed as Enter when the field is known to be multi-line.
    /// </summary>
    private static bool IsMultiline(IUIAutomationElement e, ElementRole role, string className, string aria, ScreenRect bounds, WindowInfo window)
    {
        if (role == ElementRole.Document || aria.Contains("multiline=true", StringComparison.OrdinalIgnoreCase)) { return true; }
        if (role != ElementRole.Edit) { return false; }

        // Win32 / WinForms edit controls: the ES_MULTILINE window style is authoritative.
        if (SafeValue(e, P.UIA_NativeWindowHandlePropertyId) is int hwnd && hwnd != 0 &&
            className.Contains("edit", StringComparison.OrdinalIgnoreCase))
        {
            return ((long)Interop.NativeMethods.GetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_STYLE) & Interop.NativeMethods.ES_MULTILINE) != 0;
        }

        // WPF / XAML / web text boxes expose no multi-line property: a field taller than about two text lines is multi-line.
        var dpi = window.Handle != 0 ? Interop.NativeMethods.GetDpiForWindow(window.Handle) : 0;
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        return bounds.Height > 52 * scale;
    }

    public Task<IReadOnlyDictionary<string, ElementState>> ReadStatesAsync(UiSnapshot snapshot, IReadOnlyCollection<UiElement> elements, CancellationToken cancellationToken) =>
        UiaCore.RunAsync<IReadOnlyDictionary<string, ElementState>>(() =>
        {
            var result = new Dictionary<string, ElementState>();
            foreach (var e in elements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var live = _core.Resolve(e.Locator, snapshot.Window.Handle);
                if (live is null)
                {
                    result[e.Locator] = new ElementState(false);
                    continue;
                }
                try
                {
                    result[e.Locator] = ReadState(live, e);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    _core.Forget(e.Locator);
                    result[e.Locator] = new ElementState(false);
                }
            }
            return result;
        }, cancellationToken);

    internal static ElementState ReadState(IUIAutomationElement live, UiElement e)
    {
        string? value = null;
        bool? isChecked = null;
        string? selected = null;
        if (live.GetCurrentPropertyValue(P.UIA_IsValuePatternAvailablePropertyId) is true)
        {
            value = live.GetCurrentPropertyValue(P.UIA_ValueValuePropertyId) as string;
        }
        if (live.GetCurrentPropertyValue(P.UIA_IsTogglePatternAvailablePropertyId) is true)
        {
            isChecked = live.GetCurrentPropertyValue(P.UIA_ToggleToggleStatePropertyId) is int s ? s == 1 : null;
        }
        else if (e.Role == ElementRole.RadioButton && live.GetCurrentPropertyValue(P.UIA_IsSelectionItemPatternAvailablePropertyId) is true)
        {
            isChecked = live.GetCurrentPropertyValue(P.UIA_SelectionItemIsSelectedPropertyId) is true;
        }
        if (live.GetCurrentPropertyValue(P.UIA_IsSelectionPatternAvailablePropertyId) is true &&
            live.GetCurrentPattern(UIA_PatternIds.UIA_SelectionPatternId) is IUIAutomationSelectionPattern selection)
        {
            var items = selection.GetCurrentSelection();
            if (items is { Length: > 0 }) { selected = items.GetElement(0).CurrentName; }
        }
        if (value is null && e.Role is ElementRole.Document or ElementRole.Edit &&
            live.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern text)
        {
            value = text.DocumentRange.GetText(20_000);
        }
        return new ElementState(true, value ?? selected, isChecked, selected ?? value);
    }

    // ---------------------------------------------------------------- cached property helpers
    internal static object? SafeValue(IUIAutomationElement e, int propertyId)
    {
        try
        {
            var v = e.GetCachedPropertyValue(propertyId);
            return v is null || ReferenceEquals(v, System.Reflection.Missing.Value) || v is System.Runtime.InteropServices.UnknownWrapper ? null : v;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    private static string SafeString(IUIAutomationElement e, int propertyId) => SafeValue(e, propertyId) as string ?? "";

    private static int SafeInt(IUIAutomationElement e, int propertyId) => SafeValue(e, propertyId) is int i ? i : -1;

    private static bool SafeBool(IUIAutomationElement e, int propertyId) => SafeValue(e, propertyId) is true;

    private static ScreenRect SafeRect(IUIAutomationElement e)
    {
        if (SafeValue(e, P.UIA_BoundingRectanglePropertyId) is double[] { Length: >= 4 } r &&
            !double.IsInfinity(r[0]) && !double.IsNaN(r[0]))
        {
            return new ScreenRect((int)r[0], (int)r[1], (int)r[2], (int)r[3]);
        }
        return ScreenRect.Empty;
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
