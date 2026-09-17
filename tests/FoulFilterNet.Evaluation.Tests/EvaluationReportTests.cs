using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Evaluation;

namespace FoulFilterNet.Evaluation.Tests;

/// <summary>
/// One fixture whose raw word starts 0.070 s late and whose final window covers
/// it, rendered both ways: a table to read and JSON to diff.
/// </summary>
public sealed class AReportOfOneFixture
{
    private readonly EvaluationReport _report;
    private readonly string _table;
    private readonly JsonElement _json;

    public AReportOfOneFixture()
    {
        var run = new FixtureRun(
            new FixtureTruth("single_hit.mp3", "audio", 8.388, [new PlantedSpan("damn", 3.410, 3.897)]),
            Words: [new Word("damn,", 3.480, 3.800)],
            RawHits: [new Hit("damn", 3.480, 3.800, 0)],
            FinalHits: [new Hit("damn", 3.330, 4.050, 0)],
            UsedCachedTranscript: false,
            ElapsedSeconds: 1.25);

        _report = EvaluationReport.Build([run], new EvaluationRunInfo { Model = "openai/whisper-large-v3-turbo" });
        _table = ReportFormatter.ToTable(_report);
        _json = JsonDocument.Parse(ReportFormatter.ToJson(_report)).RootElement;
    }

    [Fact]
    public void ScoresTheRawWordsAssumingThePaddingStillToCome() =>
        _report.RawWords.Fixtures.Single().Spans.Single().StartMargin.ShouldBe(0.080);

    [Fact]
    public void ScoresTheFinalHitsAsTheyStand() =>
        _report.FinalHits.Fixtures.Single().Spans.Single().StartMargin.ShouldBe(0.080);

    [Fact]
    public void TabulatesTheSignedStartError() => _table.ShouldContain("+0.070");

    [Fact]
    public void TabulatesTheFixture() => _table.ShouldContain("single_hit.mp3");

    [Fact]
    public void TabulatesTheRecall() => _table.ShouldContain("recall 1/1");

    [Fact]
    public void WritesTheRecallAsJson() =>
        _json.GetProperty("rawWords").GetProperty("summary").GetProperty("recall").GetDouble().ShouldBe(1.0);

    [Fact]
    public void WritesTheSignedErrorOfEachSpanAsJson() =>
        _json.GetProperty("rawWords").GetProperty("fixtures")[0].GetProperty("spans")[0]
            .GetProperty("startError").GetDouble().ShouldBe(0.070);

    [Fact]
    public void WritesTheRunSettingsAsJson() =>
        _json.GetProperty("run").GetProperty("model").GetString().ShouldBe("openai/whisper-large-v3-turbo");
}

/// <summary>
/// A report matching no plant at all - the audiobook's fallback window - gets a
/// row of its own, where an innocent span's report is already shown on its span's row.
/// </summary>
public sealed class AReportOfAStrayHitAndAnInnocentOne
{
    private readonly string _table;

    public AReportOfAStrayHitAndAnInnocentOne()
    {
        var audiobook = new FixtureRun(
            new FixtureTruth("audiobook.m4b", "audio", 13.03, [new PlantedSpan("damn", 5.580, 6.067)]),
            Words: [],
            RawHits: [],
            FinalHits: [new Hit("damn", 4.570, 5.283), new Hit("damn", 5.350, 6.110, 13)],
            UsedCachedTranscript: false,
            ElapsedSeconds: 1.0);

        var innocent = new FixtureRun(
            new FixtureTruth("false_positive.mp3", "audio", 4.608, [new PlantedSpan("hoe", 2.634, 3.082, IsProfanity: false)]),
            Words: [],
            RawHits: [],
            FinalHits: [new Hit("hoe", 2.410, 3.210, 7)],
            UsedCachedTranscript: false,
            ElapsedSeconds: 1.0);

        _table = ReportFormatter.ToTable(EvaluationReport.Build([audiobook, innocent], new EvaluationRunInfo()));
    }

    [Fact]
    public void GivesTheStrayHitARow() => _table.ShouldContain("4.570-5.283");

    [Fact]
    public void MarksItAFalsePositive() => _table.Split('\n').Count(line => line.Contains("FALSE+", StringComparison.Ordinal)).ShouldBe(1);

    [Fact]
    public void ShowsTheInnocentReportOnceOnItsSpansRow() =>
        _table.Split('\n').Count(line => line.Contains("2.410-3.210", StringComparison.Ordinal)).ShouldBe(1);
}

/// <summary>A span nobody heard still appears in the table, marked as missed.</summary>
public sealed class AReportOfAMissedProfanity
{
    private readonly string _table;

    public AReportOfAMissedProfanity()
    {
        var run = new FixtureRun(
            new FixtureTruth("phrase_hit.mp3", "audio", 7.812, [new PlantedSpan("go to hell", 3.505, 4.227)]),
            Words: [],
            RawHits: [],
            FinalHits: [],
            UsedCachedTranscript: false,
            ElapsedSeconds: 1.0);

        _table = ReportFormatter.ToTable(EvaluationReport.Build([run], new EvaluationRunInfo()));
    }

    [Fact]
    public void SaysItWasMissed() => _table.ShouldContain("MISSED");

    [Fact]
    public void TabulatesTheRecall() => _table.ShouldContain("recall 0/1");
}
