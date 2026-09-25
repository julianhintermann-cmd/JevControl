using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kairo.Core.Models;
using Kairo.Core.Telemetry;

namespace Kairo.Core.AI.Planning;

/// <summary>
/// Generative planning (OpenRouter chat model). Keeps a compact conversation per task: the system prompt,
/// the initial instruction with files, and short result summaries. Only the newest UI state is sent.
/// </summary>
public sealed class Planner
{
    private readonly IChatModel _chat;
    private readonly KairoLogger _log;
    private readonly TimeProvider _time;

    public Planner(IChatModel chat, KairoLogger log, TimeProvider? time = null)
    {
        _chat = chat;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Creates a new conversation for a task.</summary>
    public PlannerSession StartSession(string model, string? reasoningEffort) => new(this, model, reasoningEffort);

    internal async Task<Plan> SendAsync(PlannerSession session, List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var request = new ChatRequest
        {
            Model = session.Model,
            Messages = messages,
            ResponseSchema = PlannerPrompt.Schema,
            Temperature = 0,
            MaxTokens = 4000,
            ReasoningEffort = session.ReasoningEffort,
            Purpose = "planning",
        };

        var completion = await _chat.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        var plan = PlanParser.Parse(completion.Content);
        if (plan.Warnings.Count > 0)
        {
            _log.Warn("planner", $"plan warnings: {string.Join("; ", plan.Warnings)}");
        }
        _log.Info("planner", $"plan steps={plan.Steps.Count} after={plan.After} ms={completion.Latency.TotalMilliseconds:0}");
        return plan with { Usage = completion.Usage, Latency = completion.Latency };
    }

    internal DateTimeOffset Now => _time.GetLocalNow();
}

/// <summary>Planner conversation of one task.</summary>
public sealed class PlannerSession
{
    private readonly Planner _planner;
    private readonly List<ChatMessage> _history = [];
    private int _rounds;

    internal PlannerSession(Planner planner, string model, string? reasoningEffort)
    {
        _planner = planner;
        Model = model;
        ReasoningEffort = reasoningEffort;
    }

    public string Model { get; }
    public string? ReasoningEffort { get; }
    public int Rounds => _rounds;

    public async Task<Plan> PlanAsync(PlanningInput input, CancellationToken cancellationToken)
    {
        var text = _rounds == 0
            ? PlannerPrompt.BuildInitialMessage(input, _planner.Now)
            : PlannerPrompt.BuildContinuationMessage(input, _planner.Now);

        var parts = new List<ChatContentPart> { ChatContentPart.FromText(text) };
        if (input.Screenshot is { } shot)
        {
            parts.Add(ChatContentPart.FromText($"Screenshot of the active window ({shot.Width}x{shot.Height} px). Coordinates for mouse_click refer to this image."));
            parts.Add(ChatContentPart.FromImage(shot.Data, shot.MimeType));
        }
        var userMessage = new ChatMessage(ChatRole.User, parts);

        var messages = new List<ChatMessage> { ChatMessage.System(PlannerPrompt.SystemPrompt, cache: true) };
        messages.AddRange(CompactHistory());
        messages.Add(userMessage);

        var plan = await _planner.SendAsync(this, messages, cancellationToken).ConfigureAwait(false);

        // Keep a text-only version of the user message in history (screenshots are never re-sent).
        _history.Add(new ChatMessage(ChatRole.User, [ChatContentPart.FromText(StripUiState(text, isFirst: _rounds == 0))]));
        _history.Add(ChatMessage.Assistant(plan.RawJson));
        _rounds++;
        return plan;
    }

    /// <summary>
    /// History without outdated UI states; keeps the first message (instruction + files) and the last 3 rounds.
    /// </summary>
    private IEnumerable<ChatMessage> CompactHistory()
    {
        if (_history.Count <= 8) { return _history; }
        var first = _history.Take(2);
        var recent = _history.Skip(_history.Count - 6);
        return first.Append(ChatMessage.User("(earlier rounds omitted)")).Append(ChatMessage.Assistant("{\"status\":\"…\",\"steps\":[],\"after_steps\":\"replan\"}")).Concat(recent);
    }

    private static string StripUiState(string message, bool isFirst)
    {
        const string marker = "Current UI state:";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) { return message; }
        var end = message.IndexOf("</untrusted_data", start, StringComparison.Ordinal);
        if (end < 0) { return message; }
        end = message.IndexOf('>', end);
        if (end < 0) { return message; }
        return message[..start] + "Current UI state: (outdated – omitted)" + message[(end + 1)..];
    }
}

/// <summary>Tolerant parser for planner JSON (handles code fences, prose around JSON, unknown fields).</summary>
public static class PlanParser
{
    public static Plan Parse(string content)
    {
        var warnings = new List<string>();
        var json = ExtractJson(content);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new PlanParseException($"Der Plan ist kein gültiges JSON: {ex.Message}");
        }

        if (root is not JsonObject obj)
        {
            throw new PlanParseException("Der Plan ist kein JSON-Objekt.");
        }

