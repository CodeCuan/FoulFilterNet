using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.SmartCut.Tests;

/// <summary>Records what the advisor asked, and answers with whatever the test scripted.</summary>
internal sealed class StubTransport : ISmartCutTransport
{
    private readonly Func<string, string> _answer;

    public StubTransport(string answer) => _answer = _ => answer;

    public StubTransport(Func<string, string> answer) => _answer = answer;

    public List<string> Prompts { get; } = [];

    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);

        return Task.FromResult(_answer(prompt));
    }
}

public class WhenTheAdvisorRefinesAHit
{
    private readonly StubTransport _transport;
    private readonly SmartCutDecision _decision;

    public WhenTheAdvisorRefinesAHit()
    {
        _transport = new StubTransport("""{"reasoning": "idiom", "start_index": 4, "end_index": 6}""");
        var advisor = new LlmSmartCutAdvisor(_transport, NullLogger<LlmSmartCutAdvisor>.Instance);

        _decision = advisor
            .RefineAsync(PromptWindow.Words, "hell", centerIndex: 6, allowWidening: true)
            .GetAwaiter().GetResult();

        _decision.ShouldNotBeNull();
        _transport.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public void ReportsItselfEnabled() =>
        new LlmSmartCutAdvisor(_transport, NullLogger<LlmSmartCutAdvisor>.Instance).IsEnabled.ShouldBeTrue();

    [Fact]
    public void SendsThePromptBuiltFromTheHit() =>
        _transport.Prompts[0].ShouldContain("TARGET WORD: \"hell\"");

    [Fact]
    public void SendsTheIndexedContextWindow() => _transport.Prompts[0].ShouldContain("6: hell");

    [Fact]
    public void AdjustsTheHitToWhatTheModelChose()
    {
        _decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
        _decision.CutStart.ShouldBe(5.6, 0.001);
        _decision.CutEnd.ShouldBe(6.2, 0.001);
    }
}

public class WhenTheAdvisorMayNotWiden
{
    private readonly StubTransport _transport;
    private readonly SmartCutDecision _decision;

    public WhenTheAdvisorMayNotWiden()
    {
        _transport = new StubTransport("""{"start_index": 4, "end_index": 6}""");
        var advisor = new LlmSmartCutAdvisor(_transport, NullLogger<LlmSmartCutAdvisor>.Instance);

        _decision = advisor
            .RefineAsync(PromptWindow.Words, "hell", centerIndex: 6, allowWidening: false)
            .GetAwaiter().GetResult();

        _decision.ShouldNotBeNull();
    }

    [Fact]
    public void TellsTheModelWideningIsForbidden() =>
        _transport.Prompts[0].ShouldContain(SmartCutPrompt.SurgicalRule);

    [Fact]
    public void ClampsTheAnswerToTheTargetWordAnyway() => _decision.CutStart.ShouldBe(5.9, 0.001);
}

public class WhenTheModelRejectsTheHitThroughTheAdvisor
{
    private readonly SmartCutDecision _decision;

    public WhenTheModelRejectsTheHitThroughTheAdvisor()
    {
        var advisor = new LlmSmartCutAdvisor(
            new StubTransport("""{"reasoning": "a garden tool", "start_index": -1, "end_index": -1}"""),
            NullLogger<LlmSmartCutAdvisor>.Instance);

        _decision = advisor
            .RefineAsync(PromptWindow.Words, "hoe", centerIndex: 6, allowWidening: true)
            .GetAwaiter().GetResult();

        _decision.ShouldNotBeNull();
    }

    [Fact]
    public void DropsTheHit() => _decision.Outcome.ShouldBe(SmartCutOutcome.Reject);
}

/// <summary>
/// The contract says RefineAsync must never throw, and STATUS.md says any throw
/// from <see cref="SmartCutMapper"/> becomes KeepOriginal. A flaky LLM must not
/// be able to fail a job.
/// </summary>
public class WhenTheAdvisorHitsTrouble
{
    private readonly StubTransport _exploding;
    private readonly SmartCutDecision _afterTheTransportThrew;
    private readonly SmartCutDecision _afterTheTransportWasUnavailable;
    private readonly SmartCutDecision _afterAnEmptyContextWindow;

    public WhenTheAdvisorHitsTrouble()
    {
        _exploding = new StubTransport(_ => throw new InvalidOperationException("the transport blew up"));

        _afterTheTransportThrew = Refine(_exploding, PromptWindow.Words, 6);
        _afterTheTransportWasUnavailable =
            Refine(new StubTransport(SmartCutResponses.ApiUnavailable), PromptWindow.Words, 6);
        _afterAnEmptyContextWindow = Refine(_exploding, [], 0);
    }

    [Fact]
    public void SwallowsAnExceptionFromTheTransport() =>
        _afterTheTransportThrew.Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsWhenTheTransportIsUnavailable() =>
        _afterTheTransportWasUnavailable.Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsForAnEmptyContextWindow() =>
        _afterAnEmptyContextWindow.Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void NeverRejectsAHitItCouldNotGetAnAnswerFor() =>
        _afterTheTransportThrew.Outcome.ShouldNotBe(SmartCutOutcome.Reject);

    [Fact]
    public void DoesNotSpendAnLlmCallOnAnEmptyContextWindow() =>
        _exploding.Prompts.Count.ShouldBe(1);

    private static SmartCutDecision Refine(
        ISmartCutTransport transport,
        IReadOnlyList<Word> contextWindow,
        int centerIndex) =>
        new LlmSmartCutAdvisor(transport, NullLogger<LlmSmartCutAdvisor>.Instance)
            .RefineAsync(contextWindow, "hell", centerIndex, allowWidening: true)
            .GetAwaiter().GetResult();
}
