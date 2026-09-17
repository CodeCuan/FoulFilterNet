using FoulFilterNet.Domain;

namespace FoulFilterNet.Evaluation;

/// <summary>
/// A span of the fixture audio whose phrase and timing are known by
/// construction. <paramref name="IsProfanity"/> is false for a word planted to
/// be flagged wrongly - <c>false_positive.mp3</c>'s garden hoe.
/// </summary>
public sealed record PlantedSpan(string Phrase, double Start, double End, bool IsProfanity = true);

/// <summary>One fixture file and everything planted in it.</summary>
public sealed record FixtureTruth(string File, string Kind, double Duration, IReadOnlyList<PlantedSpan> Spans);

/// <summary>How a list of reported spans is compared with the ground truth.</summary>
public sealed record ScoringRules
{
    /// <summary>
    /// Padding the pipeline has yet to apply to what is being scored. Raw words
    /// are still to be padded, so whether they will be covered depends on it;
    /// final hits already have been.
    /// </summary>
    public required HitPadding PaddingStillToApply { get; init; }

    /// <summary>
    /// The magnitude an error may have and still count as "inside the padding",
    /// as ADR-0006's table counted it - regardless of direction.
    /// </summary>
    public HitPadding Tolerance { get; init; } = HitPadding.Default;

    /// <summary>
    /// How far apart a report and a planted span may be and still be paired.
    /// Further than this, the report is a false positive and the span a miss,
    /// rather than one enormous boundary error.
    /// </summary>
    public double MatchWindowSeconds { get; init; } = 1.0;

    /// <summary>The transcriber's words, before padding and merging (what ADR-0006 measured).</summary>
    public static ScoringRules RawWords { get; } = new() { PaddingStillToApply = HitPadding.Default };

    /// <summary>The pipeline's padded and merged windows (what actually gets censored).</summary>
    public static ScoringRules FinalHits { get; } = new() { PaddingStillToApply = new HitPadding(0, 0) };
}

/// <summary>
/// One planted span and the report paired with it, if any.
/// </summary>
/// <param name="StartError">
/// Reported start minus planted start. <b>Positive is late</b>, and a late start
/// leaves the beginning of the word audible.
/// </param>
/// <param name="EndError">
/// Reported end minus planted end. <b>Negative is early</b>, and an early end
/// leaves the tail of the word audible.
/// </param>
/// <param name="StartMargin">
/// How far before the planted start the window begins once the padding still to
/// apply is added. Negative is the length of speech left audible.
/// </param>
/// <param name="EndMargin">The same at the end.</param>
public sealed record SpanScore(
    PlantedSpan Planted,
    Hit? Reported,
    double? StartError,
    double? EndError,
    double? StartMargin,
    double? EndMargin,
    bool StartWithinTolerance,
    bool EndWithinTolerance)
{
    public bool Found => Reported is not null;

    /// <summary>The padded window contains the whole planted span.</summary>
    public bool Covered => StartMargin >= 0 && EndMargin >= 0;

    /// <summary>The tighter of the two margins.</summary>
    public double? Margin => StartMargin is { } start && EndMargin is { } end ? Math.Min(start, end) : null;
}

/// <summary>Every planted span of one fixture, scored, and the reports that matched none.</summary>
/// <param name="FalsePositives">Reports paired with no profanity, innocent spans included.</param>
/// <param name="ExpectedFalsePositives">The subset paired with a span planted to be flagged wrongly.</param>
public sealed record FixtureScore(
    string File,
    IReadOnlyList<SpanScore> Spans,
    IReadOnlyList<Hit> Reported,
    IReadOnlyList<Hit> FalsePositives,
    IReadOnlyList<Hit> ExpectedFalsePositives);

