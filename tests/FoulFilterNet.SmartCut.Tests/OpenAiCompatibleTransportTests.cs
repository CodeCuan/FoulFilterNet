using System.Net;
using System.Text.Json;
using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.SmartCut.Tests;

internal static class LocalReplies
{
    public const string ChatUrl = "http://localhost:8080/v1/chat/completions";
    public const string ModelsUrl = "http://localhost:8080/v1/models";

    public const string Answer =
        """{"choices":[{"message":{"role":"assistant","content":"  {\"start_index\": 4, \"end_index\": 6}\n"}}]}""";

    public const string ModelNotFound =
        """{"error":{"message":"Model 'gpt-oss-20b-Q4_K_M' not found","type":"invalid_request_error"}}""";

    public const string Models =
        """{"object":"list","data":[{"id":"qwen3-8b-Q5_K_M","object":"model"},{"id":"other","object":"model"}]}""";
}

public class WhenTheLocalServerAnswers
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;
    private readonly JsonElement _payload;

    public WhenTheLocalServerAnswers()
    {
        _handler = FakeHttpMessageHandler.Returning((HttpStatusCode.OK, LocalReplies.Answer));
        var transport = new OpenAiCompatibleTransport(
            _handler.Client(),
            LocalReplies.ChatUrl,
            model: "gpt-oss-20b-Q4_K_M",
            NullLogger<OpenAiCompatibleTransport>.Instance);

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();

        _handler.Requests.Count.ShouldBe(1);
        _payload = JsonDocument.Parse(_handler.Requests[0].Body!).RootElement;
    }

    [Fact]
    public void ReturnsTheMessageContentTrimmed() =>
        _response.ShouldBe("""{"start_index": 4, "end_index": 6}""");

    [Fact]
    public void PostsToTheConfiguredChatCompletionsUrl()
    {
        _handler.Requests[0].Method.ShouldBe(HttpMethod.Post);
        _handler.Requests[0].Uri.ToString().ShouldBe(LocalReplies.ChatUrl);
    }

    [Fact]
    public void AsksForTheConfiguredModel() =>
        _payload.GetProperty("model").GetString().ShouldBe("gpt-oss-20b-Q4_K_M");

    [Fact]
    public void KeepsThePythonsSystemPrompt() =>
        _payload.GetProperty("messages")[0].GetProperty("content").GetString()
            .ShouldBe("You are a video editor. Output ONLY JSON.");

    [Fact]
    public void SendsThePromptAsTheUserTurn()
    {
        _payload.GetProperty("messages")[1].GetProperty("role").GetString().ShouldBe("user");
        _payload.GetProperty("messages")[1].GetProperty("content").GetString().ShouldBe("the prompt");
    }

    [Fact]
    public void AsksForANearlyDeterministicAnswer() =>
        _payload.GetProperty("temperature").GetDouble().ShouldBe(0.1, 0.0001);

    [Fact]
    public void ConstrainsTheReplyToAJsonObject() =>
        _payload.GetProperty("response_format").GetProperty("type").GetString().ShouldBe("json_object");

    [Fact]
    public void SendsTheDecisionSchema()
    {
        var schema = _payload.GetProperty("response_format").GetProperty("schema");

        schema.GetProperty("properties").GetProperty("reasoning").GetProperty("type").GetString()
            .ShouldBe("string");
        schema.GetProperty("properties").GetProperty("start_index").GetProperty("type").GetString()
            .ShouldBe("integer");
        schema.GetProperty("properties").GetProperty("end_index").GetProperty("type").GetString()
            .ShouldBe("integer");
    }

    [Fact]
    public void RequiresEveryFieldOfTheSchema() =>
        _payload.GetProperty("response_format").GetProperty("schema").GetProperty("required")
            .EnumerateArray().Select(field => field.GetString())
            .ShouldBe(["reasoning", "start_index", "end_index"]);

    [Fact]
    public void AllowsForAColdServerLoadingWeightsIntoVram() =>
        OpenAiCompatibleTransport.RequestTimeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(4));
}

/// <summary>
/// The self-healing retry: llama-server is routinely running a different quant
/// than configuration says. Ported from the 400/"not found" branch of
/// <c>llm_inference_local</c>.
/// </summary>
public class WhenTheLocalServerRunsADifferentModel
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;

    public WhenTheLocalServerRunsADifferentModel()
    {
        var posts = 0;
        _handler = FakeHttpMessageHandler.Responding(request =>
            request.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, LocalReplies.Models)
                : ++posts == 1
                    ? (HttpStatusCode.BadRequest, LocalReplies.ModelNotFound)
                    : (HttpStatusCode.OK, LocalReplies.Answer));

        var transport = new OpenAiCompatibleTransport(
            _handler.Client(),
            LocalReplies.ChatUrl,
            model: "gpt-oss-20b-Q4_K_M",
            NullLogger<OpenAiCompatibleTransport>.Instance);

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();

        _handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public void EventuallyReturnsTheAnswer() =>
        _response.ShouldBe("""{"start_index": 4, "end_index": 6}""");

    [Fact]
    public void AsksTheServerWhatItIsActuallyRunning()
    {
        _handler.Requests[1].Method.ShouldBe(HttpMethod.Get);
        _handler.Requests[1].Uri.ToString().ShouldBe(LocalReplies.ModelsUrl);
    }

    [Fact]
    public void RetriesWithTheServersOwnModelId() =>
        JsonDocument.Parse(_handler.Requests[2].Body!).RootElement.GetProperty("model").GetString()
            .ShouldBe("qwen3-8b-Q5_K_M");

    [Fact]
    public void RetriesOnlyOnce() =>
        _handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(2);
}

