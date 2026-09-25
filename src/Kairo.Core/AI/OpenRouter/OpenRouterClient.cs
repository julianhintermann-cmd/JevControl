using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Kairo.Core.Telemetry;

namespace Kairo.Core.AI.OpenRouter;

/// <summary>
/// OpenRouter chat completions (OpenAI compatible), model listing and key validation.
/// Structured output is requested via response_format=json_schema; when a model or its providers
/// do not support it, the client falls back to json_object and finally to plain prompting.
/// </summary>
public sealed class OpenRouterClient : IChatModel
{
    internal enum StructuredMode { JsonSchema, JsonObject, PromptOnly }

    private readonly OpenRouterHttp _http;
    private readonly UsageTracker _usage;
    private readonly KairoLogger _log;
    private readonly ConcurrentDictionary<string, StructuredMode> _structuredModeByModel = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ModelInfo>? _modelCache;
    private DateTimeOffset _modelCacheTime;

    public OpenRouterClient(OpenRouterHttp http, UsageTracker usage, KairoLogger log)
    {
        _http = http;
        _usage = usage;
        _log = log;
    }

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        var mode = request.ResponseSchema is null
            ? StructuredMode.PromptOnly
            : _structuredModeByModel.GetValueOrDefault(request.Model, StructuredMode.JsonSchema);

