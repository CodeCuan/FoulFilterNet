using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FoulFilterNet.SmartCut;

/// <summary>
/// Any OpenAI-compatible chat completions endpoint - llama.cpp's server, vLLM,
/// LM Studio. Ported from <c>llm_inference_local</c> in
/// <c>Legacy/src/ai_helper.py</c>.
/// </summary>
/// <remarks>
/// Carries the Python's self-healing retry, which is worth more than it looks: a
/// local server is routinely running a different quantisation than configuration
/// says, and the only symptom is a 400 saying the model was not found. Rather
/// than fail every hit in the job, ask the server what it is running and use
/// that.
/// </remarks>
public sealed class OpenAiCompatibleTransport : ISmartCutTransport
{
    /// <summary>
    /// Generous on purpose: a cold llama-server loads several gigabytes of weights
    /// into VRAM before it answers the first prompt.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(240);

    /// <summary>Model discovery is a metadata call and should not hang the job.</summary>
    public static readonly TimeSpan ModelDiscoveryTimeout = TimeSpan.FromSeconds(15);

    private const string SystemPrompt = "You are a video editor. Output ONLY JSON.";

    private static readonly string[] RequiredFields = ["reasoning", "start_index", "end_index"];

    private readonly HttpClient _http;
    private readonly Uri _chatUri;
    private readonly Uri _modelsUri;
    private readonly ILogger<OpenAiCompatibleTransport> _logger;

    private string _model;

    public OpenAiCompatibleTransport(
        HttpClient http,
        string url,
        string model,
        ILogger<OpenAiCompatibleTransport> logger
    )
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _chatUri = new Uri(url);
        _modelsUri = ModelsUriFor(url);
        _model = model ?? string.Empty;
        _logger = logger;
    }

    /// <summary>
    /// The server's model list, derived from its completions endpoint the way the
    /// Python derived <c>LOCAL_MODELS_URL</c>.
    /// </summary>
    public static Uri ModelsUriFor(string chatCompletionsUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatCompletionsUrl);

        return new Uri(
            chatCompletionsUrl.Replace("/chat/completions", "/models", StringComparison.Ordinal)
        );
    }

    public async Task<string> CompleteAsync(
        string prompt,
        CancellationToken cancellationToken = default
    )
    {
        // An unset model id is configuration saying "whatever the server has".
        if (string.IsNullOrWhiteSpace(_model))
        {
            _model = await DiscoverModelAsync(cancellationToken) ?? string.Empty;
        }

        var (status, body) = await PostAsync(_model, prompt, cancellationToken);

        if (status == HttpStatusCode.BadRequest && MentionsAMissingModel(body))
        {
            var discovered = await DiscoverModelAsync(cancellationToken);

            if (discovered is not null)
            {
                _model = discovered;

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Local LLM: retrying with the server's own model id {Model}.",
                        _model
                    );
                }
                (status, body) = await PostAsync(discovered, prompt, cancellationToken);
            }
        }

        if (status != HttpStatusCode.OK || body is null)
        {
            _logger.LogError("Local LLM did not answer; last status {Status}.", status);
            return SmartCutResponses.ApiUnavailable;
        }

        return ReadContent(body);
    }

    private static bool MentionsAMissingModel(string? body) =>
        body is not null && body.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private async Task<(HttpStatusCode? Status, string? Body)> PostAsync(
        string model,
        string prompt,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var content = new StringContent(
                BuildPayload(model, prompt),
                Encoding.UTF8,
                "application/json"
            );

            using var response = await _http.PostAsync(_chatUri, content, cancellationToken);

            return (
                response.StatusCode,
                await response.Content.ReadAsStringAsync(cancellationToken)
            );
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogWarning(ex, "Local LLM at {Url} could not be reached.", _chatUri);
            return (null, null);
        }
    }

    private async Task<string?> DiscoverModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelDiscoveryTimeout);

            using var response = await _http.GetAsync(_modelsUri, timeout.Token);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning(
                    "Local LLM model discovery returned {Status}.",
                    (int)response.StatusCode
                );
                return null;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(timeout.Token)
            );

            if (
                document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].TryGetProperty("id", out var id)
                && id.GetString() is { Length: > 0 } modelId
            )
            {
                return modelId;
            }

            _logger.LogWarning("Local LLM at {Url} listed no models.", _modelsUri);
        }
        catch (Exception ex)
            when (ex
                    is HttpRequestException
                        or TaskCanceledException
                        or IOException
                        or JsonException
            )
        {
            _logger.LogError(ex, "Local LLM model discovery failed.");
        }

        return null;
    }

    private static string BuildPayload(string model, string prompt) =>
        JsonSerializer.Serialize(
            new
            {
                model,
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = prompt },
                },
                temperature = 0.1,
                response_format = new
                {
                    type = "json_object",
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            reasoning = new { type = "string" },
                            start_index = new { type = "integer" },
                            end_index = new { type = "integer" },
                        },
                        required = RequiredFields,
                    },
                },
            }
        );

    /// <summary><c>choices[0].message.content</c>, trimmed.</summary>
    private string ReadContent(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            var content = document
                .RootElement.GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return content?.Trim() ?? SmartCutResponses.ApiUnavailable;
        }
        catch (Exception ex)
            when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            _logger.LogError(ex, "Local LLM returned a reply in an unexpected shape.");
            return SmartCutResponses.ApiUnavailable;
        }
    }
}
