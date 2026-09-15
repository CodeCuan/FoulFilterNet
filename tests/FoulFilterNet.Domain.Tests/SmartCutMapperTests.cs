using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

/// <summary>
/// The context window from <c>Legacy/src/test_pipeline_logic.py</c>, carried over
/// verbatim so the ported assertions mean the same thing.
/// </summary>
internal static class SmartCutWindow
{
    public static readonly IReadOnlyList<Word> Words =
    [
        new Word("just", 5.0, 5.2),
        new Word("tell", 5.2, 5.4),
        new Word("him", 5.4, 5.5),
        new Word("to", 5.5, 5.6),
        new Word("go", 5.6, 5.8),
        new Word("to", 5.8, 5.9),
        new Word("hell", 5.9, 6.2),
    ];
}

/// <summary>Ports <c>test_widening_maps_phrase_span</c>.</summary>
public class WhenWideningIsAllowed
{
    private readonly SmartCutDecision _decision;

    public WhenWideningIsAllowed()
    {
        _decision = SmartCutMapper.Map(
            startIndex: 1,
            endIndex: 6,
            contextWindow: SmartCutWindow.Words,
            centerIndex: 6,
            allowWidening: true);

        _decision.ShouldNotBeNull();
        _decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
    }

    [Fact]
    public void CutsFromTheStartOfTheChosenFirstWord() => _decision.CutStart.ShouldBe(5.2, 0.001);

    [Fact]
    public void CutsToTheEndOfTheChosenLastWord() => _decision.CutEnd.ShouldBe(6.2, 0.001);

    [Fact]
    public void SpansEveryWordTheAdvisorAskedFor() =>
        (_decision.CutEnd - _decision.CutStart).ShouldBe(1.0, 0.001);
}

/// <summary>
/// Ports <c>test_surgical_mode_clamps_to_target</c>. ADR-0004: silence, bleep and
/// video never widen beyond the target word.
/// </summary>
public class WhenWideningIsNotAllowed
{
    private readonly SmartCutDecision _decision;

    public WhenWideningIsNotAllowed()
    {
        _decision = SmartCutMapper.Map(
            startIndex: 1,
            endIndex: 6,
            contextWindow: SmartCutWindow.Words,
            centerIndex: 6,
            allowWidening: false);

        _decision.ShouldNotBeNull();
        _decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
    }

    [Fact]
    public void IgnoresTheAdvisorsStartIndex() => _decision.CutStart.ShouldBe(5.9, 0.001);

    [Fact]
    public void EndsAtTheTargetWord() => _decision.CutEnd.ShouldBe(6.2, 0.001);

    [Fact]
    public void CollapsesTheWindowOntoTheTargetWordAlone() =>
        (_decision.CutEnd - _decision.CutStart).ShouldBe(0.3, 0.001);

    [Fact]
    public void ClampsACenterIndexThatIsOutOfRangeToo() =>
        SmartCutMapper.Map(0, 0, SmartCutWindow.Words, centerIndex: 99, allowWidening: false)
            .CutStart.ShouldBe(5.9, 0.001);
}

/// <summary>Ports <c>test_minus_one_means_skip</c>.</summary>
public class WhenTheAdvisorAnswersMinusOne
{
    private readonly SmartCutDecision _decision;

    public WhenTheAdvisorAnswersMinusOne()
    {
        _decision = SmartCutMapper.Map(-1, -1, SmartCutWindow.Words, centerIndex: 3, allowWidening: true);

        _decision.ShouldNotBeNull();
    }

    [Fact]
    public void RejectsTheHit() => _decision.Outcome.ShouldBe(SmartCutOutcome.Reject);

    [Fact]
    public void RejectsWhenOnlyTheStartIndexIsMinusOne() =>
        SmartCutMapper.Map(-1, 4, SmartCutWindow.Words, 3, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.Reject);

    [Fact]
    public void RejectsWhenOnlyTheEndIndexIsMinusOne() =>
        SmartCutMapper.Map(2, -1, SmartCutWindow.Words, 3, allowWidening: true)
            .Outcome.ShouldBe(SmartCutOutcome.Reject);

    [Fact]
    public void RejectsEvenWhenWideningIsForbidden() =>
        SmartCutMapper.Map(-1, -1, SmartCutWindow.Words, 3, allowWidening: false)
            .Outcome.ShouldBe(SmartCutOutcome.Reject);
}

/// <summary>Ports <c>test_out_of_bounds_indices_clamped</c>.</summary>
public class WhenTheAdvisorAnswersOutOfRange
{
    private readonly SmartCutDecision _decision;

    public WhenTheAdvisorAnswersOutOfRange()
    {
        _decision = SmartCutMapper.Map(-5, 99, SmartCutWindow.Words, centerIndex: 0, allowWidening: true);

        _decision.ShouldNotBeNull();
        _decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);
    }

    [Fact]
    public void ClampsTheStartToTheFirstWord() =>
        _decision.CutStart.ShouldBe(SmartCutWindow.Words[0].Start, 0.001);

    [Fact]
    public void ClampsTheEndToTheLastWord() =>
        _decision.CutEnd.ShouldBe(SmartCutWindow.Words[^1].End, 0.001);

    [Fact]
    public void TreatsAVeryNegativeStartAsTheBeginningRatherThanARejection() =>
        _decision.Outcome.ShouldNotBe(SmartCutOutcome.Reject);
}

public class WhenTheContextWindowIsEmpty
{
    private readonly Func<SmartCutDecision> _map;

    public WhenTheContextWindowIsEmpty()
    {
        _map = () => SmartCutMapper.Map(0, 0, [], centerIndex: 0, allowWidening: true);

        _map.ShouldNotBeNull();
    }

    [Fact]
    public void Throws() => Should.Throw<ArgumentException>(_map);

    [Fact]
    public void SaysWhichArgumentWasWrong() =>
        Should.Throw<ArgumentException>(_map).ParamName.ShouldBe("contextWindow");

    [Fact]
    public void StillRejectsWithoutLookingAtTheWindow() =>
        SmartCutMapper.Map(-1, -1, [], 0, allowWidening: true).Outcome.ShouldBe(SmartCutOutcome.Reject);
}
