using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kairo.Core.AI.OpenRouter;
using Kairo.Core.Telemetry;

namespace Kairo.Core.AI.Jev;

/// <summary>
/// Client for TypeSafe Jev via OpenRouter's Decisions API:
/// <c>POST https://openrouter.ai/api/alpha/decisions</c> with <c>{ model, state, questions }</c>.
/// Jev generates no text; it returns typed answers (noul/choice/score) with calibrated probabilities.
/// All questions of one request are evaluated in parallel, so callers should batch them.
/// </summary>
public sealed class JevClient : IDecisionModel
{
    /// <summary>Questions per HTTP request; larger batches are split and sent concurrently.</summary>
    public const int MaxQuestionsPerRequest = 16;

    /// <summary>Soft limit for the serialized state (jev-latest accepts ~32k tokens per request).</summary>
    public const int MaxStateChars = 60_000;

    private readonly OpenRouterHttp _http;
    private readonly UsageTracker _usage;
    private readonly KairoLogger _log;

    public JevClient(OpenRouterHttp http, UsageTracker usage, KairoLogger log)
    {
        _http = http;
        _usage = usage;
        _log = log;
    }

    public async Task<JevResponse> DecideAsync(JevRequest request, CancellationToken cancellationToken)
    {
        if (request.Questions.Count == 0)
        {
            return new JevResponse { Answers = new Dictionary<string, JevAnswer>() };
        }

        if (request.State.ToJsonString().Length > MaxStateChars)
        {
            throw new OpenRouterException(HttpStatusCode.RequestEntityTooLarge, "Der Zustand für Jev ist zu groß.", "state_too_large");
        }

        if (request.Questions.Count <= MaxQuestionsPerRequest)
        {
            return await SendAsync(request, request.Questions, cancellationToken).ConfigureAwait(false);
        }

        // Split into chunks and run them concurrently, then merge.
        var chunks = request.Questions.Chunk(MaxQuestionsPerRequest)
            .Select(chunk => (IReadOnlyDictionary<string, JevQuestion>)chunk.ToDictionary(kv => kv.Key, kv => kv.Value))
            .ToList();
        var responses = await Task.WhenAll(chunks.Select(c => SendAsync(request, c, cancellationToken))).ConfigureAwait(false);
        var answers = new Dictionary<string, JevAnswer>();
        var usage = UsageInfo.Zero;
        foreach (var r in responses)
        {
            foreach (var (k, v) in r.Answers) { answers[k] = v; }
            usage = usage.Add(r.Usage);
        }
        return new JevResponse
        {
            Answers = answers,
            Usage = usage,
            Provider = responses[0].Provider,
            Model = responses[0].Model,
            Latency = responses.Max(r => r.Latency),
        };
    }

    private async Task<JevResponse> SendAsync(JevRequest request, IReadOnlyDictionary<string, JevQuestion> questions, CancellationToken cancellationToken)
    {
        var body = BuildBody(request.Model, request.State, questions);
        var sw = Stopwatch.StartNew();
        var json = await _http.PostJsonAsync(_http.Options.DecisionsEndpoint, body, _http.Options.DecisionTimeout, $"jev:{request.Purpose}", cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var response = ParseResponse(json, sw.Elapsed);
        _usage.Record(request.Purpose, response.Model ?? request.Model, response.Usage, sw.Elapsed);
        _log.Info("jev", $"{request.Purpose} questions={questions.Count} ms={sw.ElapsedMilliseconds} provider={response.Provider}");

        foreach (var key in questions.Keys)
        {
            if (!response.Answers.ContainsKey(key))
            {
                throw new OpenRouterException(HttpStatusCode.BadGateway, $"Jev lieferte keine Antwort für '{key}'.", "missing_answer");
            }
        }

        return response;
    }

    internal static JsonObject BuildBody(string model, JsonNode state, IReadOnlyDictionary<string, JevQuestion> questions)
    {
        var q = new JsonObject();
        foreach (var (key, question) in questions)
        {
            q[key] = question.ToJson();
        }
        return new JsonObject
        {
            ["model"] = model,
            ["state"] = state.DeepClone(),
            ["questions"] = q,
        };
    }

    internal static JevResponse ParseResponse(JsonNode json, TimeSpan latency)
    {
        var answers = new Dictionary<string, JevAnswer>();
        if (json["answers"] is JsonObject answerObj)
        {
            foreach (var (key, node) in answerObj)
            {
                if (node is null) { continue; }
                var typeText = node["type"]?.ToString() ?? "";
                var type = typeText switch
                {
                    "noul" => JevQuestionType.Noul,
                    "choice" => JevQuestionType.Choice,
                    "score" => JevQuestionType.Score,
                    _ => node["noul"] is not null ? JevQuestionType.Noul : node["choice"] is not null ? JevQuestionType.Choice : JevQuestionType.Score,
                };

                var probabilities = new Dictionary<string, double>();
                if (node["probabilities"] is JsonObject probs)
                {
                    foreach (var (pk, pv) in probs)
                    {
                        if (TryDouble(pv) is { } d) { probabilities[pk] = d; }
                    }
                }

                Dictionary<string, string>? legend = null;
                if (node["legend"] is JsonObject leg)
                {
                    legend = leg.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "");
                }

                answers[key] = new JevAnswer
                {
                    Type = type,
                    Noul = TryDouble(node["noul"]),
                    Choice = node["choice"]?.ToString(),
                    Score = TryDouble(node["score"]),
                    Confidence = TryDouble(node["confidence"]),
                    Probabilities = probabilities,
                    Legend = legend,
                };
            }
        }

        return new JevResponse
        {
            Answers = answers,
            Usage = OpenRouterClient.ParseUsage(json["usage"], "input_tokens", "output_tokens"),
            Provider = json["provider"]?.ToString(),
            Model = json["model"]?.ToString(),
            Latency = latency,
        };
    }

    private static double? TryDouble(JsonNode? node) =>
        node is JsonValue v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>
    /// Real availability check for a Jev model: one tiny decision request (cost ≈ $0.00001).
    /// </summary>
    public async Task<(bool Available, string Message, TimeSpan Latency)> ProbeAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            var result = await DecideAsync(new JevRequest
            {
                Model = model,
                Purpose = "probe",
                State = JsonValue.Create("Der Himmel ist an einem wolkenlosen Tag blau.")!,
                Questions = new Dictionary<string, JevQuestion>
                {
                    ["is_statement"] = JevQuestion.Noul("Is this text a factual statement?"),
                },
            }, cancellationToken).ConfigureAwait(false);
            return (true, $"Jev antwortet in {result.Latency.TotalMilliseconds:0} ms ({result.Model ?? model}).", result.Latency);
        }
        catch (OpenRouterException ex)
        {
            return (false, ex.UserMessage, TimeSpan.Zero);
        }
    }
}
