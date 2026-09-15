using FoulFilterNet.Domain;

namespace FoulFilterNet.SmartCut.Tests;

/// <summary>The happy path: a small model that answered exactly as asked.</summary>
public class WhenTheModelAnswersWithCleanJson
{
    private readonly SmartCutDecision _decision;

    public WhenTheModelAnswersWithCleanJson()
    {
        _decision = SmartCutResponseParser.Parse(
            """{"reasoning": "idiomatic phrase", "start_index": 4, "end_index": 6}""",
            PromptWindow.Words,
            centerIndex: 6,
            allowWidening: true);

        _decision.ShouldNotBeNull();
        _decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
    }

    [Fact]
    public void CutsFromTheStartOfTheChosenFirstWord() => _decision.CutStart.ShouldBe(5.6, 0.001);

    [Fact]
    public void CutsToTheEndOfTheChosenLastWord() => _decision.CutEnd.ShouldBe(6.2, 0.001);

    [Fact]
    public void AcceptsIndicesTheModelQuotedAsStrings() =>
        SmartCutResponseParser.Parse(
                """{"start_index": "4", "end_index": "6"}""", PromptWindow.Words, 6, allowWidening: true)
            .CutStart.ShouldBe(5.6, 0.001);

    [Fact]
    public void AcceptsIndicesTheModelWroteAsWholeFloats() =>
        SmartCutResponseParser.Parse(
                """{"start_index": 4.0, "end_index": 6.0}""", PromptWindow.Words, 6, allowWidening: true)
            .CutStart.ShouldBe(5.6, 0.001);

    [Fact]
    public void StillClampsToTheTargetWhenWideningIsForbidden() =>
        SmartCutResponseParser.Parse(
                """{"start_index": 4, "end_index": 6}""", PromptWindow.Words, 6, allowWidening: false)
            .CutStart.ShouldBe(5.9, 0.001);
}

/// <summary>
/// Models that were told "return ONLY JSON" and returned an essay with JSON in
/// it. Ported from the Python's <c>re.search(r"\{[\s\S]*?\}", ...)</c>.
/// </summary>
public class WhenTheJsonArrivesInsideChatter
{
    private readonly SmartCutDecision _decision;

    public WhenTheJsonArrivesInsideChatter()
    {
        _decision = SmartCutResponseParser.Parse(
            "Sure! Here is my answer:\n{\"start_index\": 4, \"end_index\": 6}\nHope that helps.",
            PromptWindow.Words,
            centerIndex: 6,
            allowWidening: true);

        _decision.ShouldNotBeNull();
        _decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
    }

    [Fact]
    public void IgnoresTheSurroundingProse() => _decision.CutStart.ShouldBe(5.6, 0.001);

    [Fact]
    public void ReadsThroughAMarkdownCodeFence() =>
        SmartCutResponseParser.Parse(
                "```json\n{\"start_index\": 0, \"end_index\": 1}\n```", PromptWindow.Words, 6, allowWidening: true)
            .CutEnd.ShouldBe(5.4, 0.001);

    [Fact]
    public void ReadsThroughAReasoningPreambleSpanningLines() =>
        SmartCutResponseParser.Parse(
                "I need to think about this.\n\nThe idiom starts at 4.\n\n{\n  \"start_index\": 4,\n  \"end_index\": 6\n}",
                PromptWindow.Words,
                6,
                allowWidening: true)
            .CutStart.ShouldBe(5.6, 0.001);

    [Fact]
    public void TakesTheFirstJsonObjectWhenTheModelOffersTwo() =>
        SmartCutResponseParser.Parse(
                "{\"start_index\": 0, \"end_index\": 0}\nor maybe {\"start_index\": 4, \"end_index\": 6}",
                PromptWindow.Words,
                6,
                allowWidening: true)
            .CutEnd.ShouldBe(5.2, 0.001);
}

/// <summary>
/// The false-positive branch: "hoe" the garden tool. The Python looks for NONE
/// anywhere in the upper-cased response, and this port keeps that - a model that
/// talks itself out of the edit in prose is still answering "do not cut".
/// </summary>
public class WhenTheModelDeclinesTheHit
{
    private readonly SmartCutDecision _decision;

