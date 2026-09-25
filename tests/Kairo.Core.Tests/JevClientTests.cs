using System.Net;
using System.Text.Json.Nodes;
using Kairo.Core.AI;
using Kairo.Core.AI.Jev;
using Kairo.Core.AI.OpenRouter;
using Kairo.Core.Telemetry;
using Kairo.Core.Tests.Fakes;

namespace Kairo.Core.Tests;

public class JevClientTests
{
    private const string TestKey = "sk-or-v1-0123456789abcdef0123456789abcdef";

    private static (JevClient Client, FakeHttpHandler Handler, UsageTracker Usage) Create()
    {
        var handler = new FakeHttpHandler();
        var http = new OpenRouterHttp(new OpenRouterOptions { RetryBaseDelay = TimeSpan.FromMilliseconds(1) }, () => TestKey, KairoLogger.Null, handler);
        var usage = new UsageTracker(KairoLogger.Null);
        return (new JevClient(http, usage, KairoLogger.Null), handler, usage);
    }

    private const string SampleResponse = """
        {
          "answers": {
            "urgent": { "type": "noul", "noul": 0.99 },
            "team": { "type": "choice", "choice": "technical", "probabilities": { "billing": 0.29, "sales": 0.0, "technical": 0.71 }, "confidence": 0.56 },
            "frustration": { "type": "score", "score": 1.05, "legend": { "0": "Calm", "1": "Frustrated", "2": "Very angry" }, "probabilities": { "0": 0, "1": 0.95, "2": 0.05 }, "confidence": 0.92 }
          },
          "usage": { "input_tokens": 423, "output_tokens": 70, "cost": 0.000018 },
          "provider": "TypeSafe",
          "model": "typesafe/jev-1.13-20260917"
        }
        """;

