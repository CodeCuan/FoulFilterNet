using System.Net;
using System.Text.Json;
using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.SmartCut.Tests;

internal static class GeminiReplies
{
    public const string Answer =
        """{"candidates":[{"content":{"parts":[{"text":"  {\"start_index\": 4, \"end_index\": 6}\n"}]}}]}""";

    public const string Refusal = """{"candidates":[{"finishReason":"SAFETY","content":{}}]}""";

    public const string Busy = """{"error":{"code":429,"message":"Resource exhausted"}}""";
}

public class WhenGeminiAnswers
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;
    private readonly JsonElement _payload;

    public WhenGeminiAnswers()
    {
        _handler = FakeHttpMessageHandler.Returning((HttpStatusCode.OK, GeminiReplies.Answer));
        var transport = new GeminiTransport(
            _handler.Client(),
            apiKey: "test-key",
            model: "gemini-2.5-flash-lite",
            NullLogger<GeminiTransport>.Instance
        );

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();

        _handler.Requests.Count.ShouldBe(1);
        _payload = JsonDocument.Parse(_handler.Requests[0].Body!).RootElement;
    }

    [Fact]
    public void ReturnsTheModelsTextTrimmed() =>
        _response.ShouldBe("""{"start_index": 4, "end_index": 6}""");

    [Fact]
    public void PostsToTheConfiguredModelsGenerateContentEndpoint() =>
        _handler
            .Requests[0]
            .Uri.ToString()
            .ShouldBe(
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-lite:generateContent"
            );

    [Fact]
    public void SendsTheApiKeyAsAHeader() =>
        _handler.Requests[0].Headers["x-goog-api-key"].ShouldBe("test-key");

    [Fact]
    public void KeepsTheApiKeyOutOfTheUrl() =>
        _handler.Requests[0].Uri.ToString().ShouldNotContain("test-key");

    [Fact]
    public void SendsThePromptAsTheOnlyPart() =>
        _payload
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString()
            .ShouldBe("the prompt");

    [Fact]
    public void AsksForANearlyDeterministicAnswer() =>
        _payload
            .GetProperty("generationConfig")
            .GetProperty("temperature")
            .GetDouble()
            .ShouldBe(0.1, 0.0001);

    [Fact]
    public void CapsTheOutputAtThreeHundredTokens() =>
        _payload
            .GetProperty("generationConfig")
            .GetProperty("maxOutputTokens")
            .GetInt32()
            .ShouldBe(300);

    [Fact]
    public void AsksForJson() =>
        _payload
            .GetProperty("generationConfig")
            .GetProperty("responseMimeType")
            .GetString()
            .ShouldBe("application/json");

    [Fact]
    public void TurnsOffAllFourSafetyCategories()
    {
        var settings = _payload.GetProperty("safetySettings").EnumerateArray().ToList();

        settings
            .Select(setting => setting.GetProperty("category").GetString())
            .ShouldBe([
                "HARM_CATEGORY_HATE_SPEECH",
                "HARM_CATEGORY_SEXUALLY_EXPLICIT",
                "HARM_CATEGORY_HARASSMENT",
                "HARM_CATEGORY_DANGEROUS_CONTENT",
            ]);
    }

    [Fact]
    public void BlocksNothing() =>
        _payload
            .GetProperty("safetySettings")
            .EnumerateArray()
            .ShouldAllBe(setting => setting.GetProperty("threshold").GetString() == "BLOCK_NONE");
}

/// <summary>
/// Gemini's free tier answers 429 and 503 routinely. The Python slept 1 s then
/// 2 s between three attempts; losing that turns a busy minute into a file that
/// is not censored.
/// </summary>
public class WhenGeminiIsBusyThenAnswers
{
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;

    public WhenGeminiIsBusyThenAnswers()
    {
        _handler = FakeHttpMessageHandler.Returning(
            (HttpStatusCode.TooManyRequests, GeminiReplies.Busy),
            (HttpStatusCode.ServiceUnavailable, GeminiReplies.Busy),
            (HttpStatusCode.OK, GeminiReplies.Answer)
        );

        var transport = new GeminiTransport(
            _handler.Client(),
            "test-key",
            "gemini-2.5-flash-lite",
            NullLogger<GeminiTransport>.Instance,
            delay: (wait, _) =>
            {
                _delays.Add(wait);
                return Task.CompletedTask;
            }
        );

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();
    }

