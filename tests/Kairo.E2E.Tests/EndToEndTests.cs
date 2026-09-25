using System.Text.Json;
using Kairo.Core.Abstractions;
using Kairo.Core.Agent;
using Kairo.Core.Models;
using Kairo.Core.Security;
using Kairo.Core.Telemetry;
using Kairo.Tests.Shared;
using Kairo.Windows;
using Kairo.Windows.Automation;
using Kairo.Windows.Security;
using Xunit.Abstractions;

namespace Kairo.E2E.Tests;

/// <summary>
/// End-to-end: real Windows perception (UI Automation / DOM), real Kairo agent loop, real execution,
/// real PDF reading. The generative planner and Jev are replaced by deterministic stand-ins unless
/// OPENROUTER_API_KEY is set (see <see cref="LiveModelTests"/>).
/// </summary>
public sealed class NativeFormTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kairo-e2e-" + Guid.NewGuid().ToString("N"));
    private System.Diagnostics.Process? _process;
    private WindowInfo? _window;
    private string _outputFile = "";

    public NativeFormTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        (_process, _window, _outputFile) = await TestSupport.StartTestTargetAsync();
    }

    public Task DisposeAsync()
    {
        TestSupport.Kill(_process);
        try { Directory.Delete(_root, true); } catch (Exception) { }
        if (File.Exists(_outputFile)) { File.Delete(_outputFile); }
        return Task.CompletedTask;
    }

    private async Task<Dictionary<string, ElementState>> ReadFormAsync()
    {
        var core = new UiaCore(KairoLogger.Null);
        var provider = new UiaPerceptionProvider(core, KairoLogger.Null);
        var snapshot = await provider.CaptureAsync(_window!, new PerceptionRequest(), CancellationToken.None);
        var states = await provider.ReadStatesAsync(snapshot!, snapshot!.Elements.ToList(), CancellationToken.None);
        return snapshot.Elements.Where(e => e.Name.Length > 0).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => states[g.First().Locator]);
    }

    [SkippableFact]
    public async Task Fills_the_native_contact_form_from_a_pdf()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var pdf = E2E.CreateContactPdf(Path.Combine(_root, "docs"));
        var planner = new ScriptedFormPlanner();
        var decisions = new LexicalDecisionModel();
        var interaction = new RecordingInteraction();
        await using var runtime = E2E.CreateRuntime(interaction, planner, decisions, Path.Combine(_root, "data"));

        var task = await E2E.RunTaskAsync(runtime,
            $"Fülle das Kontaktformular aus, das ich gerade geöffnet habe. Meine Kontaktinformationen findest du unter {pdf}.",
            _window!, TimeSpan.FromMinutes(2));
        _output.WriteLine(E2E.DescribeLog(task));

        Assert.Equal(AgentTaskState.Completed, task.State);
        // The PDF was read locally and its content reached the planner as untrusted data.
        Assert.Contains("source=\"file:Kontaktinformationen.pdf\"", planner.Prompts[0]);
        Assert.Contains("max.muster@example.ch", planner.Prompts[0]);
        // Jev resolved the element steps in one batched request.
        Assert.Single(decisions.Requests, r => r.Purpose == "resolve");
        Assert.Empty(interaction.Requests); // filling needs no approval

        var form = await ReadFormAsync();
        Assert.Equal("Max", form["Vorname"].Value);
        Assert.Equal("Muster", form["Nachname"].Value);
        Assert.Equal("max.muster@example.ch", form["E-Mail"].Value);
        Assert.Equal("+41 79 123 45 67", form["Telefon"].Value);
        Assert.Equal("Muster AG", form["Firma"].Value);
        Assert.Equal("Schweiz", form["Land"].SelectedOption ?? form["Land"].Value);
        Assert.True(form["Ich akzeptiere die Datenschutzerklärung"].IsChecked);
        Assert.False(File.Exists(_outputFile), "the form must not be submitted when the user only asked to fill it");

        // Latency of the main steps was measured.
        Assert.Contains(task.Metrics.LatencySummary.Keys, k => k.StartsWith("perception", StringComparison.Ordinal));
        Assert.Contains(task.Metrics.LatencySummary.Keys, k => k.StartsWith("execute", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Submitting_requires_approval_and_is_executed_after_approval()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var pdf = E2E.CreateContactPdf(Path.Combine(_root, "docs"));
        var interaction = new RecordingInteraction { Decision = ApprovalDecision.AllowOnce };
        await using var runtime = E2E.CreateRuntime(interaction, new ScriptedFormPlanner(), new LexicalDecisionModel(), Path.Combine(_root, "data"));

        var task = await E2E.RunTaskAsync(runtime, $"Fülle das Formular mit den Daten aus {pdf} aus und sende es ab.", _window!, TimeSpan.FromMinutes(2));
        _output.WriteLine(E2E.DescribeLog(task));

        Assert.Equal(AgentTaskState.Completed, task.State);
        var approval = Assert.Single(interaction.Requests);
        Assert.Equal(RiskLevel.Sensitive, approval.Risk);
        Assert.Contains("Absenden", approval.Description);
        for (var i = 0; i < 50 && !File.Exists(_outputFile); i++) { await Task.Delay(100); }
        using var json = JsonDocument.Parse(File.ReadAllText(_outputFile));
        Assert.Equal("Max", json.RootElement.GetProperty("Vorname").GetString());
        Assert.Equal("max.muster@example.ch", json.RootElement.GetProperty("E-Mail").GetString());
        Assert.True(json.RootElement.GetProperty("Datenschutz").GetBoolean());
    }

    [SkippableFact]
    public async Task Denied_submit_is_not_executed()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var pdf = E2E.CreateContactPdf(Path.Combine(_root, "docs"));
        var interaction = new RecordingInteraction { Decision = ApprovalDecision.Deny };
        await using var runtime = E2E.CreateRuntime(interaction, new ScriptedFormPlanner(), new LexicalDecisionModel(), Path.Combine(_root, "data"));
        var task = await E2E.RunTaskAsync(runtime, $"Fülle das Formular mit {pdf} aus und sende es ab.", _window!, TimeSpan.FromMinutes(2));
        _output.WriteLine(E2E.DescribeLog(task));
        Assert.Single(interaction.Requests);
        await Task.Delay(500);
        Assert.False(File.Exists(_outputFile));
        Assert.Contains("nicht ausgeführt", task.ResultMessage);
    }

    [SkippableFact]
    public async Task Cancelling_during_planning_prevents_every_action()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var pdf = E2E.CreateContactPdf(Path.Combine(_root, "docs"));
        var planner = new ScriptedFormPlanner { ResponseDelay = TimeSpan.FromSeconds(5) };
        await using var runtime = E2E.CreateRuntime(new RecordingInteraction(), planner, new LexicalDecisionModel(), Path.Combine(_root, "data"));
        var before = await ReadFormAsync();

        var done = new TaskCompletionSource<AgentTask>();
        runtime.Tasks.TaskFinished += (_, t) => done.TrySetResult(t);
        var task = runtime.Tasks.Start($"Fülle das Formular mit {pdf} aus.", _window!);
        while (task.State != AgentTaskState.Planning || planner.Prompts.Count == 0) { await Task.Delay(20); }
        runtime.Tasks.CancelCurrent("Test");
        var finished = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AgentTaskState.Cancelled, finished.State);
        Assert.Equal(0, finished.CompletedActions);
        await Task.Delay(1000);
        var after = await ReadFormAsync();
        foreach (var (name, state) in before) { Assert.Equal(state.Value, after[name].Value); }
    }

    [SkippableFact]
    public async Task Cancelling_while_typing_stops_the_keyboard_input()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.IfNot(TestSupport.IsInteractiveSession(), "Needs keyboard input.");
        var longText = string.Concat(Enumerable.Repeat("Kairo tippt langsam. ", 20));
        var planner = new ScriptedFormPlanner
        {
            CustomPlan = prompt =>
            {
                var id = ScriptedFormPlanner.IdOf(prompt, "Nachricht");
                return $$"""{"status":"Tippe …","steps":[{"action":"focus","target":{{id}},"target_label":"Nachricht","description":"Nachricht fokussieren"},{"action":"type_text","value":{{ScriptedFormPlanner.Json(longText)}},"description":"Text tippen"}],"after_steps":"verify_and_finish","final_message":"ok"}""";
            },
        };
        await using var runtime = E2E.CreateRuntime(new RecordingInteraction(), planner, new LexicalDecisionModel(), Path.Combine(_root, "data"));
        runtime.Settings.Update(s => s.Control.TypingDelayMs = 40);

        var done = new TaskCompletionSource<AgentTask>();
        runtime.Tasks.TaskFinished += (_, t) => done.TrySetResult(t);
        runtime.Tasks.Start("Schreibe einen langen Text in die Nachricht.", _window!);

        string? typed = null;
        for (var i = 0; i < 200; i++)
        {
            typed = (await ReadFormAsync())["Nachricht"].Value;
            if (typed?.Length > 8) { break; }
            await Task.Delay(50);
        }
        Assert.True(typed?.Length > 8, "typing did not start");
        runtime.Tasks.CancelCurrent("Test");
        var finished = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(AgentTaskState.Cancelled, finished.State);

        var lengthAtCancel = (await ReadFormAsync())["Nachricht"].Value?.Length ?? 0;
        await Task.Delay(1500);
        var lengthLater = (await ReadFormAsync())["Nachricht"].Value?.Length ?? 0;
        Assert.Equal(lengthAtCancel, lengthLater);
        Assert.True(lengthLater < longText.Length, "the whole text was typed despite the cancellation");
    }
}