        while (true)
        {
            try
            {
                var result = await SendAsync(request, mode, cancellationToken).ConfigureAwait(false);
                if (request.ResponseSchema is not null) { _structuredModeByModel[request.Model] = mode; }
                return result;
            }
            catch (OpenRouterException ex) when (request.ResponseSchema is not null && mode != StructuredMode.PromptOnly &&
                                                  ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound &&
                                                  LooksLikeUnsupportedParameter(ex))
            {
                mode = mode == StructuredMode.JsonSchema ? StructuredMode.JsonObject : StructuredMode.PromptOnly;
                _log.Warn("openrouter", $"structured output not supported by {request.Model}, falling back to {mode}");
            }
        }
    }

    private static bool LooksLikeUnsupportedParameter(OpenRouterException ex)
    {
        var m = ex.Message.ToLowerInvariant();
        return m.Contains("response_format") || m.Contains("json_schema") || m.Contains("structured") ||
               m.Contains("no endpoints found") || m.Contains("parameter") || m.Contains("not support");
    }

    private async Task<ChatCompletion> SendAsync(ChatRequest request, StructuredMode mode, CancellationToken cancellationToken)
    {
        var body = BuildRequestBody(request, mode);
        var sw = Stopwatch.StartNew();
        var uri = new Uri(_http.Options.ApiBase, "chat/completions");
        var json = await _http.PostJsonAsync(uri, body, _http.Options.ChatTimeout, $"chat:{request.Purpose}", cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var choice = json["choices"]?[0];
        var content = ExtractContent(choice?["message"]?["content"]);
        var usage = ParseUsage(json["usage"], "prompt_tokens", "completion_tokens");
        var model = json["model"]?.GetValue<string>() ?? request.Model;
        _usage.Record(request.Purpose, model, usage, sw.Elapsed);

        if (string.IsNullOrWhiteSpace(content))
        {
            var finish = choice?["finish_reason"]?.ToString();
            throw new OpenRouterException(HttpStatusCode.BadGateway, $"Das Modell lieferte eine leere Antwort (finish_reason={finish}).", "empty_response");
        }

        return new ChatCompletion
        {
            Content = content,
            Model = model,
            Usage = usage,
            FinishReason = choice?["finish_reason"]?.ToString(),
            Latency = sw.Elapsed,
        };
    }

    internal static JsonObject BuildRequestBody(ChatRequest request, StructuredMode mode)
    {
        var isAnthropic = request.Model.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase);
        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var role = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.Assistant => "assistant",
                _ => "user",
            };

            var simple = message.Parts.Count == 1 && message.Parts[0].ImageDataUrl is null && !(isAnthropic && message.Parts[0].CacheBreakpoint);
            if (simple)
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = message.Parts[0].Text ?? "" });
                continue;
            }

            var parts = new JsonArray();
            foreach (var part in message.Parts)
            {
                if (part.ImageDataUrl is not null)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = part.ImageDataUrl, ["detail"] = "high" },
                    });
                }
                else
                {
                    var textPart = new JsonObject { ["type"] = "text", ["text"] = part.Text ?? "" };
                    if (part.CacheBreakpoint && isAnthropic)
                    {
                        textPart["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                    }
                    parts.Add(textPart);
                }
            }
            messages.Add(new JsonObject { ["role"] = role, ["content"] = parts });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["usage"] = new JsonObject { ["include"] = true },
        };
        if (request.Temperature is { } t) { body["temperature"] = t; }
        if (request.MaxTokens is { } max) { body["max_tokens"] = max; }
        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            body["reasoning"] = new JsonObject { ["effort"] = request.ReasoningEffort, ["exclude"] = true };
        }

        if (request.ResponseSchema is { } schema)
        {
            switch (mode)
            {
                case StructuredMode.JsonSchema:
                    body["response_format"] = new JsonObject
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = new JsonObject
                        {
                            ["name"] = schema.Name,
                            ["strict"] = schema.Strict,
                            ["schema"] = schema.Schema.DeepClone(),
                        },
                    };
                    body["provider"] = new JsonObject { ["require_parameters"] = true };
                    break;
                case StructuredMode.JsonObject:
                    body["response_format"] = new JsonObject { ["type"] = "json_object" };
                    break;
            }
        }

        return body;
    }

    private static string ExtractContent(JsonNode? content)
    {
        switch (content)
        {
            case null:
                return "";
            case JsonValue v:
                return v.ToString();
            case JsonArray parts:
                return string.Concat(parts.Select(p => p?["text"]?.ToString() ?? ""));
            default:
                return content.ToJsonString();
        }
    }

    internal static UsageInfo ParseUsage(JsonNode? usage, string inputKey, string outputKey)
    {
        if (usage is null) { return UsageInfo.Zero; }
        var input = TryInt(usage[inputKey]);
        var output = TryInt(usage[outputKey]);
        decimal? cost = null;
        if (usage["cost"] is JsonValue costValue && decimal.TryParse(costValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c))
        {
            cost = c;
        }
        return new UsageInfo(input, output, cost);
    }

    private static int TryInt(JsonNode? node) =>
        node is JsonValue v && int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;

    /// <summary>GET /models (public, cached for 10 minutes).</summary>
    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!forceRefresh && _modelCache is not null && DateTimeOffset.UtcNow - _modelCacheTime < TimeSpan.FromMinutes(10))
        {
            return _modelCache;
        }

        var json = await _http.GetJsonAsync(new Uri(_http.Options.ApiBase, "models"), _http.Options.MetadataTimeout, requireKey: false, "models", cancellationToken).ConfigureAwait(false);
        var list = new List<ModelInfo>();
        if (json["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                if (item?["id"]?.ToString() is not { Length: > 0 } id) { continue; }
                var inputModalities = item["architecture"]?["input_modalities"] as JsonArray;
                var modality = item["architecture"]?["modality"]?.ToString() ?? "";
                var supported = (item["supported_parameters"] as JsonArray)?.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0).ToList() ?? [];
                list.Add(new ModelInfo
                {
                    Id = id,
                    Name = item["name"]?.ToString() ?? id,
                    ContextLength = TryInt(item["context_length"]),
                    PromptPricePerToken = ParseDecimal(item["pricing"]?["prompt"]),
                    CompletionPricePerToken = ParseDecimal(item["pricing"]?["completion"]),
                    SupportsImages = inputModalities?.Any(m => m?.ToString() == "image") == true || modality.Contains("image", StringComparison.OrdinalIgnoreCase),
                    SupportsStructuredOutputs = supported.Contains("structured_outputs") || supported.Contains("response_format"),
                    SupportedParameters = supported,
                });
            }
        }

        _modelCache = list;
        _modelCacheTime = DateTimeOffset.UtcNow;
        return list;
    }

    private static decimal? ParseDecimal(JsonNode? node) =>
        node is not null && decimal.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>GET /key – validates the API key and returns credit information.</summary>
    public async Task<ApiKeyInfo> GetKeyInfoAsync(CancellationToken cancellationToken)
    {
        var json = await _http.GetJsonAsync(new Uri(_http.Options.ApiBase, "key"), _http.Options.MetadataTimeout, requireKey: true, "key", cancellationToken).ConfigureAwait(false);
        var data = json["data"] ?? json;
        return new ApiKeyInfo
        {
            Label = data["label"]?.ToString(),
            Usage = ParseDecimal(data["usage"]),
            Limit = ParseDecimal(data["limit"]),
            LimitRemaining = ParseDecimal(data["limit_remaining"]),
            IsFreeTier = data["is_free_tier"]?.ToString() == "true",
        };
    }
}
