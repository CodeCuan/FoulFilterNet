using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.SmartCut.Tests;

/// <summary>
/// The scenarios from <c>Legacy/src/test_ai_filter.py</c>, which exercise a real
/// model rather than a scripted handler. They are the only check that the ported
/// prompt still teaches a small model the difference between a surgical cut, an
/// idiomatic phrase and a false positive.
/// </summary>
/// <remarks>
/// Opt-in, exactly as the Python's <c>RUN_LIVE_LLM_TESTS</c> marker was: CI has
/// no LLM and no network. Start a server, then
/// <c>RUN_LIVE_LLM_TESTS=1 dotnet test</c>, optionally with
/// <c>LOCAL_LLM_URL</c> and <c>LOCAL_LLM_MODEL</c> set.
/// </remarks>
public class LiveLlmSmartCutTests
{
    private const string OptIn = "opt-in: set RUN_LIVE_LLM_TESTS=1 with an LLM at LOCAL_LLM_URL";

    private static readonly IReadOnlyList<Word> DamnIsMerelyAnIntensifier =
    [
        new Word("That", 1.0, 1.2),
        new Word("is", 1.2, 1.4),
        new Word("a", 1.4, 1.5),
        new Word("damn", 1.5, 1.9),
        new Word("good", 1.9, 2.2),
        new Word("movie", 2.2, 2.5),
    ];

    private static readonly IReadOnlyList<Word> HellEndsAnIdiom =
    [
        new Word("anyway", 4.5, 4.8),
        new Word("just", 4.8, 5.0),
        new Word("tell", 5.0, 5.2),
        new Word("him", 5.2, 5.3),
        new Word("to", 5.3, 5.4),
        new Word("go", 5.4, 5.6),
        new Word("to", 5.6, 5.7),
        new Word("hell", 5.7, 6.0),
        new Word("if", 6.0, 6.2),
        new Word("he", 6.2, 6.3),
        new Word("keeps", 6.3, 6.6),
        new Word("calling", 6.6, 7.0),
        new Word("you", 7.0, 7.2),
    ];

    private static readonly IReadOnlyList<Word> HoeIsAGardenTool =
    [
        new Word("He", 10.0, 10.2),
        new Word("used", 10.2, 10.4),
        new Word("a", 10.4, 10.5),
        new Word("hoe", 10.5, 10.8),
        new Word("to", 10.8, 11.0),
        new Word("garden", 11.0, 11.4),
    ];

    private static readonly IReadOnlyList<Word> BitchIsPlotCritical =
    [
        new Word("The", 15.0, 15.2),
        new Word("bitch", 15.2, 15.6),
        new Word("stole", 15.6, 15.9),
        new Word("the", 15.9, 16.1),
        new Word("keys", 16.1, 16.4),
    ];

    /// <summary>Gate, read the same way the Python's marker read its variable.</summary>
    public static bool LiveLlmTestsEnabled =>
        (Environment.GetEnvironmentVariable("RUN_LIVE_LLM_TESTS") ?? string.Empty).ToLowerInvariant()
            is "1" or "true" or "yes";

    [Fact(Skip = OptIn, SkipUnless = nameof(LiveLlmTestsEnabled))]
    public async Task CutsOnlyTheIntensifierAndLeavesTheComplimentAlone()
    {
        var decision = await RefineAsync(DamnIsMerelyAnIntensifier, "damn", centerIndex: 3);

        decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
        decision.CutStart.ShouldBeGreaterThanOrEqualTo(1.4);
        decision.CutEnd.ShouldBeLessThanOrEqualTo(1.9);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(LiveLlmTestsEnabled))]
    public async Task CutsTheWholeIdiomRatherThanOrphaningGoTo()
    {
        var decision = await RefineAsync(HellEndsAnIdiom, "hell", centerIndex: 7);

        decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
        decision.CutEnd.ShouldBe(6.0, 0.001);
        decision.CutStart.ShouldBeLessThanOrEqualTo(5.4);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(LiveLlmTestsEnabled))]
    public async Task RejectsTheGardenTool() =>
        (await RefineAsync(HoeIsAGardenTool, "hoe", centerIndex: 3)).Outcome.ShouldBe(SmartCutOutcome.Reject);

    [Fact(Skip = OptIn, SkipUnless = nameof(LiveLlmTestsEnabled))]
    public async Task KeepsThePlotIntactAroundAPlotCriticalInsult()
    {
        var decision = await RefineAsync(BitchIsPlotCritical, "bitch", centerIndex: 1);

        decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
        decision.CutEnd.ShouldBeLessThanOrEqualTo(15.6);
    }

    private static Task<SmartCutDecision> RefineAsync(
        IReadOnlyList<Word> contextWindow,
        string phrase,
        int centerIndex)
    {
        var url = Environment.GetEnvironmentVariable("LOCAL_LLM_URL") is { Length: > 0 } configured
            ? configured
            : "http://localhost:8080/v1/chat/completions";

        var client = new HttpClient { Timeout = OpenAiCompatibleTransport.RequestTimeout };
        var transport = new OpenAiCompatibleTransport(
            client,
            url,
            Environment.GetEnvironmentVariable("LOCAL_LLM_MODEL") ?? string.Empty,
            NullLogger<OpenAiCompatibleTransport>.Instance);

        return new LlmSmartCutAdvisor(transport, NullLogger<LlmSmartCutAdvisor>.Instance)
            .RefineAsync(contextWindow, phrase, centerIndex, allowWidening: true, TestContext.Current.CancellationToken);
    }
}
