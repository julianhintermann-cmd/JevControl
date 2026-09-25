using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Kairo.Core.Tests.Fakes;

/// <summary>Records requests and replies with scripted responses.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, string?, HttpResponseMessage>> _responses = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public Func<HttpRequestMessage, string?, HttpResponseMessage>? Default { get; set; }

    public FakeHttpHandler Enqueue(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        _responses.Enqueue((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is { } ra) { response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra); }
            return response;
        });
        return this;
    }

    public FakeHttpHandler Enqueue(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    public JsonNode? LastJsonBody => Requests.Count == 0 || Requests[^1].Body is null ? null : JsonNode.Parse(Requests[^1].Body!);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        if (_responses.Count > 0) { return _responses.Dequeue()(request, body); }
        if (Default is not null) { return Default(request, body); }
        return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":{\"message\":\"no scripted response\"}}") };
    }
}
