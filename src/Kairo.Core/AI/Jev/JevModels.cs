using System.Text.Json.Nodes;

namespace Kairo.Core.AI.Jev;

/// <summary>Jev question primitives (TypeSafe "System One").</summary>
public enum JevQuestionType
{
    /// <summary>Yes/no: returns P(yes) in [0,1]. 0.5 means "can't tell".</summary>
    Noul,
    /// <summary>Pick one label: returns the winning key, a probability per key and a confidence.</summary>
    Choice,
    /// <summary>Position on an ordered rubric: probability-weighted mean of level indexes.</summary>
    Score,
}

/// <summary>A structured choice criterion (what / not_for / examples) for options that are easy to confuse.</summary>
public sealed record JevCriterion(string What, string? NotFor = null, IReadOnlyList<string>? Examples = null)
{
    public JsonNode ToJson()
    {
        if (NotFor is null && (Examples is null || Examples.Count == 0))
        {
            return JsonValue.Create(What)!;
        }

        var obj = new JsonObject { ["what"] = What };
        if (NotFor is not null) { obj["not_for"] = NotFor; }
        if (Examples is { Count: > 0 }) { obj["examples"] = new JsonArray(Examples.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()); }
        return obj;
    }
}

/// <summary>One typed question of a Decisions API request.</summary>
public sealed record JevQuestion
{
    public required JevQuestionType Type { get; init; }
    public required string Instructions { get; init; }
    /// <summary>choice: key → criterion. noul: optional "true"/"false" criteria.</summary>
    public IReadOnlyDictionary<string, JevCriterion>? Options { get; init; }
    /// <summary>score: ordered levels, index 0 = lowest.</summary>
    public IReadOnlyList<string>? Levels { get; init; }

    public static JevQuestion Noul(string instructions, string? whenTrue = null, string? whenFalse = null)
    {
        Dictionary<string, JevCriterion>? criteria = null;
        if (whenTrue is not null || whenFalse is not null)
        {
            criteria = new Dictionary<string, JevCriterion>
            {
                ["true"] = new(whenTrue ?? "Yes"),
                ["false"] = new(whenFalse ?? "No"),
            };
        }
        return new JevQuestion { Type = JevQuestionType.Noul, Instructions = instructions, Options = criteria };
    }

    public static JevQuestion Choice(string instructions, IReadOnlyDictionary<string, JevCriterion> options) =>
        new() { Type = JevQuestionType.Choice, Instructions = instructions, Options = options };

    public static JevQuestion Score(string instructions, IReadOnlyList<string> levels) =>
        new() { Type = JevQuestionType.Score, Instructions = instructions, Levels = levels };

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["type"] = Type switch
            {
                JevQuestionType.Noul => "noul",
                JevQuestionType.Choice => "choice",
                _ => "score",
            },
            ["instructions"] = Instructions,
        };

        if (Type == JevQuestionType.Score && Levels is { Count: > 0 })
        {
            obj["criteria"] = new JsonArray(Levels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
        }
        else if (Options is { Count: > 0 })
        {
            var criteria = new JsonObject();
            foreach (var (key, criterion) in Options)
            {
                criteria[key] = criterion.ToJson();
            }
            obj["criteria"] = criteria;
        }

        return obj;
    }
}

public sealed record JevRequest
{
    public required string Model { get; init; }
    /// <summary>Application state: string, object or array. Objects let instructions refer to fields by name.</summary>
    public required JsonNode State { get; init; }
    public required IReadOnlyDictionary<string, JevQuestion> Questions { get; init; }
    /// <summary>Usage category for cost tracking.</summary>
    public string Purpose { get; init; } = "decision";
}

/// <summary>Typed answer to one question.</summary>
public sealed record JevAnswer
{
    public required JevQuestionType Type { get; init; }
    /// <summary>noul: probability of "yes".</summary>
    public double? Noul { get; init; }
    /// <summary>choice: winning key.</summary>
    public string? Choice { get; init; }
    /// <summary>score: probability weighted mean of level indexes.</summary>
    public double? Score { get; init; }
    public IReadOnlyDictionary<string, double> Probabilities { get; init; } = new Dictionary<string, double>();
    /// <summary>0..1, how peaked the distribution is (choice/score).</summary>
    public double? Confidence { get; init; }
    public IReadOnlyDictionary<string, string>? Legend { get; init; }

    public double ProbabilityOf(string key) => Probabilities.TryGetValue(key, out var p) ? p : 0;

    /// <summary>Returns the runner-up key and probability (choice).</summary>
    public (string? Key, double Probability) RunnerUp()
    {
        string? best = null;
        var bestP = -1.0;
        foreach (var (k, p) in Probabilities)
        {
            if (k == Choice) { continue; }
            if (p > bestP) { best = k; bestP = p; }
        }
        return (best, Math.Max(0, bestP));
    }

    /// <summary>
    /// TypeSafe confidence guidance: &gt; 0.9 act, 0.5–0.9 confirm, &lt; 0.5 escalate.
    /// For noul the distance from 0.5 serves as certainty.
    /// </summary>
    public ConfidenceBand Band
    {
        get
        {
            var c = Type == JevQuestionType.Noul && Noul is { } p
                ? Math.Abs(p - 0.5) * 2
                : Confidence ?? 0;
            return c > 0.9 ? ConfidenceBand.Act : c >= 0.5 ? ConfidenceBand.Confirm : ConfidenceBand.Escalate;
        }
    }
}

public enum ConfidenceBand
{
    Act,
    Confirm,
    Escalate,
}

public sealed record JevResponse
{
    public required IReadOnlyDictionary<string, JevAnswer> Answers { get; init; }
    public UsageInfo Usage { get; init; } = UsageInfo.Zero;
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public TimeSpan Latency { get; init; }

    public JevAnswer? this[string key] => Answers.TryGetValue(key, out var a) ? a : null;
}

/// <summary>Structured decision engine (Jev via OpenRouter Decisions API).</summary>
public interface IDecisionModel
{
    Task<JevResponse> DecideAsync(JevRequest request, CancellationToken cancellationToken);
}
