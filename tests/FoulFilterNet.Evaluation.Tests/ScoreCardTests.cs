using FoulFilterNet.Domain;
using FoulFilterNet.Evaluation;

namespace FoulFilterNet.Evaluation.Tests;

/// <summary>
/// Three fixtures: two profanities heard (one late, one early), one missed, one
/// innocent word flagged, and one report matching nothing at all. Detection and
/// boundary error count every planted span, innocent or not - the manifest's
/// timing is exact for all of them, and that is how ADR-0006 counted its eleven.
/// Recall and precision are about profanity only.
/// </summary>
public sealed class AScoreCardOverSeveralFixtures
{
    private readonly ScoreCard _card;

    public AScoreCardOverSeveralFixtures()
    {
        var single = BoundaryScorer.Score(
            new FixtureTruth(
                "single_hit.mp3",
                "audio",
                8.388,
                [new PlantedSpan("damn", 3.410, 3.897)]
            ),
            [new Hit("damn", 3.480, 3.800)],
            ScoringRules.RawWords
        );

        var audiobook = BoundaryScorer.Score(
            new FixtureTruth(
                "audiobook.m4b",
                "audio",
                13.03,
                [
                    new PlantedSpan("damn", 5.580, 6.067),
                    new PlantedSpan("go to hell", 11.252, 11.974),
                ]
            ),
            [new Hit("damn", 5.380, 6.100), new Hit("damn", 1.000, 1.200)],
            ScoringRules.RawWords
        );

        var innocent = BoundaryScorer.Score(
            new FixtureTruth(
                "false_positive.mp3",
                "audio",
                4.608,
                [new PlantedSpan("hoe", 2.634, 3.082, IsProfanity: false)]
            ),
            [new Hit("hoe", 2.600, 3.050)],
            ScoringRules.RawWords
        );

        _card = ScoreCard.From([single, audiobook, innocent], ScoringRules.RawWords);
    }

    [Fact]
    public void CountsEverySpanPlanted() => _card.Spans.ShouldBe(4);

    [Fact]
    public void CountsEverySpanDetected() => _card.Detected.ShouldBe(3);

    [Fact]
    public void ComputesDetectionOverEverySpan() => _card.Detection.ShouldBe(0.75);

    [Fact]
    public void CountsOnlyProfanitiesAsPlanted() => _card.Planted.ShouldBe(3);

    [Fact]
    public void CountsTheProfanitiesFound() => _card.Found.ShouldBe(2);

    [Fact]
    public void ComputesRecallOverProfanitiesOnly() => _card.Recall.ShouldBe(0.667);

    [Fact]
    public void CountsEveryReport() => _card.Reported.ShouldBe(4);

    [Fact]
    public void CountsBothTheStrayAndTheInnocentReportAsFalsePositives() =>
        _card.FalsePositives.ShouldBe(2);

    [Fact]
    public void CountsTheInnocentReportAsAnExpectedFalsePositive() =>
        _card.ExpectedFalsePositives.ShouldBe(1);

    [Fact]
    public void ComputesPrecisionOverEveryReport() => _card.Precision.ShouldBe(0.5);

    [Fact]
    public void AveragesTheAbsoluteStartErrorOverEveryDetectedSpan() =>
        _card.StartError.MeanAbsolute.ShouldBe(0.101);

    [Fact]
    public void FindsTheLargestAbsoluteStartError() => _card.StartError.MaxAbsolute.ShouldBe(0.2);

    [Fact]
    public void AveragesTheSignedStartError() => _card.StartError.MeanSigned.ShouldBe(-0.055);

    [Fact]
    public void AveragesTheAbsoluteEndError() => _card.EndError.MeanAbsolute.ShouldBe(0.054);

    [Fact]
    public void AveragesTheSignedEndError() => _card.EndError.MeanSigned.ShouldBe(-0.032);

    [Fact]
    public void FindsTheWorstLateStart() => _card.WorstLateStart.ShouldBe(0.070);

    [Fact]
    public void FindsTheWorstEarlyEnd() => _card.WorstEarlyEnd.ShouldBe(0.097);

    [Fact]
    public void CountsStartsWithinTheirTolerance() => _card.StartsWithinTolerance.ShouldBe(2);

    [Fact]
    public void CountsEndsWithinTheirTolerance() => _card.EndsWithinTolerance.ShouldBe(3);

    [Fact]
    public void CountsSpansFullyCovered() => _card.Covered.ShouldBe(3);

    [Fact]
    public void FindsTheSmallestStartMargin() => _card.WorstStartMargin.ShouldBe(0.080);

    [Fact]
    public void FindsTheSmallestEndMargin() => _card.WorstEndMargin.ShouldBe(0.153);
}

/// <summary>
/// Nothing planted and nothing found - <c>clean_speech.mp3</c> on its own.
/// Nothing to divide by must not become NaN in a report meant to be diffed.
/// </summary>
public sealed class AScoreCardWithNothingPlanted
{
    private readonly ScoreCard _card;

    public AScoreCardWithNothingPlanted()
    {
        var clean = BoundaryScorer.Score(
            new FixtureTruth("clean_speech.mp3", "audio", 6.732, []),
            [],
            ScoringRules.RawWords
        );

        _card = ScoreCard.From([clean], ScoringRules.RawWords);
    }

    [Fact]
    public void HasPerfectRecall() => _card.Recall.ShouldBe(1.0);

    [Fact]
    public void HasPerfectPrecision() => _card.Precision.ShouldBe(1.0);

    [Fact]
    public void HasNoMeanStartError() => _card.StartError.MeanAbsolute.ShouldBeNull();

    [Fact]
    public void HasNoWorstLateStart() => _card.WorstLateStart.ShouldBeNull();
}