/// <summary>
/// Compares a list of reported spans with a fixture's ground truth. Pure: no
/// FFmpeg, no model, no GPU.
/// </summary>
/// <remarks>
/// <para>
/// Each planted span is paired with the nearest report of the same phrase, as
/// ADR-0006 paired them by hand, so the five identical words of
/// <c>repeated_hits.mp3</c> each find their own report. Pairing is greedy over
/// every eligible (span, report) pair, nearest first, where distance is
/// |Δstart| + |Δend|. A report is eligible for a span when it carries the span's
/// phrase and the two lie within <see cref="ScoringRules.MatchWindowSeconds"/>
/// of each other.
/// </para>
/// <para>
/// A report can be claimed once per phrase it carries. A raw hit carries one;
/// a window the merger fused carries every phrase joined into it with <c>+</c>,
/// because it really does cover each of those plants.
/// </para>
/// </remarks>
public static class BoundaryScorer
{
    public static FixtureScore Score(FixtureTruth fixture, IReadOnlyList<Hit> reported, ScoringRules rules)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(reported);
        ArgumentNullException.ThrowIfNull(rules);

        var spans = fixture.Spans;
        var carried = reported.Select(PhrasesCarried).ToList();

        var pairs = new List<(int Span, int Report, double Distance)>();
        for (var s = 0; s < spans.Count; s++)
        {
            var phrase = Tokenizer.Normalize(spans[s].Phrase);
            for (var r = 0; r < reported.Count; r++)
            {
                if (carried[r].ContainsKey(phrase) && Gap(spans[s], reported[r]) <= rules.MatchWindowSeconds)
                {
                    var distance = Math.Abs(reported[r].Start - spans[s].Start) + Math.Abs(reported[r].End - spans[s].End);
                    pairs.Add((s, r, distance));
                }
            }
        }

        var pairedWith = new int?[spans.Count];
        var claimedBy = reported.Select(_ => new List<int>()).ToList();

        foreach (var (s, r, _) in pairs.OrderBy(p => p.Distance).ThenBy(p => p.Span).ThenBy(p => p.Report))
        {
            var phrase = Tokenizer.Normalize(spans[s].Phrase);
            if (pairedWith[s] is not null || carried[r][phrase] == 0)
            {
                continue;
            }

            pairedWith[s] = r;
            carried[r][phrase]--;
            claimedBy[r].Add(s);
        }

        var scores = new SpanScore[spans.Count];
        for (var s = 0; s < spans.Count; s++)
        {
            scores[s] = pairedWith[s] is { } r
                ? Measure(spans[s], reported[r], rules)
                : new SpanScore(spans[s], null, null, null, null, null, false, false);
        }

        var falsePositives = new List<Hit>();
        var expected = new List<Hit>();
        for (var r = 0; r < reported.Count; r++)
        {
            if (claimedBy[r].Exists(s => spans[s].IsProfanity))
            {
                continue;
            }

            falsePositives.Add(reported[r]);
            if (claimedBy[r].Count > 0)
            {
                expected.Add(reported[r]);
            }
        }

        return new FixtureScore(fixture.File, scores, reported, falsePositives, expected);
    }

    private static SpanScore Measure(PlantedSpan planted, Hit report, ScoringRules rules)
    {
        var padding = rules.PaddingStillToApply;
        var startError = Times.Round(report.Start - planted.Start);
        var endError = Times.Round(report.End - planted.End);

        return new SpanScore(
            planted,
            report,
            startError,
            endError,
            StartMargin: Times.Round(planted.Start - (report.Start - padding.Pre)),
            EndMargin: Times.Round(report.End + padding.Post - planted.End),
            StartWithinTolerance: Math.Abs(startError) <= rules.Tolerance.Pre,
            EndWithinTolerance: Math.Abs(endError) <= rules.Tolerance.Post);
    }

    /// <summary>How many times each phrase appears in a report, merged windows included.</summary>
    private static Dictionary<string, int> PhrasesCarried(Hit report) =>
        report.Phrase.Split('+')
            .Select(Tokenizer.Normalize)
            .GroupBy(phrase => phrase, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>Seconds between two spans; zero when they overlap.</summary>
    private static double Gap(PlantedSpan planted, Hit report) =>
        Math.Max(0.0, Math.Max(planted.Start, report.Start) - Math.Min(planted.End, report.End));
}
