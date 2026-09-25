using System.Net;
using System.Text.Json.Nodes;
using Kairo.Core.AI;
using Kairo.Core.AI.OpenRouter;
using Kairo.Core.AI.Planning;
using Kairo.Core.Telemetry;
using Kairo.Core.Tests.Fakes;

namespace Kairo.Core.Tests;

public class OpenRouterClientTests
{
    private static (OpenRouterClient Client, FakeHttpHandler Handler) Create()
    {
        var handler = new FakeHttpHandler();
        var http = new OpenRouterHttp(new OpenRouterOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) }, () => "sk-or-v1-abcdefabcdefabcdefabcdef", KairoLogger.Null, handler);
        return (new OpenRouterClient(http, new UsageTracker(KairoLogger.Null), KairoLogger.Null), handler);
    }

    private const string ChatOk = """
        {"id":"gen-1","model":"anthropic/claude-sonnet-5","choices":[{"message":{"role":"assistant","content":"{\"status\":\"ok\",\"steps\":[],\"after_steps\":\"verify_and_finish\"}"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":1200,"completion_tokens":40,"total_tokens":1240,"cost":0.0042}}
        """;

    [Fact]
    public async Task Chat_request_uses_json_schema_and_reports_usage()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, ChatOk);

        var result = await client.CompleteAsync(new ChatRequest
        {
            Model = "anthropic/claude-sonnet-5",
            Messages = [ChatMessage.System("sys", cache: true), ChatMessage.User("hi")],
            ResponseSchema = PlannerPrompt.Schema,
            Purpose = "planning",
        }, CancellationToken.None);

        var (request, _) = handler.Requests.Single();
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.ToString());
        Assert.True(request.Headers.Contains("HTTP-Referer"));
        Assert.True(request.Headers.Contains("X-Title"));

        var body = handler.LastJsonBody!;
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("kairo_plan", body["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(body["provider"]!["require_parameters"]!.GetValue<bool>());
        Assert.True(body["usage"]!["include"]!.GetValue<bool>());
        // Anthropic models get a cache breakpoint on the system prompt.
        Assert.Equal("ephemeral", body["messages"]![0]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("hi", body["messages"]![1]!["content"]!.GetValue<string>());

        Assert.Contains("verify_and_finish", result.Content);
        Assert.Equal(1200, result.Usage.InputTokens);
        Assert.Equal(0.0042m, result.Usage.Cost);
    }

    [Fact]
    public async Task Falls_back_to_json_object_when_schema_not_supported()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"No endpoints found that can handle the requested parameters."}}""");
        handler.Enqueue(HttpStatusCode.OK, ChatOk);

        await client.CompleteAsync(new ChatRequest
        {
            Model = "some/model",
            Messages = [ChatMessage.User("x")],
            ResponseSchema = PlannerPrompt.Schema,
        }, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var second = JsonNode.Parse(handler.Requests[1].Body!)!;
        Assert.Equal("json_object", second["response_format"]!["type"]!.GetValue<string>());
        Assert.Null(second["provider"]);

        // The downgrade is remembered for the model.
        handler.Enqueue(HttpStatusCode.OK, ChatOk);
        await client.CompleteAsync(new ChatRequest { Model = "some/model", Messages = [ChatMessage.User("x")], ResponseSchema = PlannerPrompt.Schema }, CancellationToken.None);
        Assert.Equal("json_object", JsonNode.Parse(handler.Requests[2].Body!)!["response_format"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Images_are_sent_as_image_url_parts()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, ChatOk);
        await client.CompleteAsync(new ChatRequest
        {
            Model = "google/gemini-2.5-flash",
            Messages = [new ChatMessage(ChatRole.User, [ChatContentPart.FromText("look"), ChatContentPart.FromImage([1, 2, 3], "image/png")])],
        }, CancellationToken.None);
        var content = handler.LastJsonBody!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image_url", content[1]!["type"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,AQID", content[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task Empty_answer_is_reported()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"choices":[{"message":{"content":""},"finish_reason":"length"}]}""");
        await Assert.ThrowsAsync<OpenRouterException>(() => client.CompleteAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.User("x")] }, CancellationToken.None));
    }

    [Fact]
    public async Task Parses_models_and_key_info()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """
            {"data":[
              {"id":"anthropic/claude-sonnet-5","name":"Claude Sonnet 5","context_length":200000,"pricing":{"prompt":"0.000003","completion":"0.000015"},
               "architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools","response_format","structured_outputs"]},
              {"id":"typesafe/jev-1.13","name":"Jev 1.13","context_length":32000,"pricing":{"prompt":"0.000000042","completion":"0"},"architecture":{"modality":"text->text"}}
            ]}
            """);
        handler.Enqueue(HttpStatusCode.OK, """{"data":{"label":"sk-or-v1-abc...","usage":1.25,"limit":10,"limit_remaining":8.75,"is_free_tier":false}}""");

        var models = await client.ListModelsAsync(CancellationToken.None);
        Assert.Equal(2, models.Count);
        Assert.True(models[0].SupportsImages);
        Assert.True(models[0].SupportsStructuredOutputs);
        Assert.Equal(0.000003m, models[0].PromptPricePerToken);
        Assert.False(models[1].SupportsImages);

        var key = await client.GetKeyInfoAsync(CancellationToken.None);
        Assert.Equal(8.75m, key.LimitRemaining);
        Assert.Equal("https://openrouter.ai/api/v1/key", handler.Requests[1].Request.RequestUri!.ToString());

        var check = ConnectionTester.CheckChatModel("Planer", "anthropic/claude-sonnet-5", models, DefaultModels.PlannerPreference, requireImages: false);
        Assert.True(check.Available);
        var missing = ConnectionTester.CheckChatModel("Vision", "openai/does-not-exist", models, DefaultModels.VisionPreference, requireImages: true);
        Assert.False(missing.Available);
        Assert.Equal("anthropic/claude-sonnet-5", missing.Suggestion);
    }

    [Fact]
    public async Task Only_https_is_allowed()
    {
        var handler = new FakeHttpHandler();
        var http = new OpenRouterHttp(new OpenRouterOptions { ApiBase = new Uri("http://example.com/api/v1/") }, () => "k", KairoLogger.Null, handler);
        var client = new OpenRouterClient(http, new UsageTracker(KairoLogger.Null), KairoLogger.Null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync(new ChatRequest { Model = "m", Messages = [ChatMessage.User("x")] }, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }
}
