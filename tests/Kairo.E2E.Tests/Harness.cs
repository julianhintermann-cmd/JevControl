using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Kairo.Core.Abstractions;
using Kairo.Core.Agent;
using Kairo.Core.AI;
using Kairo.Core.AI.Jev;
using Kairo.Core.Perception;
using Kairo.Core.Settings;
using Kairo.Windows;
using Microsoft.Win32;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Kairo.E2E.Tests;

/// <summary>
/// Deterministic stand-in for the generative planner (NOT an LLM): it reads exactly what Kairo sent –
/// the UI element list and the extracted file text – and maps contact data onto form fields.
/// Used so the Windows pipeline (perception, Jev resolution, permissions, execution, verification)
/// can be tested end-to-end without network access. The live test uses the real OpenRouter models.
/// </summary>
public sealed partial class ScriptedFormPlanner : IChatModel
{
    public List<string> Prompts { get; } = [];
    public bool SubmitWhenAsked { get; set; } = true;
    /// <summary>Simulated model latency (honors cancellation).</summary>
    public TimeSpan ResponseDelay { get; set; }
    /// <summary>Optional custom first-round plan built from the prompt text.</summary>
    public Func<string, string>? CustomPlan { get; set; }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var text = request.Messages[^1].TextContent;
        Prompts.Add(text);
        if (ResponseDelay > TimeSpan.Zero) { await Task.Delay(ResponseDelay, cancellationToken); }
        var round = Prompts.Count - 1;
        string json;
        if (round == 0 && CustomPlan is not null)
        {
            json = CustomPlan(text);
        }
        else if (round > 0)
        {
            json = """{"status":"Fertig","steps":[],"after_steps":"verify_and_finish","final_message":"Das Formular ist ausgefüllt."}""";
        }
        else
        {
            var instruction = Regex.Match(Prompts[0], "<user_instruction>(.*?)</user_instruction>", RegexOptions.Singleline).Groups[1].Value;
            var facts = ExtractFacts(text);
            var elements = ElementLine().Matches(text).Select(m => (Id: int.Parse(m.Groups[1].Value), Role: m.Groups[2].Value, Label: m.Groups[3].Value)).ToList();
            var steps = new List<string>();

            void Fill(string keyword, string? value)
            {
                if (string.IsNullOrEmpty(value)) { return; }
                var el = elements.FirstOrDefault(e => e.Role is "edit" or "textarea" && e.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (el.Label is null) { return; }
                steps.Add($$"""{"action":"set_value","target":{{el.Id}},"target_label":{{Json(el.Label)}},"value":{{Json(value)}},"description":{{Json(el.Label + " eintragen")}}}""");
            }

            var name = facts.GetValueOrDefault("Name")?.Split(' ', 2);
            Fill("Vorname", name?.ElementAtOrDefault(0));
            Fill("Nachname", name?.ElementAtOrDefault(1));
            Fill("E-Mail", facts.GetValueOrDefault("E-Mail"));
            Fill("Telefon", facts.GetValueOrDefault("Telefon"));
            Fill("Firma", facts.GetValueOrDefault("Firma"));
            Fill("Nachricht", "Bitte kontaktieren Sie mich.");

            var country = facts.GetValueOrDefault("Adresse")?.Split(',').LastOrDefault()?.Trim();
            var land = elements.FirstOrDefault(e => e.Role == "combobox" && e.Label.Contains("Land", StringComparison.OrdinalIgnoreCase));
            if (land.Label is not null && country is { Length: > 0 })
            {
                steps.Add($$"""{"action":"select_option","target":{{land.Id}},"target_label":{{Json(land.Label)}},"option":{{Json(country)}},"description":"Land wählen"}""");
            }
            var privacy = elements.FirstOrDefault(e => e.Role == "checkbox" && e.Label.Contains("Datenschutz", StringComparison.OrdinalIgnoreCase));
            if (privacy.Label is not null)
            {
                steps.Add($$"""{"action":"set_checked","target":{{privacy.Id}},"target_label":{{Json(privacy.Label)}},"checked":true,"description":"Datenschutz akzeptieren"}""");
            }
            if (SubmitWhenAsked && Regex.IsMatch(instruction, @"\b(sende|absenden|abschicken)\b", RegexOptions.IgnoreCase))
            {
                var submit = elements.FirstOrDefault(e => e.Role == "button" && e.Label.Contains("Absenden", StringComparison.OrdinalIgnoreCase));
                if (submit.Label is not null)
                {
                    steps.Add($$"""{"action":"click","target":{{submit.Id}},"target_label":{{Json(submit.Label)}},"description":"Formular absenden"}""");
                }
            }
            json = $$"""{"status":"Fülle Formular aus …","steps":[{{string.Join(",", steps)}}],"after_steps":"verify_and_finish","final_message":"Das Formular ist ausgefüllt."}""";
        }

        return new ChatCompletion { Content = json, Model = "scripted-planner", Usage = new UsageInfo(0, 0, 0m), Latency = TimeSpan.FromMilliseconds(1) };
    }

