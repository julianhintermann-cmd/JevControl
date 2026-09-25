using System.Globalization;
using System.Text.Json.Nodes;
using Kairo.Core.Abstractions;
using Kairo.Core.Models;
using Kairo.Core.Perception;
using Kairo.Core.Telemetry;
using Kairo.Windows.Input;
using Kairo.Windows.Windows;

namespace Kairo.Windows.Browser;

/// <summary>
/// Stage 2 of the perception: for Chrome/Edge windows the DOM is read through the Kairo extension.
/// Elements get the locator "dom:&lt;tabId&gt;:&lt;kid&gt;" and are acted upon with DOM operations.
/// </summary>
public sealed class BrowserPerceptionProvider : IPerceptionProvider
{
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(20);
    private readonly BrowserBridgeServer _bridge;
    private readonly KairoLogger _log;

    public BrowserPerceptionProvider(BrowserBridgeServer bridge, KairoLogger log)
    {
        _bridge = bridge;
        _log = log;
    }

    public bool Enabled { get; set; } = true;

    public PerceptionSource Source => PerceptionSource.BrowserDom;

    public int Priority => 20;

    public bool CanHandle(WindowInfo window) => Enabled && window.IsChromiumBrowser && _bridge.ConnectionFor(window.BrowserKind) is not null;