        var steps = new List<AgentAction>();
        if (obj["steps"] is JsonArray stepArray)
        {
            var index = 0;
            foreach (var node in stepArray)
            {
                index++;
                if (node is not JsonObject s) { continue; }
                var actionName = Str(s["action"]);
                if (!ActionKindExtensions.TryParseWireName(actionName, out var kind))
                {
                    warnings.Add($"Schritt {index}: unbekannte Aktion '{actionName}' ignoriert");
                    continue;
                }

                var action = new AgentAction
                {
                    Kind = kind,
                    TargetId = Int(s["target"]),
                    TargetLabel = Str(s["target_label"]),
                    Value = Str(s["value"]),
                    Option = Str(s["option"]),
                    Checked = Bool(s["checked"]),
                    Keys = Str(s["keys"]),
                    Path = Str(s["path"]),
                    Destination = Str(s["destination"]),
                    Url = Str(s["url"]),
                    App = Str(s["app"]),
                    Direction = Str(s["direction"]),
                    Amount = Int(s["amount"]),
                    X = Int(s["x"]),
                    Y = Int(s["y"]),
                    Description = Str(s["description"]) ?? "",
                    Expectation = Str(s["expect"]),
                };

                if (Validate(action) is { } problem)
                {
                    warnings.Add($"Schritt {index} ({kind.ToWireName()}): {problem}");
                    continue;
                }
                steps.Add(action);
            }
        }

        var after = Str(obj["after_steps"])?.ToLowerInvariant() switch
        {
            "replan" => PlanContinuation.Replan,
            "ask_user" => PlanContinuation.AskUser,
            _ => PlanContinuation.VerifyAndFinish,
        };

        // Informational steps always require another round to use their results.
        if (after == PlanContinuation.VerifyAndFinish && steps.Any(s => s.Kind.IsInformational() || s.Kind == ActionKind.RequestVision))
        {
            after = PlanContinuation.Replan;
        }

        var facts = new List<PlanFact>();
        if (obj["facts"] is JsonArray factArray)
        {
            foreach (var f in factArray.OfType<JsonObject>())
            {
                var key = Str(f["key"]);
                var value = Str(f["value"]);
                if (key is not null && value is not null) { facts.Add(new PlanFact(key, value)); }
            }
        }

        var question = Str(obj["question"]);
        if (after == PlanContinuation.AskUser && string.IsNullOrWhiteSpace(question))
        {
            question = Str(obj["final_message"]) ?? "Mir fehlen Informationen, um fortzufahren. Kannst du die Aufgabe genauer beschreiben?";
        }

        return new Plan
        {
            Status = Str(obj["status"]) ?? "",
            Steps = steps,
            After = after,
            FinalMessage = Str(obj["final_message"]),
            Question = question,
            Facts = facts,
            Warnings = warnings,
            RawJson = obj.ToJsonString(),
        };
    }

    /// <summary>Returns a problem description for actions with missing mandatory fields.</summary>
    public static string? Validate(AgentAction a) => a.Kind switch
    {
        ActionKind.SetValue when a.Value is null => "value fehlt",
        ActionKind.SetValue or ActionKind.Click or ActionKind.Focus or ActionKind.SelectOption or ActionKind.SetChecked
            when a.TargetId is null && string.IsNullOrWhiteSpace(a.TargetLabel) => "target fehlt",
        ActionKind.SelectOption when string.IsNullOrWhiteSpace(a.Option) && string.IsNullOrWhiteSpace(a.Value) => "option fehlt",
        ActionKind.TypeText when string.IsNullOrEmpty(a.Value) => "value fehlt",
        ActionKind.Hotkey when string.IsNullOrWhiteSpace(a.Keys) => "keys fehlt",
        ActionKind.MouseClick when a.X is null || a.Y is null => "Koordinaten fehlen",
        ActionKind.LaunchApp when string.IsNullOrWhiteSpace(a.App) && string.IsNullOrWhiteSpace(a.Path) => "app fehlt",
        ActionKind.OpenFile or ActionKind.ReadFile or ActionKind.CreateFolder or ActionKind.DeleteFile or ActionKind.WriteTextFile
            when string.IsNullOrWhiteSpace(a.Path) => "path fehlt",
        ActionKind.RenameFile or ActionKind.MoveFile or ActionKind.CopyFile
            when string.IsNullOrWhiteSpace(a.Path) || (string.IsNullOrWhiteSpace(a.Destination) && string.IsNullOrWhiteSpace(a.Value)) => "path/destination fehlt",
        ActionKind.OpenUrl when string.IsNullOrWhiteSpace(a.Url) => "url fehlt",
        _ => null,
    };

    internal static string ExtractJson(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline) { text = text[(firstNewline + 1)..lastFence]; }
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static string? Str(JsonNode? node)
    {
        if (node is null) { return null; }
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) { return string.IsNullOrEmpty(s) ? null : s; }
            return v.ToString();
        }
        return node.ToJsonString();
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) { return null; }
        if (v.TryGetValue<int>(out var i)) { return i; }
        if (v.TryGetValue<double>(out var d)) { return (int)Math.Round(d); }
        var s = v.ToString().Trim().TrimStart('[').TrimEnd(']');
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool? Bool(JsonNode? node)
    {
        if (node is not JsonValue v) { return null; }
        if (v.TryGetValue<bool>(out var b)) { return b; }
        var s = v.ToString().Trim().ToLowerInvariant();
        return s is "true" or "ja" or "yes" or "1" ? true : s is "false" or "nein" or "no" or "0" ? false : null;
    }
}

public sealed class PlanParseException(string message) : Exception(message);
