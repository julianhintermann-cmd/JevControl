using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kairo.Core.Telemetry;

namespace Kairo.Core.AI.OpenRouter;

/// <summary>Error returned by OpenRouter (never contains the API key).</summary>
public sealed class OpenRouterException : Exception
{
    public OpenRouterException(HttpStatusCode? statusCode, string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ErrorCode { get; }
    public TimeSpan? RetryAfter { get; set; }

    public bool IsAuthError => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    public bool IsInsufficientCredits => StatusCode == HttpStatusCode.PaymentRequired;
    public bool IsModelNotFound => StatusCode == HttpStatusCode.NotFound;
    public bool IsTransient => StatusCode is null or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
                               (int?)StatusCode >= 500;

    /// <summary>German, user facing explanation.</summary>
    public string UserMessage => StatusCode switch
    {
        HttpStatusCode.Unauthorized => "Der OpenRouter-API-Schlüssel ist ungültig oder wurde widerrufen.",
        HttpStatusCode.Forbidden => "OpenRouter hat die Anfrage abgelehnt (fehlende Berechtigung oder Moderation).",
        HttpStatusCode.PaymentRequired => "Das OpenRouter-Guthaben reicht nicht aus.",
        HttpStatusCode.NotFound => "Das gewählte Modell ist bei OpenRouter nicht verfügbar.",
        HttpStatusCode.RequestEntityTooLarge => "Die Anfrage war zu groß für das Modell.",
        HttpStatusCode.TooManyRequests => "OpenRouter-Ratenlimit erreicht. Bitte kurz warten.",
        HttpStatusCode.BadRequest => "OpenRouter hat die Anfrage als ungültig abgelehnt.",
        null => "OpenRouter ist nicht erreichbar. Bitte Internetverbindung prüfen.",
        _ when (int)StatusCode >= 500 => "OpenRouter oder der Modellanbieter meldet einen Serverfehler.",
        _ => "Unerwarteter Fehler bei OpenRouter.",
    };
}

/// <summary>
/// Shared HTTPS transport for OpenRouter with timeouts, retries (429/5xx/network) and key handling.
/// The API key is read from the provider for every request and only ever placed in the Authorization header.
/// </summary>
public sealed class OpenRouterHttp : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Func<string?> _apiKeyProvider;
    private readonly KairoLogger _log;

    public OpenRouterHttp(OpenRouterOptions options, Func<string?> apiKeyProvider, KairoLogger log, HttpMessageHandler? handler = null)
    {
        Options = options;
        _apiKeyProvider = apiKeyProvider;
        _log = log;
        if (handler is null)
        {
            handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                SslOptions = { EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 },
            };
            _ownsClient = true;
        }

        _http = new HttpClient(handler, disposeHandler: _ownsClient)
        {
            Timeout = Timeout.InfiniteTimeSpan, // per request timeouts below
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Kairo/1.0 (+Windows)");
    }

    public OpenRouterOptions Options { get; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_apiKeyProvider());