    public static int IdOf(string prompt, string label) =>
        ElementLine().Matches(prompt).Where(m => m.Groups[3].Value == label).Select(m => int.Parse(m.Groups[1].Value)).First();

    public static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static Dictionary<string, string> ExtractFacts(string text)
    {
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FactLine().Matches(text))
        {
            facts.TryAdd(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        }
        return facts;
    }

    [GeneratedRegex(@"\[(\d+)\] (\w+) ""([^""]*)""")]
    private static partial Regex ElementLine();

    [GeneratedRegex(@"^(Name|E-Mail|Telefon|Firma|Adresse):\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex FactLine();
}

/// <summary>Deterministic stand-in for Jev: lexical similarity between the planned label and the offered choices.</summary>
public sealed partial class LexicalDecisionModel : IDecisionModel
{
    public List<JevRequest> Requests { get; } = [];

    public Task<JevResponse> DecideAsync(JevRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var answers = new Dictionary<string, JevAnswer>();
        foreach (var (key, q) in request.Questions)
        {
            if (q.Type == JevQuestionType.Noul)
            {
                answers[key] = new JevAnswer { Type = JevQuestionType.Noul, Noul = request.Purpose == "verify" ? 0.95 : 0.05 };
                continue;
            }
            var wanted = Label().Match(q.Instructions).Groups[1].Value;
            var best = q.Options!
                .Where(o => o.Key.StartsWith('e'))
                .Select(o => (o.Key, Score: ElementMatcher.TextSimilarity(ElementMatcher.Normalize(wanted), ElementMatcher.Normalize(Quoted().Match(o.Value.What).Groups[1].Value))))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            var choice = best.Score >= 0.5 ? best.Key : TargetResolver.NoneKey;
            answers[key] = new JevAnswer
            {
                Type = JevQuestionType.Choice,
                Choice = choice,
                Confidence = 0.9,
                Probabilities = q.Options!.Keys.ToDictionary(k => k, k => k == choice ? 0.95 : 0.05 / Math.Max(1, q.Options.Count - 1)),
            };
        }
        return Task.FromResult(new JevResponse { Answers = answers, Model = "lexical-decisions" });
    }

    [GeneratedRegex("labelled \"([^\"]*)\"")]
    private static partial Regex Label();

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex Quoted();
}

/// <summary>Approves everything (or denies) and records the requests.</summary>
public sealed class RecordingInteraction : IUserInteraction
{
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.AllowOnce;
    public List<ApprovalRequest> Requests { get; } = [];

    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Decision);
    }

