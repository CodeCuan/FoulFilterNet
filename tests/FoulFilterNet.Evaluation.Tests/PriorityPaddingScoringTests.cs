using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Evaluation;
using FoulFilterNet.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FoulFilterNet.Evaluation.Tests;

internal static class PriorityScoring
{
    public static readonly CutPadding Padding = CutPadding.ForPriorityWords(
        PriorityWordList.Default
    );

    /// <summary>ct_edge_0_1000ms: the F-word ended 0.47 s before its 10 ms report.</summary>
    public static FixtureTruth Edge => Fixtures.Single("fuck", 3.168, 3.692);

    public static Hit Report => new("fuck", 4.160, 4.170);
}

/// <summary>
/// X03: raw words are scored against the padding the job will still apply,
/// and for a priority word that is its minimum length and wider padding.
/// </summary>
public sealed class ARawPriorityWordScoredWithTheJobsPadding
{
    private readonly SpanScore _span;

    public ARawPriorityWordScoredWithTheJobsPadding()
    {
        _span = BoundaryScorer
            .Score(
                PriorityScoring.Edge,
                [PriorityScoring.Report],
                ScoringRules.RawWordsFor(PriorityScoring.Padding)
            )
            .Spans.Single();

        _span.Found.ShouldBeTrue();
    }

    // 3.168 - (4.170 - 0.8 - 0.25)
    [Fact]
    public void MeasuresTheStartMarginFromTheGrownAndPaddedStart() =>
        _span.StartMargin.ShouldBe(0.048);

    // 4.170 + 0.5 - 3.692
    [Fact]
    public void MeasuresTheEndMarginFromThePriorityPostPadding() => _span.EndMargin.ShouldBe(0.978);

    [Fact]
    public void IsCoveredOncePadded() => _span.Covered.ShouldBeTrue();

    [Fact]
    public void StillReportsTheRawStartError() => _span.StartError.ShouldBe(0.992);
}

public sealed class ARawPriorityWordScoredWithTheDefaultPadding
{
    private readonly SpanScore _span;

    public ARawPriorityWordScoredWithTheDefaultPadding()
    {
        _span = BoundaryScorer
            .Score(PriorityScoring.Edge, [PriorityScoring.Report], ScoringRules.RawWords)
            .Spans.Single();

        _span.Found.ShouldBeTrue();
    }

    [Fact]
    public void IsNotCovered() => _span.Covered.ShouldBeFalse();

    [Fact]
    public void LeavesTheStartAudible() => _span.StartMargin.ShouldBe(-0.842);
}

public sealed class WhenChoosingScoringRules
{
    [Fact]
    public void ScoresRawWordsWithTheDefaultPaddingByDefault() =>
        ScoringRules.RawWords.PaddingStillToApply.ShouldBeSameAs(CutPadding.Default);

    [Fact]
    public void ScoresRawWordsWithTheJobsPaddingWhenGivenIt() =>
        ScoringRules
            .RawWordsFor(PriorityScoring.Padding)
            .PaddingStillToApply.ShouldBeSameAs(PriorityScoring.Padding);

    [Fact]
    public void ScoresFinalHitsWithNothingStillToApply() =>
        ScoringRules.FinalHits.PaddingStillToApply.ShouldBeSameAs(CutPadding.None);

    [Fact]
    public void KeepsTheToleranceAtTheDefaultPadding() =>
        ScoringRules.RawWordsFor(PriorityScoring.Padding).Tolerance.ShouldBe(HitPadding.Default);

    [Fact]
    public void RejectsANullPadding() =>
        Should.Throw<ArgumentNullException>(() => ScoringRules.RawWordsFor(null!));
}

/// <summary>
/// How much audio a level would silence: the union of its windows once the
/// padding still to apply is added, clamped at zero, summed over fixtures.
/// </summary>
public sealed class AScoreCardCountingCensoredSeconds
{
    private readonly ScoreCard _final;
    private readonly ScoreCard _raw;

