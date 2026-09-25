using System.Text.Json.Nodes;

namespace Kairo.Core.AI;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>A part of a chat message: text or image.</summary>
public sealed record ChatContentPart
{
    public string? Text { get; init; }
    /// <summary>Data URL (data:image/png;base64,...) for image parts.</summary>
    public string? ImageDataUrl { get; init; }
    /// <summary>Marks the end of a prompt prefix that providers may cache (Anthropic via OpenRouter).</summary>
    public bool CacheBreakpoint { get; init; }

    public static ChatContentPart FromText(string text, bool cacheBreakpoint = false) => new() { Text = text, CacheBreakpoint = cacheBreakpoint };

    public static ChatContentPart FromImage(byte[] data, string mimeType) =>
        new() { ImageDataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(data)}" };
}

public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ChatContentPart> Parts)
{
    public static ChatMessage System(string text, bool cache = false) => new(ChatRole.System, [ChatContentPart.FromText(text, cache)]);
    public static ChatMessage User(string text) => new(ChatRole.User, [ChatContentPart.FromText(text)]);
    public static ChatMessage Assistant(string text) => new(ChatRole.Assistant, [ChatContentPart.FromText(text)]);

    public string TextContent => string.Concat(Parts.Where(p => p.Text is not null).Select(p => p.Text));
}

/// <summary>JSON schema for structured output (OpenRouter response_format json_schema).</summary>
public sealed record JsonSchemaSpec(string Name, JsonObject Schema, bool Strict = true);

public sealed record ChatRequest
{
    public required string Model { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public JsonSchemaSpec? ResponseSchema { get; init; }
    public double? Temperature { get; init; } = 0;
    public int? MaxTokens { get; init; }
    /// <summary>Optional reasoning effort ("low", "medium", "high"); null = model default.</summary>
    public string? ReasoningEffort { get; init; }
    /// <summary>Usage category for cost tracking ("planning", "vision", ...).</summary>
    public string Purpose { get; init; } = "chat";
}

public sealed record UsageInfo(int InputTokens, int OutputTokens, decimal? Cost)
{
    public static readonly UsageInfo Zero = new(0, 0, 0m);

    public UsageInfo Add(UsageInfo other) =>
        new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, (Cost ?? 0m) + (other.Cost ?? 0m));
}

public sealed record ChatCompletion
{
    public required string Content { get; init; }
    public required string Model { get; init; }
    public UsageInfo Usage { get; init; } = UsageInfo.Zero;
    public string? FinishReason { get; init; }
    public TimeSpan Latency { get; init; }
}

public interface IChatModel
{
    Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>Model metadata from GET /models.</summary>
public sealed record ModelInfo
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public int ContextLength { get; init; }
    public decimal? PromptPricePerToken { get; init; }
    public decimal? CompletionPricePerToken { get; init; }
    public bool SupportsImages { get; init; }
    public bool SupportsStructuredOutputs { get; init; }
    public IReadOnlyList<string> SupportedParameters { get; init; } = [];
}

/// <summary>Result of GET /key.</summary>
public sealed record ApiKeyInfo
{
    public string? Label { get; init; }
    public decimal? Usage { get; init; }
    public decimal? Limit { get; init; }
    public decimal? LimitRemaining { get; init; }
    public bool IsFreeTier { get; init; }
}
