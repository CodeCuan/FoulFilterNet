using FoulFilterNet.Domain;

namespace FoulFilterNet.Evaluation;

/// <summary>Summary statistics of one signed error over every span that was found.</summary>
/// <param name="MeanAbsolute">Mean magnitude, or null when nothing was found.</param>
/// <param name="MaxAbsolute">Largest magnitude.</param>
/// <param name="MeanSigned">Mean with direction kept: the bias.</param>
public sealed record ErrorStatistics(double? MeanAbsolute, double? MaxAbsolute, double? MeanSigned);

/// <summary>Detection, recall, precision and boundary error over a set of scored fixtures.</summary>
/// <remarks>
/// Two denominators, on purpose. <see cref="Detection"/> and every boundary
/// figure count <em>every</em> planted span, the innocent ones included: the
/// manifest's timing is exact for all of them, a boundary error is the engine's
/// whatever the word means, and that is how ADR-0006 counted its eleven.
/// <see cref="Recall"/> and <see cref="Precision"/> are about profanity only, so a
/// flagged garden hoe is a false positive there.
/// </remarks>
public sealed record ScoreCard
{
    /// <summary>Every planted span, innocent or not.</summary>
    public int Spans { get; init; }

    /// <summary>Planted spans some report was paired with.</summary>
    public int Detected { get; init; }

    public double Detection { get; init; }

    /// <summary>Planted profanities.</summary>
    public int Planted { get; init; }

    public int Found { get; init; }

    public int Reported { get; init; }

    public int FalsePositives { get; init; }

    public int ExpectedFalsePositives { get; init; }

    public double Recall { get; init; }

    public double Precision { get; init; }

    public required ErrorStatistics StartError { get; init; }

    public required ErrorStatistics EndError { get; init; }

    /// <summary>The latest start, in seconds after the planted start. Negative means every start was early.</summary>
    public double? WorstLateStart { get; init; }

    /// <summary>The earliest end, in seconds before the planted end. Negative means every end was late.</summary>
    public double? WorstEarlyEnd { get; init; }

    /// <summary>The padding errors were measured against, from <see cref="ScoringRules.Tolerance"/>.</summary>
    public required HitPadding Tolerance { get; init; }

    public int StartsWithinTolerance { get; init; }

    public int EndsWithinTolerance { get; init; }

    public int Covered { get; init; }

    public double? WorstStartMargin { get; init; }

    public double? WorstEndMargin { get; init; }

    /// <summary>
    /// Aggregate <paramref name="fixtures"/>. Boundary figures cover every
    /// detected span. With nothing to divide by, recall and precision
    /// are 1 and the boundary figures are null - never NaN in a report meant to
    /// be diffed.
    /// </summary>
    public static ScoreCard From(IReadOnlyList<FixtureScore> fixtures, ScoringRules rules)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(rules);

        var spans = fixtures.SelectMany(f => f.Spans).ToList();
        var detected = spans.Where(s => s.Found).ToList();
        var planted = spans.Where(s => s.Planted.IsProfanity).ToList();
        var found = planted.Where(s => s.Found).ToList();
        var reported = fixtures.Sum(f => f.Reported.Count);
        var falsePositives = fixtures.Sum(f => f.FalsePositives.Count);

        return new ScoreCard
        {
            Tolerance = rules.Tolerance,
            Spans = spans.Count,
            Detected = detected.Count,
            Detection = Ratio(detected.Count, spans.Count),
            Planted = planted.Count,
            Found = found.Count,
            Reported = reported,
            FalsePositives = falsePositives,
            ExpectedFalsePositives = fixtures.Sum(f => f.ExpectedFalsePositives.Count),
            Recall = Ratio(found.Count, planted.Count),
            Precision = Ratio(reported - falsePositives, reported),
            StartError = Statistics(detected.Select(s => s.StartError!.Value).ToList()),
            EndError = Statistics(detected.Select(s => s.EndError!.Value).ToList()),
            WorstLateStart = Max(detected.Select(s => s.StartError!.Value)),
            WorstEarlyEnd = Max(detected.Select(s => -s.EndError!.Value)),
            StartsWithinTolerance = detected.Count(s => s.StartWithinTolerance),
            EndsWithinTolerance = detected.Count(s => s.EndWithinTolerance),
            Covered = detected.Count(s => s.Covered),
            WorstStartMargin = Min(detected.Select(s => s.StartMargin!.Value)),
            WorstEndMargin = Min(detected.Select(s => s.EndMargin!.Value)),
        };
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 1.0 : Times.Round((double)numerator / denominator);

    private static ErrorStatistics Statistics(List<double> errors) =>
        errors.Count == 0
            ? new ErrorStatistics(null, null, null)
            : new ErrorStatistics(
                Times.Round(errors.Average(Math.Abs)),
                Times.Round(errors.Max(Math.Abs)),
                Times.Round(errors.Average())
            );

    private static double? Max(IEnumerable<double> values) =>
        values.Select(v => (double?)v).Max() is { } max ? Times.Round(max) : null;

    private static double? Min(IEnumerable<double> values) =>
        values.Select(v => (double?)v).Min() is { } min ? Times.Round(min) : null;
}