    public AScoreCardCountingCensoredSeconds()
    {
        var truth = Fixtures.Single("damn", 1.0, 1.5);
        var overlapping = BoundaryScorer.Score(
            truth,
            [new Hit("damn", 1.0, 2.0), new Hit("damn", 1.5, 2.5)],
            ScoringRules.FinalHits
        );
        var another = BoundaryScorer.Score(
            truth,
            [new Hit("damn", 0.0, 1.0)],
            ScoringRules.FinalHits
        );
        _final = ScoreCard.From([overlapping, another], ScoringRules.FinalHits);

        var raw = BoundaryScorer.Score(
            truth,
            [new Hit("damn", 0.05, 0.5), new Hit("fuck", 5.0, 5.01)],
            ScoringRules.RawWordsFor(PriorityScoring.Padding)
        );
        _raw = ScoreCard.From([raw], ScoringRules.RawWordsFor(PriorityScoring.Padding));
    }

    [Fact]
    public void CountsOverlappingWindowsOnce() => _final.CensoredSeconds.ShouldBe(2.5);

    // 0-0.75 (damn, clamped at zero) + 3.96-5.51 (fuck grown to 0.8 s and padded)
    [Fact]
    public void CountsRawWordsOncePaddedAsTheJobWould() => _raw.CensoredSeconds.ShouldBe(2.3);
}

public sealed class AReportScoredWithPriorityPadding
{
    private readonly EvaluationReport _report;
    private readonly string _table;

    public AReportScoredWithPriorityPadding()
    {
        var run = new FixtureRun(
            PriorityScoring.Edge,
            Words: [new Word("fuck", 4.160, 4.170)],
            RawHits: [PriorityScoring.Report],
            FinalHits: [new Hit("fuck", 3.12, 4.67, 0)],
            UsedCachedTranscript: true,
            ElapsedSeconds: 0.5
        );

        _report = EvaluationReport.Build([run], new EvaluationRunInfo(), PriorityScoring.Padding);
        _table = ReportFormatter.ToTable(_report);
    }

    [Fact]
    public void ScoresTheRawWordsWithTheJobsPadding() =>
        _report.RawWords.Summary.Covered.ShouldBe(1);

    [Fact]
    public void ScoresTheFinalHitsAsTheyStand() =>
        _report.FinalHits.Fixtures.Single().Spans.Single().StartMargin.ShouldBe(0.048);

    [Fact]
    public void TabulatesTheCensoredSeconds() => _table.ShouldContain("censored 1.550 s");

    [Fact]
    public void DescribesThePriorityPadding() =>
        EvaluationRunInfo
            .Describe(PriorityScoring.Padding)
            .ShouldBe("7 priority word(s): at least 0.80 s, padded 0.25/0.50 s");

    [Fact]
    public void DescribesNoPriorityPadding() =>
        EvaluationRunInfo.Describe(CutPadding.Default).ShouldBe("off");
}

/// <summary>
/// The evaluator scores raw words with the padding its pipeline applies, so
/// both must come from the one registration.
/// </summary>
public sealed class WhenTheEvaluationPipelineIsComposed : IDisposable
{
    private readonly string _data = Path.Combine(
        Path.GetTempPath(),
        "foulfilter-eval-compose-" + Guid.NewGuid().ToString("N")
    );
    private readonly ServiceProvider _services;

    public WhenTheEvaluationPipelineIsComposed()
    {
        Directory.CreateDirectory(_data);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Storage:DataDirectory"] = _data }
            )
            .Build();
        _services = new ServiceCollection()
            .AddLogging()
            .AddEvaluationPipeline(configuration)
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    [Fact]
    public void CutsTheBuiltInPriorityWordsWider() =>
        _services.GetRequiredService<CutPadding>().PriorityWords.ShouldBe(PriorityWordList.Default);

    [Fact]
    public void GivesThePipelineThatPadding() =>
        ((MediaPipeline)_services.GetRequiredService<IMediaPipeline>()).CutPadding.ShouldBeSameAs(
            _services.GetRequiredService<CutPadding>()
        );
}