/// <summary>Real browser (Microsoft Edge) with a local test form – via UI Automation and via the extension.</summary>
public sealed class BrowserFormTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kairo-e2e-web-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFormServer _server;

    public BrowserFormTests(ITestOutputHelper output)
    {
        _output = output;
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assets", "e2e-contact-form.html"));
        _server = new LocalFormServer(html);
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    [SkippableFact]
    public async Task Fills_and_submits_a_web_form_in_edge_via_ui_automation()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var edge = E2E.FindEdge();
        Skip.If(edge is null, "Microsoft Edge is not installed.");
        var profile = Path.Combine(_root, "edge-profile");
        var browser = E2E.StartEdge(edge!, profile, _server.Url);
        try
        {
            var window = await TestSupport.WaitForWindowAsync(w => w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) && w.Title.Contains("Kontakt", StringComparison.Ordinal), TimeSpan.FromSeconds(45));
            await Task.Delay(1500); // let the page finish loading and the accessibility tree build
            var pdf = E2E.CreateContactPdf(Path.Combine(_root, "docs"));
            var interaction = new RecordingInteraction { Decision = ApprovalDecision.AllowOnce };
            var planner = new ScriptedFormPlanner();
            await using var runtime = E2E.CreateRuntime(interaction, planner, new LexicalDecisionModel(), Path.Combine(_root, "data"));

            var task = await E2E.RunTaskAsync(runtime,
                $"Fülle das Kontaktformular aus, das ich gerade geöffnet habe, und sende es ab. Meine Kontaktinformationen findest du unter {pdf}.",
                window, TimeSpan.FromMinutes(3));
            _output.WriteLine(planner.Prompts.FirstOrDefault() ?? "");
            _output.WriteLine(E2E.DescribeLog(task));

            Assert.Equal(AgentTaskState.Completed, task.State);
            Assert.Contains(interaction.Requests, r => r.Risk >= RiskLevel.Sensitive);
            var winner = await Task.WhenAny(_server.Submission.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(winner == _server.Submission.Task, "the browser did not submit the form");
            var form = await _server.Submission.Task;
            Assert.Equal("Max", form["vorname"]);
            Assert.Equal("Muster", form["nachname"]);
            Assert.Equal("max.muster@example.ch", form["email"]);
            Assert.Equal("+41 79 123 45 67", form["telefon"]);
            Assert.Equal("Muster AG", form["firma"]);
            Assert.Equal("CH", form["land"]);
            Assert.Equal("ja", form["datenschutz"]);
        }
        finally
        {
            E2E.KillEdge(browser, profile);
        }
    }

    [SkippableFact]
    public async Task Fills_a_web_form_through_the_browser_extension_dom_bridge()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        var edge = E2E.FindEdge();
        Skip.If(edge is null, "Microsoft Edge is not installed.");

        var extension = Path.Combine(TestSupport.RepoRoot(), "extension");
        var hostExe = Directory.EnumerateFiles(Path.Combine(TestSupport.RepoRoot(), "src", "Kairo.BrowserHost", "bin"), "Kairo.BrowserHost.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        Skip.If(hostExe is null, "Kairo.BrowserHost.exe not built.");

        var pipe = "kairo-e2e-" + Guid.NewGuid().ToString("N");
        var profile = Path.Combine(_root, "edge-ext-profile");
        NativeHostRegistrar.Register(hostExe!, Path.Combine(_root, "host"));
        Environment.SetEnvironmentVariable("KAIRO_BRIDGE_PIPE", pipe); // inherited by Edge → native host
        var pdf = E2E.CreateContactPdf(Path.Combine(_root, "docs"));
        var interaction = new RecordingInteraction();
        await using var runtime = E2E.CreateRuntime(interaction, new ScriptedFormPlanner { SubmitWhenAsked = false }, new LexicalDecisionModel(), Path.Combine(_root, "data"), startBridge: true, pipeName: pipe);
        var connected = new TaskCompletionSource<bool>();
        runtime.Bridge.Connected += (_, _) => connected.TrySetResult(true);

        var browser = E2E.StartEdge(edge!, profile, _server.Url, $"--load-extension=\"{extension}\"", $"--disable-extensions-except=\"{extension}\"");
        try
        {
            var window = await TestSupport.WaitForWindowAsync(w => w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) && w.Title.Contains("Kontakt", StringComparison.Ordinal), TimeSpan.FromSeconds(45));
            var winner = await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(40)));
            Skip.If(winner != connected.Task, "The unpacked extension could not be loaded via --load-extension in this Edge version (manual test required, see docs/TESTING.md).");

            var task = await E2E.RunTaskAsync(runtime, $"Fülle das Kontaktformular aus. Meine Daten stehen in {pdf}.", window, TimeSpan.FromMinutes(2));
            _output.WriteLine(E2E.DescribeLog(task));
            Assert.Equal(AgentTaskState.Completed, task.State);
            Assert.Contains(task.Log, l => l.Text.Contains("Browser-DOM", StringComparison.Ordinal));

            var snapshot = await runtime.Perception.GetSnapshotAsync(window, forceRefresh: true, CancellationToken.None);
            Assert.Equal(PerceptionSource.BrowserDom, snapshot!.Source);
            Assert.Equal("Max", snapshot.Elements.First(e => e.Name == "Vorname").Value);
            Assert.Equal("max.muster@example.ch", snapshot.Elements.First(e => e.Name == "E-Mail").Value);
            Assert.Equal("Schweiz", snapshot.Elements.First(e => e.Name == "Land").Value);
            Assert.True(snapshot.Elements.First(e => e.Name.StartsWith("Ich akzeptiere", StringComparison.Ordinal)).IsChecked);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KAIRO_BRIDGE_PIPE", null);
            E2E.KillEdge(browser, profile);
            NativeHostRegistrar.Unregister();
        }
    }
}