    [Fact]
    public async Task Sends_documented_decisions_request_and_parses_all_primitives()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, SampleResponse);

        var response = await client.DecideAsync(new JevRequest
        {
            Model = "~typesafe/jev-latest",
            State = JsonValue.Create("The payment integration has failed for three days.")!,
            Questions = new Dictionary<string, JevQuestion>
            {
                ["urgent"] = JevQuestion.Noul("Does this support ticket express urgency?"),
                ["team"] = JevQuestion.Choice("Which team should own this ticket?", new Dictionary<string, JevCriterion>
                {
                    ["billing"] = new("Payment, charge, or refund issues"),
                    ["technical"] = new("Bugs or integration failures", NotFor: "Pricing", Examples: ["API returns 500"]),
                    ["sales"] = new("Pricing questions or new purchases"),
                }),
                ["frustration"] = JevQuestion.Score("How frustrated is the customer?", ["Calm", "Concerned but civil", "Very angry"]),
            },
        }, CancellationToken.None);

        var (request, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://openrouter.ai/api/alpha/decisions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(TestKey, request.Headers.Authorization.Parameter);

        var json = JsonNode.Parse(body!)!;
        Assert.Equal("~typesafe/jev-latest", json["model"]!.GetValue<string>());
        Assert.Equal("The payment integration has failed for three days.", json["state"]!.GetValue<string>());
        Assert.Equal("noul", json["questions"]!["urgent"]!["type"]!.GetValue<string>());
        Assert.Null(json["questions"]!["urgent"]!["criteria"]);
        Assert.Equal("choice", json["questions"]!["team"]!["type"]!.GetValue<string>());
        Assert.Equal("Payment, charge, or refund issues", json["questions"]!["team"]!["criteria"]!["billing"]!.GetValue<string>());
        Assert.Equal("Bugs or integration failures", json["questions"]!["team"]!["criteria"]!["technical"]!["what"]!.GetValue<string>());
        Assert.Equal("Pricing", json["questions"]!["team"]!["criteria"]!["technical"]!["not_for"]!.GetValue<string>());
        Assert.Equal(3, json["questions"]!["frustration"]!["criteria"]!.AsArray().Count);

        Assert.Equal(0.99, response["urgent"]!.Noul);
        Assert.Equal("technical", response["team"]!.Choice);
        Assert.Equal(0.71, response["team"]!.ProbabilityOf("technical"), 3);
        Assert.Equal(("billing", 0.29), (response["team"]!.RunnerUp().Key, Math.Round(response["team"]!.RunnerUp().Probability, 2)));
        Assert.Equal(ConfidenceBand.Confirm, response["team"]!.Band);
        Assert.Equal(1.05, response["frustration"]!.Score);
        Assert.Equal("Frustrated", response["frustration"]!.Legend!["1"]);
        Assert.Equal(ConfidenceBand.Act, response["urgent"]!.Band);
        Assert.Equal(423, response.Usage.InputTokens);
        Assert.Equal(0.000018m, response.Usage.Cost);
        Assert.Equal("TypeSafe", response.Provider);
    }

    [Fact]
    public async Task Noul_criteria_are_sent_as_true_false_object_and_object_state_is_kept()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"answers":{"q":{"type":"noul","noul":0.2}},"usage":{"input_tokens":10,"output_tokens":1,"cost":0}}""");
        await client.DecideAsync(new JevRequest
        {
            Model = "typesafe/jev-1.13",
            State = new JsonObject { ["customer_question"] = "Refund?", ["excerpts"] = new JsonArray("30 days") },
            Questions = new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul("Contacted before?", "Mentions earlier tickets", "No sign") },
        }, CancellationToken.None);

        var json = handler.LastJsonBody!;
        Assert.Equal("Mentions earlier tickets", json["questions"]!["q"]!["criteria"]!["true"]!.GetValue<string>());
        Assert.Equal("No sign", json["questions"]!["q"]!["criteria"]!["false"]!.GetValue<string>());
        Assert.Equal("Refund?", json["state"]!["customer_question"]!.GetValue<string>());
    }

    [Fact]
    public async Task Large_batches_are_split_and_merged()
    {
        var (client, handler, _) = Create();
        handler.Default = (_, body) =>
        {
            var q = JsonNode.Parse(body!)!["questions"]!.AsObject();
            var answers = new JsonObject();
            foreach (var (key, _) in q) { answers[key] = new JsonObject { ["type"] = "noul", ["noul"] = 0.7 }; }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["answers"] = answers, ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 0, ["cost"] = 0.00001 } }.ToJsonString()),
            };
        };

        var questions = Enumerable.Range(0, 40).ToDictionary(i => $"q{i}", i => JevQuestion.Noul($"Question {i}?"));
        var response = await client.DecideAsync(new JevRequest { Model = "m", State = JsonValue.Create("s")!, Questions = questions }, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.True(JsonNode.Parse(r.Body!)!["questions"]!.AsObject().Count <= JevClient.MaxQuestionsPerRequest));
        Assert.Equal(40, response.Answers.Count);
        Assert.Equal(300, response.Usage.InputTokens);
    }

    [Fact]
    public async Task Retries_transient_errors_then_succeeds()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.TooManyRequests, """{"error":{"code":429,"message":"rate limited"}}""");
        handler.Enqueue(HttpStatusCode.BadGateway, """{"error":{"code":502,"message":"provider error"}}""");
        handler.Enqueue(HttpStatusCode.OK, """{"answers":{"q":{"type":"noul","noul":0.9}}}""");

        var response = await client.DecideAsync(new JevRequest
        {
            Model = "m",
            State = JsonValue.Create("s")!,
            Questions = new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul("?") },
        }, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(0.9, response["q"]!.Noul);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, false)]
    [InlineData(HttpStatusCode.PaymentRequired, false, true)]
    [InlineData(HttpStatusCode.NotFound, false, false)]
    public async Task Non_transient_errors_are_not_retried_and_mapped(HttpStatusCode status, bool auth, bool credits)
    {
        var (client, handler, _) = Create();
        handler.Enqueue(status, "{\"error\":{\"code\":" + (int)status + ",\"message\":\"nope, key sk-or-v1-aaaaaaaaaaaaaaaaaaaaaaaa leaked\"}}");

        var ex = await Assert.ThrowsAsync<OpenRouterException>(() => client.DecideAsync(new JevRequest
        {
            Model = "m",
            State = JsonValue.Create("s")!,
            Questions = new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul("?") },
        }, CancellationToken.None));

        Assert.Single(handler.Requests);
        Assert.Equal(auth, ex.IsAuthError);
        Assert.Equal(credits, ex.IsInsufficientCredits);
        Assert.DoesNotContain("aaaaaaaaaaaaaaaa", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(ex.UserMessage));
    }

    [Fact]
    public async Task Missing_answer_is_an_error()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"answers":{}}""");
        await Assert.ThrowsAsync<OpenRouterException>(() => client.DecideAsync(new JevRequest
        {
            Model = "m",
            State = JsonValue.Create("s")!,
            Questions = new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul("?") },
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_key_fails_without_network_call()
    {
        var handler = new FakeHttpHandler();
        var http = new OpenRouterHttp(new OpenRouterOptions(), () => null, KairoLogger.Null, handler);
        var client = new JevClient(http, new UsageTracker(KairoLogger.Null), KairoLogger.Null);
        var ex = await Assert.ThrowsAsync<OpenRouterException>(() => client.DecideAsync(new JevRequest
        {
            Model = "m",
            State = JsonValue.Create("s")!,
            Questions = new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul("?") },
        }, CancellationToken.None));
        Assert.Equal("missing_key", ex.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Usage_is_recorded_into_the_current_task_metrics()
    {
        var (client, handler, usage) = Create();
        handler.Enqueue(HttpStatusCode.OK, SampleResponse.Replace("\"urgent\"", "\"q\""));
        var metrics = new TaskMetrics();
        using (usage.BeginTask(metrics))
        {
            await client.DecideAsync(new JevRequest
            {
                Model = "m",
                Purpose = "resolve",
                State = JsonValue.Create("s")!,
                Questions = new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul("?") },
            }, CancellationToken.None);
        }
        Assert.Equal(1, metrics.ModelCalls);
        Assert.Equal(1, metrics.DecisionCalls);
        Assert.Equal(0.000018m, metrics.TotalCost);
    }

    [Fact]
    public void Band_uses_distance_from_half_for_noul()
    {
        Assert.Equal(ConfidenceBand.Escalate, new JevAnswer { Type = JevQuestionType.Noul, Noul = 0.55 }.Band);
        Assert.Equal(ConfidenceBand.Confirm, new JevAnswer { Type = JevQuestionType.Noul, Noul = 0.8 }.Band);
        Assert.Equal(ConfidenceBand.Act, new JevAnswer { Type = JevQuestionType.Noul, Noul = 0.02 }.Band);
    }
}
