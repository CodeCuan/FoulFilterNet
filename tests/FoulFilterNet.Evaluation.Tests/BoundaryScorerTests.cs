using FoulFilterNet.Domain;
using FoulFilterNet.Evaluation;

namespace FoulFilterNet.Evaluation.Tests;

/// <summary>
/// A word reported a little late at the start and a little early at the end:
/// the two directions that leave profanity audible, and the ones ADR-0006 says
/// matter more than magnitude.
/// </summary>
public sealed class ARawWordReportedLateAndEarly
{
    private readonly SpanScore _span;

    public ARawWordReportedLateAndEarly()
    {
        var fixture = Fixtures.Single("damn", 3.410, 3.897);

        _span = BoundaryScorer
            .Score(fixture, [new Hit("damn", 3.480, 3.690)], ScoringRules.RawWords)
            .Spans.Single();
    }

    [Fact]
    public void IsFound() => _span.Found.ShouldBeTrue();

    [Fact]
    public void ReportsALateStartAsAPositiveError() => _span.StartError.ShouldBe(0.070);

    [Fact]
    public void ReportsAnEarlyEndAsANegativeError() => _span.EndError.ShouldBe(-0.207);

    [Fact]
    public void MeasuresTheStartMarginAgainstThePrePaddingStillToCome() =>
        _span.StartMargin.ShouldBe(0.080);

    [Fact]
    public void MeasuresTheEndMarginAgainstThePostPaddingStillToCome() =>
        _span.EndMargin.ShouldBe(0.043);

    [Fact]
    public void TakesTheSmallerSideAsTheMargin() => _span.Margin.ShouldBe(0.043);

    [Fact]
    public void IsCoveredOncePadded() => _span.Covered.ShouldBeTrue();

    [Fact]
    public void CountsTheStartAsWithinTolerance() => _span.StartWithinTolerance.ShouldBeTrue();

    [Fact]
    public void CountsTheEndAsWithinTolerance() => _span.EndWithinTolerance.ShouldBeTrue();
}

/// <summary>
/// An early start is outside the tolerance by magnitude - which is what
/// ADR-0006's "10/11 starts" column counted - yet over-censors rather than
/// leaking, so the span is still covered.
/// </summary>
public sealed class ARawWordReportedWellBeforeItStarts
{
    private readonly SpanScore _span;

    public ARawWordReportedWellBeforeItStarts()
    {
        var fixture = Fixtures.Single("damn", 3.410, 3.897);

        _span = BoundaryScorer
            .Score(fixture, [new Hit("damn", 3.168, 3.900)], ScoringRules.RawWords)
            .Spans.Single();
    }

    [Fact]
    public void ReportsAnEarlyStartAsANegativeError() => _span.StartError.ShouldBe(-0.242);

    [Fact]
    public void CountsTheStartAsOutsideTolerance() => _span.StartWithinTolerance.ShouldBeFalse();

    [Fact]
    public void IsStillCovered() => _span.Covered.ShouldBeTrue();
}

/// <summary>An end so early that the post-padding cannot reach it.</summary>
public sealed class ARawWordEndingBeyondThePaddingsReach
{
    private readonly SpanScore _span;

    public ARawWordEndingBeyondThePaddingsReach()
    {
        var fixture = Fixtures.Single("damn", 5.580, 6.067);

        _span = BoundaryScorer
            .Score(fixture, [new Hit("damn", 4.900, 5.170)], ScoringRules.RawWords)
            .Spans.Single();
    }

    [Fact]
    public void IsNotCovered() => _span.Covered.ShouldBeFalse();

    [Fact]
    public void ReportsHowMuchWouldStayAudibleAsANegativeMargin() => _span.Margin.ShouldBe(-0.647);
}

/// <summary>
/// A final hit has already been padded and merged, so no further padding is
/// assumed: the window itself either covers the planted span or it does not.
/// </summary>
public sealed class AFinalHitThatIsAlreadyPadded
{
    private readonly SpanScore _span;

    public AFinalHitThatIsAlreadyPadded()
    {
        var fixture = Fixtures.Single("damn", 3.410, 3.897);

        _span = BoundaryScorer
            .Score(fixture, [new Hit("damn", 3.330, 3.940)], ScoringRules.FinalHits)
            .Spans.Single();
    }

    [Fact]
    public void MeasuresTheStartMarginFromTheWindowItself() => _span.StartMargin.ShouldBe(0.080);

    [Fact]
    public void MeasuresTheEndMarginFromTheWindowItself() => _span.EndMargin.ShouldBe(0.043);

    [Fact]
    public void IsCovered() => _span.Covered.ShouldBeTrue();
}

/// <summary>
/// The same word five times, as in <c>repeated_hits.mp3</c>, reported out of
/// order. Each planted span must pair with the report nearest to it, not with
/// the first report of the same phrase.
/// </summary>
public sealed class RepeatedWordsReportedOutOfOrder
{
    private readonly FixtureScore _score;

    public RepeatedWordsReportedOutOfOrder()
    {
        var fixture = new FixtureTruth(
            "repeated_hits.mp3",
            "audio",
            11.016,
            [
                new PlantedSpan("damn", 0.000, 0.487),
                new PlantedSpan("damn", 2.202, 2.689),
                new PlantedSpan("damn", 4.651, 5.138),
            ]
        );

        _score = BoundaryScorer.Score(
            fixture,
            [
                new Hit("damn", 4.700, 5.100),
                new Hit("damn", 0.020, 0.500),
                new Hit("damn", 2.250, 2.650),
            ],
            ScoringRules.RawWords
        );
    }