/// <summary>Runs only when OPENROUTER_API_KEY is set: real planner model and real Jev via OpenRouter.</summary>
public sealed class LiveModelTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kairo-live-" + Guid.NewGuid().ToString("N"));
    private System.Diagnostics.Process? _process;
    private WindowInfo? _window;

    public LiveModelTests(ITestOutputHelper output) => _output = output;

    private static string? Key => Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Key)) { return; }
        (_process, _window, _) = await TestSupport.StartTestTargetAsync();
    }

    public Task DisposeAsync()
    {
        TestSupport.Kill(_process);
        try { Directory.Delete(_root, true); } catch (Exception) { }
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Jev_answers_through_the_openrouter_decisions_api()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.If(string.IsNullOrWhiteSpace(Key), "OPENROUTER_API_KEY not set – live test skipped.");
        await using var runtime = E2E.CreateRuntime(new RecordingInteraction(), null, null, Path.Combine(_root, "data"));
        runtime.Secrets.SetSecret(KairoRuntime.ApiKeySecretName, Key!);
        var (ok, message, latency) = await runtime.Jev.ProbeAsync(runtime.Settings.Current.Models.DecisionModel, CancellationToken.None);
        _output.WriteLine($"{message} ({latency.TotalMilliseconds:0} ms)");
        Assert.True(ok, message);
    }

    [SkippableFact]
    public async Task Fills_the_native_form_with_real_models()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Skip.If(string.IsNullOrWhiteSpace(Key), "OPENROUTER_API_KEY not set – live test skipped.");
        var pdf = E2E.CreateContactPdf(Path.Combine(_root, "docs"));
        await using var runtime = E2E.CreateRuntime(new RecordingInteraction { Decision = ApprovalDecision.Deny }, null, null, Path.Combine(_root, "data"));
        runtime.Secrets.SetSecret(KairoRuntime.ApiKeySecretName, Key!);
        var report = await runtime.ConnectionTester.TestAsync(runtime.Settings.Current.Models.PlannerModel, runtime.Settings.Current.Models.DecisionModel, null, CancellationToken.None);
        foreach (var m in report.Models.Where(m => !m.Available && m.Suggestion is not null && m.Role.StartsWith("Planungs", StringComparison.Ordinal)))
        {
            runtime.Settings.Update(s => s.Models.PlannerModel = m.Suggestion!);
        }

        var task = await E2E.RunTaskAsync(runtime,
            $"Fülle das Kontaktformular aus, das ich gerade geöffnet habe. Meine Kontaktinformationen findest du unter {pdf}. Nicht absenden.",
            _window!, TimeSpan.FromMinutes(4));
        _output.WriteLine(E2E.DescribeLog(task));
        _output.WriteLine($"Cost: ${task.Metrics.TotalCost:0.00000}, calls: {task.Metrics.ModelCalls} (Jev: {task.Metrics.DecisionCalls}), duration {task.Duration.TotalSeconds:0.0} s");

        Assert.Equal(AgentTaskState.Completed, task.State);
        Assert.True(task.Metrics.DecisionCalls >= 1, "Jev was not used");
        var provider = new UiaPerceptionProvider(new UiaCore(KairoLogger.Null), KairoLogger.Null);
        var snapshot = await provider.CaptureAsync(_window!, new PerceptionRequest(), CancellationToken.None);
        var states = await provider.ReadStatesAsync(snapshot!, snapshot!.Elements.ToList(), CancellationToken.None);
        string? Value(string name) => states[snapshot.Elements.First(e => e.Name == name).Locator].Value;
        Assert.Equal("Max", Value("Vorname"));
        Assert.Equal("Muster", Value("Nachname"));
        Assert.Equal("max.muster@example.ch", Value("E-Mail"));
    }
}
