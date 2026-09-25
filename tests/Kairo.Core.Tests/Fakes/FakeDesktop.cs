using Kairo.Core.Abstractions;
using Kairo.Core.Models;

namespace Kairo.Core.Tests.Fakes;

/// <summary>An in-memory "application window" with a form. Implements perception, execution and window access.</summary>
public sealed class FakeDesktop : IPerceptionProvider, IActionExecutor, IWindowService
{
    public sealed class Field
    {
        public required int Id { get; init; }
        public required ElementRole Role { get; init; }
        public required string Name { get; init; }
        public string? Value { get; set; }
        public bool? Checked { get; set; }
        public List<string>? Options { get; init; }
        public string? InputHint { get; init; }
        public bool IsPassword { get; init; }
        public bool Required { get; init; }
        public bool Offscreen { get; set; }
        public Action? OnClick { get; init; }
    }

    public FakeDesktop(string title = "Kontakt – Beispiel GmbH – Google Chrome", string process = "chrome")
    {
        Window = new WindowInfo { Handle = 0x1234, Title = title, ProcessName = process, ProcessId = 42, Bounds = new ScreenRect(0, 0, 1280, 800) };
    }

    public WindowInfo Window { get; set; }
    public List<Field> Fields { get; } = [];
    public List<string> Texts { get; } = [];
    public List<(DateTimeOffset Time, AgentAction Action)> Executed { get; } = [];
    public int Captures { get; private set; }
    public bool Submitted { get; set; }
    public TimeSpan ActionDelay { get; set; } = TimeSpan.Zero;
    /// <summary>SetValue on these labels silently does nothing unless input simulation is used (tests correction).</summary>
    public HashSet<string> IgnoreStructuredSetValue { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<AgentAction, ActionResult>? SystemActionHandler { get; set; }

    public Field Get(string name) => Fields.First(f => f.Name == name);

    public static FakeDesktop ContactForm()
    {
        var d = new FakeDesktop();
        d.Texts.Add("Kontaktformular");
        d.Texts.Add("Wir melden uns innerhalb von 24 Stunden.");
        d.Fields.Add(new Field { Id = 1, Role = ElementRole.Edit, Name = "Vorname", Value = "", Required = true, InputHint = "given-name" });
        d.Fields.Add(new Field { Id = 2, Role = ElementRole.Edit, Name = "Nachname", Value = "", Required = true, InputHint = "family-name" });
        d.Fields.Add(new Field { Id = 3, Role = ElementRole.Edit, Name = "E-Mail", Value = "", Required = true, InputHint = "email" });
        d.Fields.Add(new Field { Id = 4, Role = ElementRole.Edit, Name = "Telefon", Value = "", InputHint = "tel" });
        d.Fields.Add(new Field { Id = 5, Role = ElementRole.ComboBox, Name = "Land", Value = "Bitte wählen", Options = ["Bitte wählen", "Schweiz", "Deutschland", "Österreich"] });
        d.Fields.Add(new Field { Id = 6, Role = ElementRole.Document, Name = "Nachricht", Value = "" });
        d.Fields.Add(new Field { Id = 7, Role = ElementRole.CheckBox, Name = "Ich akzeptiere die Datenschutzerklärung", Checked = false, Required = true });
        d.Fields.Add(new Field { Id = 8, Role = ElementRole.Button, Name = "Absenden", InputHint = "submit", OnClick = () => d.Submitted = true });
        d.Fields.Add(new Field { Id = 9, Role = ElementRole.Link, Name = "Impressum" });
        return d;
    }

    // ---------------- IPerceptionProvider ----------------
    public PerceptionSource Source => PerceptionSource.UiAutomation;
    public int Priority => 10;
    public bool CanHandle(WindowInfo window) => window.Handle == Window.Handle;

    public Task<UiSnapshot?> CaptureAsync(WindowInfo window, PerceptionRequest request, CancellationToken cancellationToken)
    {
        Captures++;
        var elements = Fields.Select(ToElement).ToList();
        return Task.FromResult<UiSnapshot?>(new UiSnapshot
        {
            SnapshotId = Guid.NewGuid().ToString("N"),
            Source = PerceptionSource.UiAutomation,
            Window = Window,
            Elements = elements,
            TextBlocks = Texts.ToList(),
            Truncated = Fields.Any(f => f.Offscreen),
        });
    }

    private static UiElement ToElement(Field f) => new()
    {
        Id = f.Id,
        Role = f.Role,
        Name = f.Name,
        Value = f.IsPassword ? null : f.Value,
        IsChecked = f.Checked,
        Options = f.Options,
        InputHint = f.InputHint,
        IsPassword = f.IsPassword,
        IsRequired = f.Required,
        IsOffscreen = f.Offscreen,
        IsMultiline = f.Role == ElementRole.Document,
        Capabilities = f.Role switch
        {
            ElementRole.Edit or ElementRole.Document => ElementCapabilities.SetValue | ElementCapabilities.Focus,
            ElementRole.ComboBox => ElementCapabilities.ExpandCollapse | ElementCapabilities.Select | ElementCapabilities.SetValue,
            ElementRole.CheckBox => ElementCapabilities.Toggle,
            _ => ElementCapabilities.Invoke,
        },
        Bounds = new ScreenRect(100, 100 + f.Id * 40, 300, 30),
        Locator = $"fake:{f.Id}",
    };

    public Task<IReadOnlyDictionary<string, ElementState>> ReadStatesAsync(UiSnapshot snapshot, IReadOnlyCollection<UiElement> elements, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ElementState>();
        foreach (var e in elements)
        {
            var f = Fields.FirstOrDefault(x => $"fake:{x.Id}" == e.Locator);
            result[e.Locator] = f is null ? new ElementState(false) : new ElementState(true, f.Value, f.Checked, f.Role == ElementRole.ComboBox ? f.Value : null);
        }
        return Task.FromResult<IReadOnlyDictionary<string, ElementState>>(result);
    }

    // ---------------- IActionExecutor ----------------
    public async Task<ActionResult> ExecuteAsync(AgentAction action, ActionContext context, CancellationToken cancellationToken)
    {
        context.Gate.ThrowIfClosed();
        if (ActionDelay > TimeSpan.Zero) { await Task.Delay(ActionDelay, cancellationToken); }
        context.Gate.ThrowIfClosed();
        Executed.Add((DateTimeOffset.UtcNow, action));

        if (action.Kind.IsElementAction())
        {
            var f = Fields.FirstOrDefault(x => x.Id == action.TargetId);
            if (f is null) { return ActionResult.Fail(ActionErrorKind.ElementNotFound, "not found"); }
            switch (action.Kind)
            {
                case ActionKind.SetValue:
                    if (IgnoreStructuredSetValue.Contains(f.Name) && !context.PreferInputSimulation) { return ActionResult.Ok("", "ValuePattern"); }
                    f.Value = action.Value;
                    return ActionResult.Ok("", context.PreferInputSimulation ? "SendInput" : "ValuePattern");
                case ActionKind.SetChecked:
                    f.Checked = action.Checked ?? true;
                    return ActionResult.Ok("", "TogglePattern");
                case ActionKind.SelectOption:
                    var option = f.Options?.FirstOrDefault(o => string.Equals(o, action.Option ?? action.Value, StringComparison.OrdinalIgnoreCase));
                    if (option is null) { return ActionResult.Fail(ActionErrorKind.Failed, "option not found"); }
                    f.Value = option;
                    return ActionResult.Ok("", "SelectionItemPattern");
                case ActionKind.Click:
                    f.OnClick?.Invoke();
                    return ActionResult.Ok("", "InvokePattern", structureChanged: f.Role is ElementRole.Button or ElementRole.Link);
                case ActionKind.Focus:
                    return ActionResult.Ok("", "SetFocus");
            }
        }

        if (SystemActionHandler is not null) { return SystemActionHandler(action); }
        return action.Kind switch
        {
            ActionKind.Scroll => ScrollAll(),
            _ => ActionResult.Ok($"{action.Kind} ok"),
        };
    }

    private ActionResult ScrollAll()
    {
        foreach (var f in Fields) { f.Offscreen = false; }
        return ActionResult.Ok("scrolled", "ScrollPattern", structureChanged: true);
    }

    public async Task<IReadOnlyList<ActionResult>> ExecuteElementBatchAsync(IReadOnlyList<AgentAction> actions, ActionContext context, CancellationToken cancellationToken)
    {
        var results = new List<ActionResult>();
        foreach (var a in actions)
        {
            results.Add(await ExecuteAsync(a, context, cancellationToken));
        }
        return results;
    }

    // ---------------- IWindowService ----------------
    public WindowInfo? GetForegroundWindow() => Window;
    public WindowInfo? GetWindow(nint handle) => handle == Window.Handle ? Window : null;
    public IReadOnlyList<WindowInfo> ListWindows() => [Window, new WindowInfo { Handle = 0x999, Title = "Posteingang – Outlook", ProcessName = "olk" }];
    public bool IsWindowAlive(nint handle) => handle == Window.Handle;
    public Task<bool> ActivateAsync(nint handle, CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>Scripted approvals / answers.</summary>
public sealed class FakeInteraction : IUserInteraction
{
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.AllowOnce;
    public string? Answer { get; set; } = "Keine weiteren Angaben.";
    public List<ApprovalRequest> Approvals { get; } = [];
    public List<string> Questions { get; } = [];
    public Action? OnApproval { get; set; }

    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Approvals.Add(request);
        OnApproval?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Decision);
    }

    public Task<string?> AskUserAsync(string question, CancellationToken cancellationToken)
    {
        Questions.Add(question);
        return Task.FromResult(Answer);
    }
}
