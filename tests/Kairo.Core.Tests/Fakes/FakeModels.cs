using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kairo.Core.AI;
using Kairo.Core.AI.Jev;
using Kairo.Core.Perception;

namespace Kairo.Core.Tests.Fakes;

/// <summary>Scripted chat model: a responder builds the plan from the request (e.g. from the UI state and file text).</summary>
public sealed class FakeChatModel : IChatModel
{
    private readonly Func<ChatRequest, int, string> _responder;

    public FakeChatModel(Func<ChatRequest, int, string> responder) => _responder = responder;

    public Kairo.Core.Telemetry.UsageTracker? Usage { get; set; }

    public List<ChatRequest> Requests { get; } = [];

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var content = _responder(request, Requests.Count - 1);
        Usage?.Record(request.Purpose, request.Model, new UsageInfo(1000, 200, 0.002m), TimeSpan.FromMilliseconds(5));
        return Task.FromResult(new ChatCompletion
        {
            Content = content,
            Model = request.Model,
            Usage = new UsageInfo(1000, 200, 0.002m),
            Latency = TimeSpan.FromMilliseconds(5),
        });
    }

    public static string LastUserText(ChatRequest request) => request.Messages[^1].TextContent;
}

/// <summary>
/// Deterministic stand-in for Jev: answers choice questions by lexical similarity between the planned label
/// (from the instructions) and the candidate criteria; noul questions with configurable probabilities.
/// </summary>
public sealed partial class FakeJev : IDecisionModel
{
    public List<JevRequest> Requests { get; } = [];

    public Kairo.Core.Telemetry.UsageTracker? Usage { get; set; }

    public double GoalAchieved { get; set; } = 0.95;
    public double RiskProbability { get; set; } = 0.1;
    public bool Fail { get; set; }
    /// <summary>Overrides: planned label → criterion key to choose.</summary>
    public Dictionary<string, string> ForcedChoices { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<JevResponse> DecideAsync(JevRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (Fail) { throw new Kairo.Core.AI.OpenRouter.OpenRouterException(System.Net.HttpStatusCode.ServiceUnavailable, "Jev down"); }

        var answers = new Dictionary<string, JevAnswer>();
        foreach (var (key, q) in request.Questions)
        {
            switch (q.Type)
            {
                case JevQuestionType.Noul:
                    var p = request.Purpose switch
                    {
                        "verify" => GoalAchieved,
                        "risk" => RiskProbability,
                        _ => 0.5,
                    };
                    answers[key] = new JevAnswer { Type = JevQuestionType.Noul, Noul = p };
                    break;
                case JevQuestionType.Choice:
                    answers[key] = Choose(q);
                    break;
                default:
                    answers[key] = new JevAnswer { Type = JevQuestionType.Score, Score = 0, Confidence = 1 };
                    break;
            }
        }
        Usage?.Record(request.Purpose, "typesafe/jev-fake", new UsageInfo(400, 70, 0.00002m), TimeSpan.FromMilliseconds(3));
        return Task.FromResult(new JevResponse { Answers = answers, Usage = new UsageInfo(400, 70, 0.00002m), Model = "typesafe/jev-fake", Provider = "Fake" });
    }

    private JevAnswer Choose(JevQuestion q)
    {
        var label = LabelPattern().Match(q.Instructions).Groups["l"].Value;
        var options = q.Options!;
        string best;
        if (ForcedChoices.TryGetValue(label, out var forced) && options.ContainsKey(forced))
        {
            best = forced;
        }
        else
        {
            best = options
                .Where(o => o.Key.StartsWith('e'))
                .Select(o => (o.Key, Score: Similarity(label, o.Value.What)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Score >= 0.5 ? x.Key : Kairo.Core.Agent.TargetResolver.NoneKey)
                .FirstOrDefault() ?? Kairo.Core.Agent.TargetResolver.NoneKey;
        }

        var probs = options.Keys.ToDictionary(k => k, k => k == best ? 0.94 : 0.06 / Math.Max(1, options.Count - 1));
        return new JevAnswer { Type = JevQuestionType.Choice, Choice = best, Probabilities = probs, Confidence = 0.9 };
    }

    private static double Similarity(string label, string criterion)
    {
        var m = CriterionLabel().Match(criterion);
        var candidateLabel = m.Success ? m.Groups["l"].Value : criterion;
        return ElementMatcher.TextSimilarity(ElementMatcher.Normalize(label), ElementMatcher.Normalize(candidateLabel));
    }

    [GeneratedRegex("labelled \"(?<l>[^\"]*)\"")]
    private static partial Regex LabelPattern();

    [GeneratedRegex("\"(?<l>[^\"]*)\"")]
    private static partial Regex CriterionLabel();
}
