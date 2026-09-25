namespace Kairo.Core.AI.OpenRouter;

/// <summary>Static configuration of the OpenRouter integration.</summary>
public sealed record OpenRouterOptions
{
    /// <summary>Base URL of the OpenAI compatible API (chat completions, models, key).</summary>
    public Uri ApiBase { get; init; } = new("https://openrouter.ai/api/v1/");

    /// <summary>Decisions API used by Jev (alpha, separate from chat completions).</summary>
    public Uri DecisionsEndpoint { get; init; } = new("https://openrouter.ai/api/alpha/decisions");

    /// <summary>Sent as HTTP-Referer for OpenRouter app attribution.</summary>
    public string AppUrl { get; init; } = "https://github.com/julianhintermann-cmd/jevcontrol";

    /// <summary>Sent as X-Title / X-OpenRouter-Title.</summary>
    public string AppTitle { get; init; } = "Kairo";

    public TimeSpan ChatTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan DecisionTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Retries for transient failures (429, 5xx, network).</summary>
    public int MaxRetries { get; init; } = 2;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(400);
}

/// <summary>Model defaults. All of them can be changed in the settings.</summary>
public static class DefaultModels
{
    /// <summary>Jev decision model (Decisions API). "~typesafe/jev-latest" always points to the newest Jev.</summary>
    public const string Decision = "~typesafe/jev-latest";

    public static readonly IReadOnlyList<string> DecisionAlternatives = ["~typesafe/jev-latest", "typesafe/jev-1.13"];

    /// <summary>Planner default; availability is verified against /models during onboarding.</summary>
    public const string Planner = "anthropic/claude-sonnet-5";

    public const string Vision = "anthropic/claude-sonnet-5";

    /// <summary>Preference lists used when the configured model is not available for the key.</summary>
    public static readonly IReadOnlyList<string> PlannerPreference =
    [
        "anthropic/claude-sonnet-5",
        "anthropic/claude-opus-5.5",
        "anthropic/claude-sonnet-4.5",
        "anthropic/claude-haiku-4.5",
        "openai/gpt-5.6-terra",
        "google/gemini-2.5-flash",
    ];

    public static readonly IReadOnlyList<string> VisionPreference =
    [
        "anthropic/claude-sonnet-5",
        "anthropic/claude-sonnet-4.5",
        "anthropic/claude-haiku-4.5",
        "google/gemini-2.5-flash",
        "openai/gpt-5.6-terra",
    ];
}