public class WhenTheModelIdIsNotConfigured
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;

    public WhenTheModelIdIsNotConfigured()
    {
        _handler = FakeHttpMessageHandler.Responding(request =>
            request.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, LocalReplies.Models)
                : (HttpStatusCode.OK, LocalReplies.Answer));

        var transport = new OpenAiCompatibleTransport(
            _handler.Client(),
            LocalReplies.ChatUrl,
            model: "",
            NullLogger<OpenAiCompatibleTransport>.Instance);

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();

        _response.ShouldBe("""{"start_index": 4, "end_index": 6}""");
    }

    [Fact]
    public void AsksTheServerBeforeGuessing() =>
        _handler.Requests[0].Uri.ToString().ShouldBe(LocalReplies.ModelsUrl);

    [Fact]
    public void UsesWhatTheServerNamed() =>
        JsonDocument.Parse(_handler.Requests[1].Body!).RootElement.GetProperty("model").GetString()
            .ShouldBe("qwen3-8b-Q5_K_M");
}

public class WhenTheLocalServerIsNotRunning
{
    private readonly string _response;

    public WhenTheLocalServerIsNotRunning()
    {
        var handler = FakeHttpMessageHandler.Throwing(new HttpRequestException("connection refused"));
        var transport = new OpenAiCompatibleTransport(
            handler.Client(),
            LocalReplies.ChatUrl,
            "gpt-oss-20b-Q4_K_M",
            NullLogger<OpenAiCompatibleTransport>.Instance);

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsUnavailableRatherThanThrowing() =>
        _response.ShouldBe(SmartCutResponses.ApiUnavailable);

    [Fact]
    public void WhichMeansKeepTheOriginalTimestamps() =>
        SmartCutResponseParser.Parse(_response, PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);
}

public class WhenTheLocalServerFailsOutright
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;

    public WhenTheLocalServerFailsOutright()
    {
        _handler = FakeHttpMessageHandler.Returning(
            (HttpStatusCode.InternalServerError, """{"error":"out of memory"}"""));

        var transport = new OpenAiCompatibleTransport(
            _handler.Client(),
            LocalReplies.ChatUrl,
            "gpt-oss-20b-Q4_K_M",
            NullLogger<OpenAiCompatibleTransport>.Instance);

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsUnavailable() => _response.ShouldBe(SmartCutResponses.ApiUnavailable);

    [Fact]
    public void DoesNotGoModelHuntingForAnErrorThatIsNotAboutTheModel() =>
        _handler.Requests.ShouldAllBe(request => request.Method == HttpMethod.Post);

    [Fact]
    public void DoesNotRetry() => _handler.Requests.Count.ShouldBe(1);
}

public class WhenModelDiscoveryAlsoFails
{
    private readonly FakeHttpMessageHandler _handler;
    private readonly string _response;

    public WhenModelDiscoveryAlsoFails()
    {
        _handler = FakeHttpMessageHandler.Responding(request =>
            request.Method == HttpMethod.Get
                ? (HttpStatusCode.NotFound, "no such endpoint")
                : (HttpStatusCode.BadRequest, LocalReplies.ModelNotFound));

        var transport = new OpenAiCompatibleTransport(
            _handler.Client(),
            LocalReplies.ChatUrl,
            "gpt-oss-20b-Q4_K_M",
            NullLogger<OpenAiCompatibleTransport>.Instance);

        _response = transport.CompleteAsync("the prompt").GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsUnavailable() => _response.ShouldBe(SmartCutResponses.ApiUnavailable);

    [Fact]
    public void DoesNotRetryThePostWithoutAModelIdToRetryWith() =>
        _handler.Requests.Count(request => request.Method == HttpMethod.Post).ShouldBe(1);
}

public class WhenDerivingTheModelsUrl
{
    private readonly Uri _uri;

    public WhenDerivingTheModelsUrl()
    {
        _uri = OpenAiCompatibleTransport.ModelsUriFor(LocalReplies.ChatUrl);

        _uri.ShouldNotBeNull();
    }

    [Fact]
    public void SwapsTheChatCompletionsSuffix() => _uri.ToString().ShouldBe(LocalReplies.ModelsUrl);

    [Fact]
    public void LeavesAnyOtherPathAlone() =>
        OpenAiCompatibleTransport.ModelsUriFor("http://gpu-box:9000/v1/chat/completions").ToString()
            .ShouldBe("http://gpu-box:9000/v1/models");
}
