using System.Globalization;
using System.Text;
using System.Text.Json;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Evaluation;

/// <summary>The settings a run was made with, recorded so two reports can be told apart.</summary>
public sealed record EvaluationRunInfo
{
    public string? Manifest { get; init; }

    public string? Model { get; init; }

    public string? Language { get; init; }

    public string? Device { get; init; }

    /// <summary>Which native Whisper.net runtime actually loaded (Cuda, Cpu, ...).</summary>
    public string? Runtime { get; init; }

    public string? BadWords { get; init; }

    public bool Rescan { get; init; }

    public bool SmartCutEnabled { get; init; }

    public string? StartedAt { get; init; }

    /// <summary>Wall clock for the whole run, model load included.</summary>
    public double? TotalSeconds { get; init; }
}

/// <summary>One level of scoring: the summary and every fixture behind it.</summary>
public sealed record LevelReport(ScoreCard Summary, IReadOnlyList<FixtureScore> Fixtures);

/// <summary>How long one fixture took, and whether its transcript was resumed rather than heard.</summary>
public sealed record FixtureTiming(
    string File,
    double ElapsedSeconds,
    bool UsedCachedTranscript,
    int Words
);

/// <summary>The whole evaluation, at both levels.</summary>
public sealed record EvaluationReport(
    EvaluationRunInfo Run,
    LevelReport RawWords,
    LevelReport FinalHits,
    IReadOnlyList<FixtureTiming> Timings
)
{
    /// <summary>
    /// Score <paramref name="runs"/> twice: the raw words against the padding
    /// still to come (the question ADR-0006 asked), and the final hits as they
    /// stand (the question a listener asks).
    /// </summary>
    public static EvaluationReport Build(IReadOnlyList<FixtureRun> runs, EvaluationRunInfo run)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(run);

        return new EvaluationReport(
            run,
            Level(runs, r => r.RawHits, ScoringRules.RawWords),
            Level(runs, r => r.FinalHits, ScoringRules.FinalHits),
            runs.Select(r => new FixtureTiming(
                    r.Fixture.File,
                    r.ElapsedSeconds,
                    r.UsedCachedTranscript,
                    r.Words.Count
                ))
                .ToList()
        );
    }

    private static LevelReport Level(
        IReadOnlyList<FixtureRun> runs,
        Func<FixtureRun, IReadOnlyList<Hit>> hits,
        ScoringRules rules
    )
    {
        var fixtures = runs.Select(r => BoundaryScorer.Score(r.Fixture, hits(r), rules)).ToList();
        return new LevelReport(ScoreCard.From(fixtures, rules), fixtures);
    }
}

/// <summary>Renders a report for people and for diffs.</summary>
public static class ReportFormatter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string ToJson(EvaluationReport report) => JsonSerializer.Serialize(report, Json);

    /// <summary>
    /// A table per level. Signs follow the scorer: a positive dStart is late and a
    /// negative dEnd is early, the two directions that leave speech audible; a
    /// negative margin is how much would be heard.
    /// </summary>
    public static string ToTable(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();

        Level(
            text,
            "Raw words - the transcriber's words before padding, margins assume the padding still to come",
            report.RawWords
        );
        text.AppendLine();
        Level(
            text,
            "Final hits - the padded, merged windows that actually get censored",
            report.FinalHits
        );

        return text.ToString();
    }

    private static void Level(StringBuilder text, string title, LevelReport level)
    {
        text.AppendLine(title);
        text.AppendLine(
            Row("fixture", "phrase", "truth", "reported", "dStart", "dEnd", "covered", "margin")
        );
        text.AppendLine(new string('-', 104));

        foreach (var fixture in level.Fixtures)
        {
            foreach (var span in fixture.Spans)
            {
                var phrase = span.Planted.IsProfanity
                    ? span.Planted.Phrase
                    : span.Planted.Phrase + " (innocent)";
                var truth = Window(span.Planted.Start, span.Planted.End);

                text.AppendLine(
                    span.Reported is { } reported
                        ? Row(
                            fixture.File,
                            phrase,
                            truth,
                            Window(reported.Start, reported.End),
                            Signed(span.StartError),
                            Signed(span.EndError),
                            span.Covered ? "yes" : "NO",
                            Signed(span.Margin)
                        )
                        : Row(
                            fixture.File,
                            phrase,
                            truth,
                            "MISSED",
                            string.Empty,
                            string.Empty,
                            "NO",
                            string.Empty
                        )
                );
            }

            // A report paired with an innocent span already has its row above,
            // marked innocent; only reports matching no plant at all get one here.
            foreach (
                var falsePositive in fixture.FalsePositives.Except(fixture.ExpectedFalsePositives)
            )
            {
                text.AppendLine(
                    Row(
                        fixture.File,
                        falsePositive.Phrase,
                        "(no plant)",
                        Window(falsePositive.Start, falsePositive.End),
                        string.Empty,
                        string.Empty,
                        "FALSE+",
                        string.Empty
                    )
                );
            }
        }

        var card = level.Summary;
        text.AppendLine(new string('-', 104));
        text.AppendLine(
            string.Create(
                Invariant,
                $"detection {card.Detected}/{card.Spans} ({card.Detection:0.000})   recall {card.Found}/{card.Planted} ({card.Recall:0.000})   precision {card.Reported - card.FalsePositives}/{card.Reported} ({card.Precision:0.000})   false positives {card.FalsePositives} ({card.ExpectedFalsePositives} expected)"
            )
        );
        text.AppendLine(
            string.Create(
                Invariant,
                $"|dStart| mean {Plain(card.StartError.MeanAbsolute)} max {Plain(card.StartError.MaxAbsolute)} signed mean {Signed(card.StartError.MeanSigned)}   within {card.Tolerance.Pre:0.00} s: {card.StartsWithinTolerance}/{card.Detected}"
            )
        );
        text.AppendLine(
            string.Create(
                Invariant,
                $"|dEnd|   mean {Plain(card.EndError.MeanAbsolute)} max {Plain(card.EndError.MaxAbsolute)} signed mean {Signed(card.EndError.MeanSigned)}   within {card.Tolerance.Post:0.00} s: {card.EndsWithinTolerance}/{card.Detected}"
            )
        );
        text.AppendLine(
            string.Create(
                Invariant,
                $"worst late start {Signed(card.WorstLateStart)}   worst early end {Signed(card.WorstEarlyEnd)}   covered {card.Covered}/{card.Spans}   worst margin start {Signed(card.WorstStartMargin)} end {Signed(card.WorstEndMargin)}"
            )
        );
    }

    private static string Row(
        string fixture,
        string phrase,
        string truth,
        string reported,
        string dStart,
        string dEnd,
        string covered,
        string margin
    ) =>
        string.Create(
            Invariant,
            $"{fixture, -20} {phrase, -17} {truth, -15} {reported, -25} {dStart, 7} {dEnd, 7} {covered, -7} {margin, 7}"
        );

    private static string Window(double start, double end) =>
        string.Create(Invariant, $"{start:0.000}-{end:0.000}");

    private static string Signed(double? value) =>
        value is { } v ? v.ToString("+0.000;-0.000;0.000", Invariant) : "n/a";

    private static string Plain(double? value) =>
        value is { } v ? v.ToString("0.000", Invariant) : "n/a";
}