    public WhenTheModelDeclinesTheHit()
    {
        _decision = SmartCutResponseParser.Parse("NONE", PromptWindow.Words, 6, allowWidening: true);

        _decision.ShouldNotBeNull();
    }

    [Fact]
    public void RejectsTheHit() => _decision.Outcome.ShouldBe(SmartCutOutcome.Reject);

    [Fact]
    public void RejectsWhateverTheCasing() =>
        SmartCutResponseParser.Parse("none", PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.Reject);

    [Fact]
    public void RejectsWhenTheWordAppearsInProseAroundTheJson() =>
        SmartCutResponseParser.Parse(
                "There is none of that here. {\"start_index\": 4, \"end_index\": 6}",
                PromptWindow.Words,
                6,
                allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.Reject);

    [Fact]
    public void RejectsTheMinusOnePairToo() =>
        SmartCutResponseParser.Parse(
                """{"reasoning": "a garden tool", "start_index": -1, "end_index": -1}""",
                PromptWindow.Words,
                6,
                allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.Reject);
}

/// <summary>
/// Everything that means "the LLM did not answer". These must keep the original
/// timestamps, never reject: collapsing an outage into a rejection would turn a
/// dead LLM into mass under-censoring.
/// </summary>
public class WhenTheModelDidNotAnswer
{
    private readonly SmartCutDecision _decision;

    public WhenTheModelDidNotAnswer()
    {
        _decision = SmartCutResponseParser.Parse(
            SmartCutResponses.ApiUnavailable, PromptWindow.Words, 6, allowWidening: true);

        _decision.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsTheOriginalTimestampsWhenTheTransportIsUnavailable() =>
        _decision.Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsWhenTheModelRefused() =>
        SmartCutResponseParser.Parse(SmartCutResponses.ErrorOrRefusal, PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsForANullResponse() =>
        SmartCutResponseParser.Parse(null, PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsForAnEmptyResponse() =>
        SmartCutResponseParser.Parse(string.Empty, PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsForWhitespace() =>
        SmartCutResponseParser.Parse("   \n  ", PromptWindow.Words, 6, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);
}

/// <summary>
/// A model that answered, badly. Same safe fallback as an outage - never a
/// rejection, and never an exception.
/// </summary>
public class WhenTheResponseCannotBeParsed
{
    private readonly SmartCutDecision _decision;

    public WhenTheResponseCannotBeParsed()
    {
        _decision = SmartCutResponseParser.Parse(
            "{ this is not valid json }", PromptWindow.Words, 6, allowWidening: true);

        _decision.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsTheOriginalTimestamps() => _decision.Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsWhenThereIsNoJsonAtAll() =>
        SmartCutResponseParser.Parse("I am sorry, I cannot help with that.", PromptWindow.Words, 6, true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsWhenTheIndicesAreMissing() =>
        SmartCutResponseParser.Parse("""{"reasoning": "I thought about it"}""", PromptWindow.Words, 6, true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsWhenAnIndexIsNotANumber() =>
        SmartCutResponseParser.Parse("""{"start_index": "four", "end_index": 6}""", PromptWindow.Words, 6, true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void KeepsTheOriginalTimestampsWhenTheContextWindowIsEmpty() =>
        SmartCutResponseParser.Parse("""{"start_index": 0, "end_index": 0}""", [], 0, true)
            .Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);
}

public class WhenExtractingTheFirstJsonObject
{
    private readonly string? _extracted;

    public WhenExtractingTheFirstJsonObject()
    {
        _extracted = SmartCutResponseParser.ExtractFirstJsonObject(
            "blah {\"a\": 1} blah {\"b\": 2}");

        _extracted.ShouldNotBeNull();
    }

    [Fact]
    public void ReturnsTheFirstObjectOnly() => _extracted.ShouldBe("""{"a": 1}""");

    [Fact]
    public void SpansNewlines() =>
        SmartCutResponseParser.ExtractFirstJsonObject("x\n{\n1\n}\ny").ShouldBe("{\n1\n}");

    [Fact]
    public void ReturnsNullWhenThereIsNoBracedText() =>
        SmartCutResponseParser.ExtractFirstJsonObject("no braces here").ShouldBeNull();

    [Fact]
    public void ReturnsNullForNull() => SmartCutResponseParser.ExtractFirstJsonObject(null).ShouldBeNull();
}
