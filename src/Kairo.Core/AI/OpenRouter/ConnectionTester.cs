using Kairo.Core.AI.Jev;

namespace Kairo.Core.AI.OpenRouter;

public sealed record ModelCheck(string Role, string ModelId, bool Available, string Message, string? Suggestion = null);

public sealed record ConnectionReport
{
    public bool KeyValid { get; init; }
    public string KeyMessage { get; init; } = "";
    public ApiKeyInfo? KeyInfo { get; init; }
    public IReadOnlyList<ModelCheck> Models { get; init; } = [];
    public bool AllAvailable => KeyValid && Models.All(m => m.Available);
}

/// <summary>
/// Onboarding / settings connection test: validates the key (GET /key), checks that the planner and vision
/// models exist for the account (GET /models) and that Jev really answers (one tiny Decisions request).
/// </summary>
public sealed class ConnectionTester
{
    private readonly OpenRouterClient _client;
    private readonly JevClient _jev;

    public ConnectionTester(OpenRouterClient client, JevClient jev)
    {
        _client = client;
        _jev = jev;
    }

    public async Task<ConnectionReport> TestAsync(string plannerModel, string decisionModel, string? visionModel, CancellationToken cancellationToken)
    {
        ApiKeyInfo? info;
        try
        {
            info = await _client.GetKeyInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OpenRouterException ex)
        {
            return new ConnectionReport { KeyValid = false, KeyMessage = ex.UserMessage };
        }

        var keyMessage = info.LimitRemaining is { } remaining
            ? $"Schlüssel gültig. Verbleibendes Limit: ${remaining:0.00}."
            : $"Schlüssel gültig. Bisherige Nutzung: ${info.Usage ?? 0:0.00}.";

        var checks = new List<ModelCheck>();
        IReadOnlyList<ModelInfo> models = [];
        try
        {
            models = await _client.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OpenRouterException ex)
        {
            checks.Add(new ModelCheck("Modellliste", "-", false, ex.UserMessage));
        }

        if (models.Count > 0)
        {
            checks.Add(CheckChatModel("Planungsmodell", plannerModel, models, DefaultModels.PlannerPreference, requireImages: false));
            if (!string.IsNullOrWhiteSpace(visionModel))
            {
                checks.Add(CheckChatModel("Vision-Modell", visionModel, models, DefaultModels.VisionPreference, requireImages: true));
            }
        }

        var (ok, message, _) = await _jev.ProbeAsync(decisionModel, cancellationToken).ConfigureAwait(false);
        checks.Add(new ModelCheck("Entscheidungsmodell (Jev)", decisionModel, ok, message,
            ok ? null : DefaultModels.DecisionAlternatives.FirstOrDefault(m => m != decisionModel)));

        return new ConnectionReport { KeyValid = true, KeyMessage = keyMessage, KeyInfo = info, Models = checks };
    }

    internal static ModelCheck CheckChatModel(string role, string modelId, IReadOnlyList<ModelInfo> models, IReadOnlyList<string> preference, bool requireImages)
    {
        var model = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        string? Suggest() => preference.FirstOrDefault(p => models.Any(m => string.Equals(m.Id, p, StringComparison.OrdinalIgnoreCase) && (!requireImages || m.SupportsImages)));

        if (model is null)
        {
            return new ModelCheck(role, modelId, false, "Modell ist bei OpenRouter nicht verfügbar.", Suggest());
        }
        if (requireImages && !model.SupportsImages)
        {
            return new ModelCheck(role, modelId, false, "Modell unterstützt keine Bilder.", Suggest());
        }

        var price = model.PromptPricePerToken is { } p && model.CompletionPricePerToken is { } c
            ? $" (${p * 1_000_000:0.##}/${c * 1_000_000:0.##} pro 1 Mio. Tokens)"
            : "";
        var structured = model.SupportsStructuredOutputs ? ", strukturierte Ausgabe" : "";
        return new ModelCheck(role, modelId, true, $"Verfügbar{structured}{price}.");
    }
}