    [Fact]
    public void PairsTheFirstPlantWithTheReportNearestIt() =>
        _score.Spans[0].Reported!.Start.ShouldBe(0.020);

    [Fact]
    public void PairsTheSecondPlantWithTheReportNearestIt() =>
        _score.Spans[1].Reported!.Start.ShouldBe(2.250);

    [Fact]
    public void PairsTheThirdPlantWithTheReportNearestIt() =>
        _score.Spans[2].Reported!.Start.ShouldBe(4.700);

    [Fact]
    public void LeavesNoReportUnpaired() => _score.FalsePositives.ShouldBeEmpty();
}

/// <summary>Two plants and one report: only one of them can have been heard.</summary>
public sealed class TwoPlantsCompetingForOneReport
{
    private readonly FixtureScore _score;

    public TwoPlantsCompetingForOneReport()
    {
        var fixture = new FixtureTruth(
            "f.mp3",
            "audio",
            10,
            [new PlantedSpan("damn", 1.000, 1.500), new PlantedSpan("damn", 2.000, 2.500)]
        );

        _score = BoundaryScorer.Score(
            fixture,
            [new Hit("damn", 1.950, 2.450)],
            ScoringRules.RawWords
        );
    }

    [Fact]
    public void GivesTheReportToTheNearerPlant() => _score.Spans[1].Found.ShouldBeTrue();

    [Fact]
    public void MissesTheOther() => _score.Spans[0].Found.ShouldBeFalse();
}

/// <summary>A report of a different phrase at the right moment is not a detection.</summary>
public sealed class AReportOfTheWrongPhrase
{
    private readonly FixtureScore _score;

    public AReportOfTheWrongPhrase()
    {
        _score = BoundaryScorer.Score(
            Fixtures.Single("go to hell", 3.505, 4.227),
            [new Hit("hell", 3.900, 4.200)],
            ScoringRules.RawWords
        );
    }

    [Fact]
    public void MissesThePlant() => _score.Spans.Single().Found.ShouldBeFalse();

    [Fact]
    public void CountsTheReportAsAFalsePositive() => _score.FalsePositives.Count.ShouldBe(1);
}

/// <summary>
/// The right phrase, but nowhere near the plant: pairing it would turn a miss
/// and a false positive into one enormous boundary error.
/// </summary>
public sealed class AReportOfTheRightPhraseTooFarAway
{
    private readonly FixtureScore _score;

    public AReportOfTheRightPhraseTooFarAway()
    {
        _score = BoundaryScorer.Score(
            Fixtures.Single("damn", 3.410, 3.897),
            [new Hit("damn", 8.000, 8.400)],
            ScoringRules.RawWords
        );
    }

    [Fact]
    public void MissesThePlant() => _score.Spans.Single().Found.ShouldBeFalse();

    [Fact]
    public void LeavesTheErrorUnmeasured() => _score.Spans.Single().StartError.ShouldBeNull();

    [Fact]
    public void CountsTheReportAsAFalsePositive() => _score.FalsePositives.Count.ShouldBe(1);
}

/// <summary>
/// The merger joins overlapping windows' phrases with <c>+</c>. A merged window
/// covers every plant it absorbed, so it can be claimed once per phrase it carries.
/// </summary>
public sealed class AMergedFinalWindowCoveringTwoPlants
{
    private readonly FixtureScore _score;

    public AMergedFinalWindowCoveringTwoPlants()
    {
        var fixture = new FixtureTruth(
            "f.mp3",
            "audio",
            10,
            [new PlantedSpan("damn", 1.000, 1.400), new PlantedSpan("go to hell", 1.500, 2.200)]
        );

        _score = BoundaryScorer.Score(
            fixture,
            [new Hit("damn+go to hell", 0.850, 2.450)],
            ScoringRules.FinalHits
        );
    }

    [Fact]
    public void FindsTheFirstPlant() => _score.Spans[0].Found.ShouldBeTrue();

    [Fact]
    public void FindsTheSecondPlant() => _score.Spans[1].Found.ShouldBeTrue();

    [Fact]
    public void IsNotAFalsePositive() => _score.FalsePositives.ShouldBeEmpty();
}

/// <summary>
/// <c>false_positive.mp3</c>'s "hoe" is a garden tool. Finding it is a correct
/// detection by the matcher and a false positive for the censor.
/// </summary>
public sealed class AReportOfAnInnocentSpan
{
    private readonly FixtureScore _score;

    public AReportOfAnInnocentSpan()
    {
        var fixture = new FixtureTruth(
            "false_positive.mp3",
            "audio",
            4.608,
            [new PlantedSpan("hoe", 2.634, 3.082, IsProfanity: false)]
        );

        _score = BoundaryScorer.Score(
            fixture,
            [new Hit("hoe", 2.600, 3.050)],
            ScoringRules.RawWords
        );
    }

    [Fact]
    public void PairsItWithTheInnocentSpan() => _score.Spans.Single().Found.ShouldBeTrue();

    [Fact]
    public void CountsItAsAFalsePositive() => _score.FalsePositives.Count.ShouldBe(1);

    [Fact]
    public void KnowsTheFalsePositiveWasExpected() =>
        _score.ExpectedFalsePositives.Count.ShouldBe(1);
}

internal static class Fixtures
{
    public static FixtureTruth Single(string phrase, double start, double end) =>
        new("fixture.mp3", "audio", 10, [new PlantedSpan(phrase, start, end)]);
}