    public Task<string?> AskUserAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

public static class E2E
{
    public static string CreateContactPdf(string directory)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        string[] lines =
        [
            "Kontaktinformationen",
            "Name: Max Muster",
            "E-Mail: max.muster@example.ch",
            "Telefon: +41 79 123 45 67",
            "Firma: Muster AG",
            "Adresse: Bahnhofstrasse 1, 8001 Zuerich, Schweiz",
        ];
        var y = 780.0;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(60, y), font);
            y -= 22;
        }
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Kontaktinformationen.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    public static KairoRuntime CreateRuntime(IUserInteraction interaction, IChatModel? chat, IDecisionModel? decisions, string dataRoot, bool startBridge = false, string? pipeName = null)
    {
        var runtime = new KairoRuntime(interaction, new KairoRuntimeOptions
        {
            Paths = new KairoPaths(Path.Combine(dataRoot, "roaming"), Path.Combine(dataRoot, "local")),
            ChatModelOverride = chat,
            DecisionModelOverride = decisions,
            StartBrowserBridge = startBridge,
            BrowserPipeName = pipeName,
        });
        runtime.Settings.Update(s =>
        {
            s.OnboardingCompleted = true;
            s.Privacy.VisionConsent = VisionConsent.Never;
            s.Control.UseBrowserExtension = startBridge;
        });
        return runtime;
    }

    public static async Task<AgentTask> RunTaskAsync(KairoRuntime runtime, string instruction, Kairo.Core.Models.WindowInfo target, TimeSpan timeout)
    {
        var done = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.Tasks.TaskFinished += (_, t) => done.TrySetResult(t);
        var task = runtime.Tasks.Start(instruction, target);
        var winner = await Task.WhenAny(done.Task, Task.Delay(timeout));
        if (winner != done.Task)
        {
            runtime.Tasks.CancelCurrent("Test timeout");
            throw new TimeoutException($"Task did not finish in time. Status: {task.StatusText}\n{string.Join("\n", task.Log.Select(l => l.Text))}");
        }
        return await done.Task;
    }

    public static string DescribeLog(AgentTask task) =>
        $"{task.State}: {task.ResultMessage ?? task.ErrorMessage}\n" + string.Join("\n", task.Log.Select(l => $"  [{l.Kind}] {l.Text}"));

    public static string? FindEdge()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
            if (key?.GetValue(null) is string path && File.Exists(path)) { return path; }
        }
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static Process StartEdge(string edge, string userDataDir, string url, params string[] extraArgs)
    {
        var args = new List<string>
        {
            $"--user-data-dir=\"{userDataDir}\"", "--no-first-run", "--no-default-browser-check", "--disable-sync",
            "--force-renderer-accessibility", "--disable-features=msEdgeWelcomePage,msSmartScreenProtection,EdgeCollections",
            "--window-size=1100,900", "--new-window",
        };
        args.AddRange(extraArgs);
        args.Add(url);
        return Process.Start(new ProcessStartInfo(edge, string.Join(' ', args)) { UseShellExecute = false })!;
    }

    public static void KillEdge(Process? process, string userDataDir)
    {
        // The test profile directory is unique, so the launched process is the browser main process of this
        // profile – killing its process tree never touches the developer's normal Edge windows.
        Kairo.Tests.Shared.TestSupport.Kill(process);
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(userDataDir, true);
                break;
            }
            catch (Exception) when (i < 9)
            {
                Thread.Sleep(300);
            }
            catch (Exception)
            {
            }
        }
    }
}

/// <summary>Minimal local web server for the test form (serves HTML, records POSTs).</summary>
public sealed class LocalFormServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _html;
    private readonly CancellationTokenSource _cts = new();

    public LocalFormServer(string html)
    {
        _html = html;
        for (var attempt = 0; ; attempt++)
        {
            Port = Random.Shared.Next(20000, 60000);
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
            }
        }
        _ = Task.Run(LoopAsync);
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/form.html";
    public TaskCompletionSource<Dictionary<string, string>> Submission { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }

            var response = context.Response;
            string body;
            if (context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var form = await reader.ReadToEndAsync();
                Submission.TrySetResult(form.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=', 2))
                    .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.ElementAtOrDefault(1) ?? "")));
                body = "<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\"><title>Danke – Muster AG</title></head><body><h1>Vielen Dank!</h1></body></html>";
            }
            else
            {
                body = _html;
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
    }
}