    public async Task<UiSnapshot?> CaptureAsync(WindowInfo window, PerceptionRequest request, CancellationToken cancellationToken)
    {
        var connection = _bridge.ConnectionFor(window.BrowserKind);
        if (connection is null) { return null; }

        var tabId = await FindTabAsync(connection, window, cancellationToken).ConfigureAwait(false);
        var parameters = new JsonObject { ["maxElements"] = request.MaxElements, ["includeText"] = request.IncludeText };
        if (tabId is { } t) { parameters["tabId"] = t; }

        JsonNode? result;
        try
        {
            result = await connection.RequestAsync("snapshot", parameters, SnapshotTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserBridgeException ex) when (ex.Code is "restricted_page" or "no_tab")
        {
            _log.Info("dom", $"DOM not available ({ex.Code}) – falling back to UI Automation");
            return null;
        }
        return result is null ? null : ToSnapshot(result, window);
    }

    /// <summary>Maps the Win32 window to the browser tab: focused window whose active tab title matches.</summary>
    internal static async Task<int?> FindTabAsync(BrowserConnection connection, WindowInfo window, CancellationToken cancellationToken)
    {
        var list = await connection.RequestAsync("listWindows", null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (list?["windows"] is not JsonArray windows) { return null; }
        int? best = null;
        var bestScore = -1;
        foreach (var w in windows.OfType<JsonObject>())
        {
            var activeTab = (w["tabs"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => t["active"]?.GetValue<bool>() == true);
            var title = activeTab?["title"]?.ToString() ?? "";
            var score = 0;
            if (title.Length > 0 && window.Title.StartsWith(title, StringComparison.Ordinal)) { score += 4; }
            else if (title.Length > 0 && window.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) { score += 2; }
            if (w["focused"]?.GetValue<bool>() == true) { score += 1; }
            if (Math.Abs((w["left"]?.GetValue<int>() ?? int.MinValue) - window.Bounds.X) < 20 && Math.Abs((w["top"]?.GetValue<int>() ?? int.MinValue) - window.Bounds.Y) < 20) { score += 1; }
            if (score > bestScore && activeTab?["tabId"] is JsonValue id)
            {
                bestScore = score;
                best = id.GetValue<int>();
            }
        }
        return best;
    }

    internal static UiSnapshot ToSnapshot(JsonNode result, WindowInfo window)
    {
        var tabId = result["tabId"]?.GetValue<int>() ?? 0;
        var viewport = result["viewport"];
        var dpr = viewport?["devicePixelRatio"]?.GetValue<double>() ?? 1.0;
        var innerWidth = viewport?["innerWidth"]?.GetValue<double>() ?? 0;
        var innerHeight = viewport?["innerHeight"]?.GetValue<double>() ?? 0;
        var outerWidth = viewport?["outerWidth"]?.GetValue<double>() ?? innerWidth;
        var border = Math.Max(0, (outerWidth - innerWidth) / 2) * dpr;
        var contentLeft = window.Bounds.X + border;
        var contentTop = window.Bounds.Bottom - innerHeight * dpr - border;

        var elements = new List<UiElement>();
        var id = 1;
        foreach (var e in (result["elements"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var kid = e["kid"]?.ToString();
            if (kid is null) { continue; }
            var role = (e["role"]?.ToString() ?? "").ToLowerInvariant();
            var type = e["type"]?.ToString()?.ToLowerInvariant();
            var elementRole = role switch
            {
                "textbox" => ElementRole.Edit,
                "textarea" => ElementRole.Document,
                "button" => ElementRole.Button,
                "link" => ElementRole.Link,
                "checkbox" or "switch" => ElementRole.CheckBox,
                "radio" => ElementRole.RadioButton,
                "combobox" => ElementRole.ComboBox,
                "listbox" => ElementRole.List,
                "slider" => ElementRole.Slider,
                "tab" => ElementRole.TabItem,
                "menuitem" => ElementRole.MenuItem,
                "option" => ElementRole.ListItem,
                "file" => ElementRole.FileInput,
                _ => ElementRole.Unknown,
            };
            if (elementRole == ElementRole.Unknown) { continue; }

            var caps = elementRole switch
            {
                ElementRole.Edit or ElementRole.Document => ElementCapabilities.SetValue | ElementCapabilities.Focus | ElementCapabilities.ScrollIntoView,
                ElementRole.CheckBox or ElementRole.RadioButton => ElementCapabilities.Toggle | ElementCapabilities.Invoke | ElementCapabilities.Focus,
                ElementRole.ComboBox or ElementRole.List => ElementCapabilities.Select | ElementCapabilities.ExpandCollapse | ElementCapabilities.Focus,
                ElementRole.Slider => ElementCapabilities.SetValue | ElementCapabilities.RangeValue,
                ElementRole.FileInput => ElementCapabilities.Focus,
                _ => ElementCapabilities.Invoke | ElementCapabilities.Focus | ElementCapabilities.ScrollIntoView,
            };

            var rect = e["rect"];
            var bounds = rect is null ? ScreenRect.Empty : new ScreenRect(
                (int)Math.Round(contentLeft + (rect["x"]?.GetValue<double>() ?? 0) * dpr),
                (int)Math.Round(contentTop + (rect["y"]?.GetValue<double>() ?? 0) * dpr),
                (int)Math.Round((rect["width"]?.GetValue<double>() ?? 0) * dpr),
                (int)Math.Round((rect["height"]?.GetValue<double>() ?? 0) * dpr));

            List<string>? options = null;
            string? selectedOption = null;
            if (e["options"] is JsonArray optionArray)
            {
                options = [];
                foreach (var o in optionArray.OfType<JsonObject>())
                {
                    var text = o["text"]?.ToString() ?? o["value"]?.ToString() ?? "";
                    if (text.Length > 0) { options.Add(text); }
                    if (o["selected"]?.GetValue<bool>() == true) { selectedOption = text; }
                }
            }

            var label = e["label"]?.ToString();
            var text2 = e["text"]?.ToString();
            var name = !string.IsNullOrWhiteSpace(label) ? label : text2 ?? e["name"]?.ToString() ?? "";
            var isPassword = e["isPassword"]?.GetValue<bool>() == true;
            var autocomplete = e["autocomplete"]?.ToString();
            var hint = !string.IsNullOrWhiteSpace(autocomplete) && autocomplete is not ("on" or "off") ? autocomplete :
                       type is "email" or "tel" or "url" or "number" or "date" or "submit" or "search" or "password" ? type : null;

            elements.Add(new UiElement
            {
                Id = id++,
                Role = elementRole,
                Name = name.Trim(),
                Value = isPassword ? null : elementRole == ElementRole.ComboBox ? selectedOption ?? e["value"]?.ToString() : e["value"]?.ToString(),
                Placeholder = e["placeholder"]?.ToString(),
                Section = e["section"]?.ToString(),
                InputHint = hint,
                Capabilities = caps,
                IsEnabled = e["disabled"]?.GetValue<bool>() != true,
                IsReadOnly = e["readonly"]?.GetValue<bool>() == true,
                IsRequired = e["required"]?.GetValue<bool>() == true,
                IsMultiline = e["multiline"]?.GetValue<bool>() == true || elementRole == ElementRole.Document,
                IsPassword = isPassword,
                IsChecked = e["checked"] is JsonValue c && c.TryGetValue<bool>(out var b) ? b : null,
                IsOffscreen = e["inViewport"]?.GetValue<bool>() == false || e["visible"]?.GetValue<bool>() == false,
                Options = options,
                Bounds = bounds,
                AutomationId = e["id"]?.ToString() is { Length: > 0 } htmlId ? htmlId : e["name"]?.ToString(),
                Url = e["href"]?.ToString(),
                Locator = $"dom:{tabId.ToString(CultureInfo.InvariantCulture)}:{kid}",
            });
        }

        return new UiSnapshot
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            Source = PerceptionSource.BrowserDom,
            Window = window,
            Url = result["url"]?.ToString(),
            PageTitle = result["title"]?.ToString(),
            TabId = tabId,
            Elements = elements,
            TextBlocks = (result["texts"] as JsonArray ?? []).Select(t => t?.ToString() ?? "").Where(t => t.Length > 0).ToList(),
            Truncated = result["truncated"]?.GetValue<bool>() == true,
        };
    }

    internal static (int TabId, string Kid) ParseLocator(string locator)
    {
        // dom:<tabId>:<frameId>:k<n>
        var rest = locator["dom:".Length..];
        var sep = rest.IndexOf(':');
        return (int.Parse(rest[..sep], CultureInfo.InvariantCulture), rest[(sep + 1)..]);
    }

    public async Task<IReadOnlyDictionary<string, ElementState>> ReadStatesAsync(UiSnapshot snapshot, IReadOnlyCollection<UiElement> elements, CancellationToken cancellationToken)
    {
        var connection = _bridge.ConnectionFor(snapshot.Window.BrowserKind) ?? throw new BrowserBridgeException("disconnected", "Browser-Erweiterung nicht verbunden.");
        var result = new Dictionary<string, ElementState>();
        foreach (var group in elements.GroupBy(e => ParseLocator(e.Locator).TabId))
        {
            var kids = new JsonArray(group.Select(e => (JsonNode)JsonValue.Create(ParseLocator(e.Locator).Kid)!).ToArray());
            var response = await connection.RequestAsync("readValues", new JsonObject { ["tabId"] = group.Key, ["kids"] = kids }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            foreach (var e in group)
            {
                var v = response?["values"]?[ParseLocator(e.Locator).Kid];
                result[e.Locator] = v is null || v["exists"]?.GetValue<bool>() != true
                    ? new ElementState(false)
                    : new ElementState(true, v["value"]?.ToString(), v["checked"] is JsonValue c && c.TryGetValue<bool>(out var b) ? b : null, v["value"]?.ToString());
            }
        }
        return result;
    }
}

/// <summary>DOM actions through the extension (fill, click, check, select, focus) – one round trip per batch.</summary>
public sealed class BrowserElementActions
{
    private static readonly TimeSpan ActTimeout = TimeSpan.FromSeconds(30);
    private readonly BrowserBridgeServer _bridge;
    private readonly WindowService _windows;
    private readonly InputSimulator _input;

    public BrowserElementActions(BrowserBridgeServer bridge, WindowService windows, InputSimulator input)
    {
        _bridge = bridge;
        _windows = windows;
        _input = input;
    }

    private static (string Action, string? Value) Map(AgentAction a) => a.Kind switch
    {
        ActionKind.SetValue => ("fill", a.Value ?? ""),
        ActionKind.Click => ("click", null),
        ActionKind.SetChecked => (a.Checked == false ? "uncheck" : "check", null),
        ActionKind.SelectOption => ("select", a.Option ?? a.Value),
        ActionKind.Focus => ("focus", null),
        _ => throw new NotSupportedException(a.Kind.ToString()),
    };

    public async Task<ActionResult> ExecuteAsync(AgentAction action, UiElement element, ActionContext context, CancellationToken cancellationToken)
    {
        var connection = _bridge.ConnectionFor(context.TargetWindow.BrowserKind);
        if (connection is null) { return ActionResult.Fail(ActionErrorKind.NotSupported, "Browser-Erweiterung nicht verbunden."); }
        var (tabId, kid) = BrowserPerceptionProvider.ParseLocator(element.Locator);

        if (context.PreferInputSimulation && action.Kind == ActionKind.SetValue)
        {
            // Correction mode: focus through the DOM, then type like a human would.
            await connection.RequestAsync("act", new JsonObject { ["tabId"] = tabId, ["kid"] = kid, ["action"] = "focus" }, ActTimeout, cancellationToken).ConfigureAwait(false);
            context.Gate.ThrowIfClosed();
            if (!await _windows.ActivateAsync(context.TargetWindow.Handle, cancellationToken).ConfigureAwait(false))
            {
                return ActionResult.Fail(ActionErrorKind.Failed, "Das Browserfenster konnte nicht in den Vordergrund geholt werden – Kairo hat deshalb keine Tastatureingabe gesendet.");
            }
            _input.Press("ctrl+a");
            await _input.TypeTextAsync(action.Value ?? "", element.IsMultiline, context.TypingDelayMs, () => context.Gate.IsOpen, cancellationToken).ConfigureAwait(false);
            return ActionResult.Ok("", "DOM-Fokus + SendInput");
        }

        var (name, value) = Map(action);
        await connection.ActionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            context.Gate.ThrowIfClosed();
            var parameters = new JsonObject { ["tabId"] = tabId, ["kid"] = kid, ["action"] = name };
            if (value is not null) { parameters["value"] = value; }
            await connection.RequestAsync("act", parameters, ActTimeout, cancellationToken).ConfigureAwait(false);
            return ActionResult.Ok("", "DOM", structureChanged: action.Kind == ActionKind.Click);
        }
        catch (BrowserBridgeException ex)
        {
            return ActionResult.Fail(ex.Code == "element_not_found" ? ActionErrorKind.ElementNotFound : ActionErrorKind.Failed, ex.Message);
        }
        finally
        {
            connection.ActionLock.Release();
        }
    }

    public async Task<IReadOnlyList<ActionResult>> ExecuteBatchAsync(IReadOnlyList<(AgentAction Action, UiElement Element)> items, ActionContext context, CancellationToken cancellationToken)
    {
        var connection = _bridge.ConnectionFor(context.TargetWindow.BrowserKind);
        if (connection is null) { return items.Select(_ => ActionResult.Fail(ActionErrorKind.NotSupported, "Browser-Erweiterung nicht verbunden.")).ToList(); }

        var results = new List<ActionResult>();
        foreach (var group in items.GroupBy(i => BrowserPerceptionProvider.ParseLocator(i.Element.Locator).TabId))
        {
            var list = group.ToList();
            var actions = new JsonArray();
            foreach (var (action, element) in list)
            {
                var (name, value) = Map(action);
                var obj = new JsonObject { ["kid"] = BrowserPerceptionProvider.ParseLocator(element.Locator).Kid, ["action"] = name };
                if (value is not null) { obj["value"] = value; }
                actions.Add(obj);
            }

            await connection.ActionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                context.Gate.ThrowIfClosed();
                var response = await connection.RequestAsync("actBatch", new JsonObject { ["tabId"] = group.Key, ["actions"] = actions }, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
                var perItem = response?["results"] as JsonArray;
                for (var i = 0; i < list.Count; i++)
                {
                    var r = perItem?.ElementAtOrDefault(i);
                    results.Add(r?["ok"]?.GetValue<bool>() == true
                        ? ActionResult.Ok("", "DOM (Batch)")
                        : ActionResult.Fail(ActionErrorKind.Failed, r?["error"]?.ToString() ?? "Aktion fehlgeschlagen"));
                }
            }
            catch (BrowserBridgeException ex)
            {
                results.AddRange(list.Select(_ => ActionResult.Fail(ActionErrorKind.Failed, ex.Message)));
            }
            finally
            {
                connection.ActionLock.Release();
            }
        }
        return results;
    }

    /// <summary>Tab level operations used by open_url / browser_tab.</summary>
    public async Task<ActionResult> TabOperationAsync(WindowInfo window, string operation, string? url, string? title, CancellationToken cancellationToken)
    {
        var connection = _bridge.ConnectionFor(window.BrowserKind) ?? _bridge.Connections.FirstOrDefault();
        if (connection is null) { return ActionResult.Fail(ActionErrorKind.NotSupported, "Browser-Erweiterung nicht verbunden."); }
        try
        {
            switch (operation)
            {
                case "new":
                    await connection.RequestAsync("openTab", new JsonObject { ["url"] = url ?? "about:blank", ["active"] = true }, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
                    return ActionResult.Ok("Neuer Tab geöffnet.", "Extension", structureChanged: true);
                case "navigate":
                    var tab = await BrowserPerceptionProvider.FindTabAsync(connection, window, cancellationToken).ConfigureAwait(false);
                    await connection.RequestAsync("navigate", new JsonObject { ["tabId"] = tab, ["url"] = url }, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
                    return ActionResult.Ok("Seite geöffnet.", "Extension", structureChanged: true);
                case "switch":
                    var list = await connection.RequestAsync("listWindows", null, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    var tabs = (list?["windows"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(w => (w["tabs"] as JsonArray ?? []).OfType<JsonObject>()).ToList();
                    var wanted = ElementMatcher.Normalize(title);
                    var match = tabs.Select(t => (Tab: t, Score: ElementMatcher.TextSimilarity(wanted, ElementMatcher.Normalize(t["title"]?.ToString()))))
                        .OrderByDescending(x => x.Score).FirstOrDefault();
                    if (match.Tab is null || match.Score < 0.4) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, $"Kein Tab „{title}“ gefunden."); }
                    await connection.RequestAsync("activateTab", new JsonObject { ["tabId"] = match.Tab["tabId"]!.GetValue<int>() }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    return ActionResult.Ok($"Tab „{match.Tab["title"]}“ aktiviert.", "Extension", structureChanged: true);
                case "close":
                    var current = await BrowserPerceptionProvider.FindTabAsync(connection, window, cancellationToken).ConfigureAwait(false);
                    await connection.RequestAsync("closeTab", new JsonObject { ["tabId"] = current }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    return ActionResult.Ok("Tab geschlossen.", "Extension", structureChanged: true);
                case "back":
                case "reload":
                    var active = await BrowserPerceptionProvider.FindTabAsync(connection, window, cancellationToken).ConfigureAwait(false);
                    await connection.RequestAsync(operation == "back" ? "goBack" : "reload", new JsonObject { ["tabId"] = active }, TimeSpan.FromSeconds(25), cancellationToken).ConfigureAwait(false);
                    return ActionResult.Ok("", "Extension", structureChanged: true);
                default:
                    return ActionResult.Fail(ActionErrorKind.InvalidArguments, $"Unbekannte Tab-Aktion „{operation}“.");
            }
        }
        catch (BrowserBridgeException ex)
        {
            return ActionResult.Fail(ActionErrorKind.Failed, ex.Message);
        }
    }

    public bool IsConnected(WindowInfo window) => window.IsChromiumBrowser && _bridge.ConnectionFor(window.BrowserKind) is not null;
}