    /// <summary>Pre-opens the TLS connection so the first real request is fast (called when the overlay opens).</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Options.ApiBase);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var _ = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Warm-up is best effort only.
        }
    }

    public Task<JsonNode> GetJsonAsync(Uri uri, TimeSpan timeout, bool requireKey, string operation, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Get, uri, body: null, timeout, requireKey, operation, cancellationToken);

    public Task<JsonNode> PostJsonAsync(Uri uri, JsonNode body, TimeSpan timeout, string operation, CancellationToken cancellationToken) =>
        SendJsonAsync(HttpMethod.Post, uri, body, timeout, requireKey: true, operation, cancellationToken);

    private async Task<JsonNode> SendJsonAsync(HttpMethod method, Uri uri, JsonNode? body, TimeSpan timeout, bool requireKey, string operation, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
        {
            throw new InvalidOperationException("OpenRouter requests must use HTTPS.");
        }

        var payload = body?.ToJsonString();
        Exception? lastError = null;
        for (var attempt = 0; attempt <= Options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0)
            {
                var delay = TimeSpan.FromMilliseconds(Options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 150));
                if (lastError is OpenRouterException { RetryAfter: { } retryAfter } && retryAfter < TimeSpan.FromSeconds(10))
                {
                    delay = retryAfter;
                }
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            using var request = new HttpRequestMessage(method, uri);
            var key = _apiKeyProvider();
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            }
            else if (requireKey)
            {
                throw new OpenRouterException(HttpStatusCode.Unauthorized, "Es ist kein OpenRouter-API-Schlüssel hinterlegt.", "missing_key");
            }

            request.Headers.TryAddWithoutValidation("HTTP-Referer", Options.AppUrl);
            request.Headers.TryAddWithoutValidation("X-Title", Options.AppTitle);
            request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", Options.AppTitle);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (payload is not null)
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                sw.Stop();
                _log.Info("openrouter", $"{operation} status={(int)response.StatusCode} ms={sw.ElapsedMilliseconds} attempt={attempt + 1}");

                if (response.IsSuccessStatusCode)
                {
                    JsonNode? node;
                    try
                    {
                        node = JsonNode.Parse(text);
                    }
                    catch (JsonException ex)
                    {
                        throw new OpenRouterException(response.StatusCode, "OpenRouter lieferte keine gültige JSON-Antwort.", "invalid_json", ex);
                    }

                    // OpenRouter may return 200 with an error object (e.g. provider errors mid-stream).
                    if (node?["error"] is JsonObject embeddedError && node["choices"] is null && node["answers"] is null && node["data"] is null)
                    {
                        var code = embeddedError["code"]?.ToString();
                        var status = int.TryParse(code, out var s) ? (HttpStatusCode)s : HttpStatusCode.BadGateway;
                        var ex = new OpenRouterException(status, SanitizeMessage(embeddedError["message"]?.ToString()), code);
                        if (ex.IsTransient && attempt < Options.MaxRetries) { lastError = ex; continue; }
                        throw ex;
                    }

                    return node ?? new JsonObject();
                }

                var error = ParseError(response.StatusCode, text);
                if (response.Headers.RetryAfter?.Delta is { } ra) { error.RetryAfter = ra; }
                if (error.IsTransient && attempt < Options.MaxRetries)
                {
                    lastError = error;
                    continue;
                }
                throw error;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warn("openrouter", $"{operation} timeout after {sw.ElapsedMilliseconds} ms (attempt {attempt + 1})");
                lastError = new OpenRouterException(HttpStatusCode.RequestTimeout, $"Zeitüberschreitung bei {operation}.", "timeout");
            }
            catch (HttpRequestException ex)
            {
                _log.Warn("openrouter", $"{operation} network error: {ex.HttpRequestError}");
                lastError = new OpenRouterException(null, "Netzwerkfehler bei der Verbindung zu OpenRouter.", "network", ex);
            }
        }

        throw lastError ?? new OpenRouterException(null, "Unbekannter Fehler.");
    }

    private static OpenRouterException ParseError(HttpStatusCode status, string body)
    {
        string? message = null;
        string? code = null;
        try
        {
            var node = JsonNode.Parse(body);
            var err = node?["error"];
            message = err?["message"]?.ToString() ?? err?.ToString();
            code = err?["code"]?.ToString();
        }
        catch (JsonException)
        {
            message = body.Length > 300 ? body[..300] : body;
        }

        return new OpenRouterException(status, $"HTTP {(int)status}: {SanitizeMessage(message)}", code);
    }

    /// <summary>Makes sure no key-like material from an error message ends up in logs or UI.</summary>
    private static string SanitizeMessage(string? message) => Redactor.Redact(message ?? "Unbekannter Fehler");

    public void Dispose()
    {
        if (_ownsClient) { _http.Dispose(); }
    }
}