    [Fact]
    public void EventuallyReturnsTheAnswer() =>
        _response.ShouldBe("""{"start_index": 4, "end_index": 6}""");

    [Fact]
    public void RetriesBothBusyStatuses() => _handler.Requests.Count.ShouldBe(3);

    [Fact]
    public void BacksOffExponentially() =>
        _delays.Select(delay => delay.TotalSeconds).ShouldBe([1.0, 2.0]);
}

public class WhenGeminiStaysBusy
{
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;

    public WhenGeminiStaysBusy()
    {
        _handler = FakeHttpMessageHandler.Returning(
            (HttpStatusCode.ServiceUnavailable, GeminiReplies.Busy)
        );

        var transport = new GeminiTransport(
            _handler.Client(),
            "test-key",
            "gemini-2.5-flash-lite",
            NullLogger<GeminiTransport>.Instance,
            delay: (wait, _) =>
            {
                _delays.Add(wait);
                return Task.CompletedTask;
            }
        );

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();
    }

    [Fact]
    public void GivesUpAsUnavailable() => _response.ShouldBe(SmartCutResponses.ApiUnavailable);

    [Fact]
    public void TriesThreeTimes() => _handler.Requests.Count.ShouldBe(GeminiTransport.MaxAttempts);

    [Fact]
    public void DoesNotSleepAfterTheLastAttempt() =>
        _delays.Count.ShouldBe(GeminiTransport.MaxAttempts - 1);
}

public class WhenGeminiFailsForAnyOtherReason
{
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;

    public WhenGeminiFailsForAnyOtherReason()
    {
        _handler = FakeHttpMessageHandler.Returning(
            (HttpStatusCode.BadRequest, """{"error":"bad model"}""")
        );

        var transport = new GeminiTransport(
            _handler.Client(),
            "test-key",
            "gemini-2.5-flash-lite",
            NullLogger<GeminiTransport>.Instance,
            delay: (wait, _) =>
            {
                _delays.Add(wait);
                return Task.CompletedTask;
            }
        );

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsUnavailableRatherThanThrowing() =>
        _response.ShouldBe(SmartCutResponses.ApiUnavailable);

    [Fact]
    public void DoesNotRetryAnErrorThatWillNotFixItself() => _handler.Requests.Count.ShouldBe(1);

    [Fact]
    public void DoesNotSleep() => _delays.ShouldBeEmpty();
}

public class WhenGeminiRefusesTheContent
{
    private readonly string _response;

    public WhenGeminiRefusesTheContent()
    {
        var handler = FakeHttpMessageHandler.Returning((HttpStatusCode.OK, GeminiReplies.Refusal));
        var transport = new GeminiTransport(
            handler.Client(),
            "test-key",
            "gemini-2.5-flash-lite",
            NullLogger<GeminiTransport>.Instance
        );

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();

        _response.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void SaysSoDistinctlyFromBeingUnreachable() =>
        _response.ShouldBe(SmartCutResponses.ErrorOrRefusal);

    [Fact]
    public void WhichStillMeansKeepTheOriginalTimestamps() =>
        SmartCutResponseParser
            .Parse(_response, PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);
}

public class WhenGeminiCannotBeReached
{
    private readonly string _response;

    public WhenGeminiCannotBeReached()
    {
        var handler = FakeHttpMessageHandler.Throwing(new HttpRequestException("no such host"));
        var transport = new GeminiTransport(
            handler.Client(),
            "test-key",
            "gemini-2.5-flash-lite",
            NullLogger<GeminiTransport>.Instance
        );

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsUnavailableRatherThanThrowing() =>
        _response.ShouldBe(SmartCutResponses.ApiUnavailable);

    [Fact]
    public void WhichMeansKeepTheOriginalTimestamps() =>
        SmartCutResponseParser
            .Parse(_response, PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);
}
