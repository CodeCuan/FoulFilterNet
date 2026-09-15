using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FoulFilterNet.SmartCut;

/// <summary>
/// Google Gemini over its REST API. Ported from <c>llm_inference_google</c> in
/// <c>Legacy/src/ai_helper.py</c>, which used the google-genai SDK; the request
/// this builds is the same one that SDK would have sent.
/// </summary>
/// <remarks>
/// Every safety category is set to BLOCK_NONE. That is not carelessness: the
/// prompt is a list of profanities and the model's job is to decide where to cut
/// them. With the defaults in place Gemini refuses the request outright, which
/// this class would report as a refusal and the pipeline would answer by leaving
/// every hit exactly where the matcher put it.
/// </remarks>
public sealed class GeminiTransport : ISmartCutTransport
{
    /// <summary>Attempts per prompt, including the first. Matches the Python's <c>retries=3</c>.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Where the key goes. Never the query string - URLs end up in logs and proxies.</summary>
    public const string ApiKeyHeader = "x-goog-api-key";

    private const string EndpointPrefix = "https://generativelanguage.googleapis.com/v1beta/models/";
    private const string EndpointSuffix = ":generateContent";

    private static readonly string[] SafetyCategories =
    [
        "HARM_CATEGORY_HATE_SPEECH",
        "HARM_CATEGORY_SEXUALLY_EXPLICIT",
        "HARM_CATEGORY_HARASSMENT",
        "HARM_CATEGORY_DANGEROUS_CONTENT",
    ];

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Uri _endpoint;
    private readonly ILogger<GeminiTransport> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">
    /// How to wait between retries. Injected so the backoff can be asserted
    /// without a test suite that takes three seconds to prove it.
    /// </param>
    public GeminiTransport(
        HttpClient http,
        string apiKey,
        string model,
        ILogger<GeminiTransport> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _apiKey = apiKey;
        _endpoint = new Uri(EndpointPrefix + model + EndpointSuffix);
        _logger = logger;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var payload = BuildPayload(prompt);

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            HttpStatusCode status;
            string body;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                };
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, _apiKey);

                using var response = await _http.SendAsync(request, cancellationToken);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // The Python's exception branch only retried when the message
                // mentioned 429, 503 or "demand"; a dead connection fell straight
                // through to API_UNAVAILABLE.
                _logger.LogWarning(ex, "Gemini could not be reached.");
                return SmartCutResponses.ApiUnavailable;
            }

            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == MaxAttempts - 1)
                {
                    break;
                }

                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(
                    "Gemini busy ({Status}), attempt {Attempt} of {MaxAttempts}; waiting {Wait}.",
                    (int)status,
                    attempt + 1,
                    MaxAttempts,
                    wait);

                await _delay(wait, cancellationToken);
                continue;
            }

            if (status is not HttpStatusCode.OK)
            {
                _logger.LogError("Gemini returned {Status}.", (int)status);
                break;
            }

            return ReadFirstPart(body);
        }

        return SmartCutResponses.ApiUnavailable;
    }

    private static string BuildPayload(string prompt) => JsonSerializer.Serialize(new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } },
        generationConfig = new
        {
            temperature = 0.1,
            maxOutputTokens = 300,
            responseMimeType = "application/json",
        },
        safetySettings = Array.ConvertAll(
            SafetyCategories,
            category => new { category, threshold = "BLOCK_NONE" }),
    });

    /// <summary>
    /// <c>candidates[0].content.parts[0].text</c>. Anything else - no candidates,
    /// no parts - is the shape a safety block arrives in, and is a refusal rather
    /// than an outage.
    /// </summary>
    private static string ReadFirstPart(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array
                && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var content)
                && content.TryGetProperty("parts", out var parts)
                && parts.ValueKind == JsonValueKind.Array
                && parts.GetArrayLength() > 0
                && parts[0].TryGetProperty("text", out var text)
                && text.GetString() is { } answer)
            {
                return answer.Trim();
            }
        }
        catch (JsonException)
        {
            return SmartCutResponses.ErrorOrRefusal;
        }

        return SmartCutResponses.ErrorOrRefusal;
    }
}
