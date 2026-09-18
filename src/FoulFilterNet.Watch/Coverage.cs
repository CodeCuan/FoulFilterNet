using FoulFilterNet.Transcription;

namespace FoulFilterNet.Watch;

/// <summary>
/// One stretch of a Web Video's timeline whose Hits are final, in seconds. Both
/// ends are included, so a zero-length interval is a single covered point.
/// </summary>
/// <param name="From">Where the covered stretch begins.</param>
/// <param name="To">Where it ends; never before <paramref name="From"/>.</param>
public readonly record struct CoverageInterval(double From, double To)
{
    /// <summary>How many seconds the interval spans.</summary>
    public double Length => To - From;
}

/// <summary>
/// The Coverage of a Web Video: the parts of its timeline whose Hits are final,
/// given which transcription windows have finished. Immutable; a Watch Session
/// builds a new one after every window.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TranscriptionWindows.Plan"/> fixes every window's share
/// (<c>KeepFrom</c>-<c>KeepTo</c>) before anything is transcribed, and
/// <see cref="TranscriptionWindows.Stitch"/> keeps each word from exactly one
/// window. So a finished window's share is final on its own, whatever order the
/// windows ran in, and Coverage is the union of the finished shares, clamped to
/// <c>[0, duration]</c>. It is a set of intervals rather than a high-water mark
/// because a seek has windows transcribed out of order.
/// </para>
/// <para>
/// The one exception is an edge next to an unfinished window. A phrase can
/// straddle a share boundary (a word kept by each side), and a Hit's padding
/// reaches past its words, so the audio just inside the edge may yet be
/// censored once the neighbour is heard. Each run is therefore trimmed by a
/// guard (<see cref="DefaultGuardSeconds"/>) at such an edge. The start and end
/// of the file have no neighbour and are never trimmed, and nor is a run's edge
/// once clamping has put it at 0 or at the duration. A run too short for its
/// guards is dropped rather than kept as a sliver.
/// </para>
/// <para>
/// <b>Floating point.</b> No tolerance is needed: runs are merged by window
/// <em>index</em> (consecutive finished windows form one run), never by
/// comparing times. That is sound because the plan is checked to tile the
/// timeline exactly - each window's <c>KeepFrom</c> equals the previous one's
/// <c>KeepTo</c>, which <see cref="TranscriptionWindows.Plan"/> guarantees by
/// copying the value - so neighbouring shares meet with no gap for rounding to
/// open. <see cref="Contains"/> and <see cref="CoveredAheadOf"/> compare
/// exactly against the resulting ends.
/// </para>
/// </remarks>
public sealed class Coverage
{
    /// <summary>
    /// Seconds trimmed from a run at an edge that borders an unfinished window:
    /// enough for the padding of a Hit whose phrase runs across that edge.
    /// </summary>
    public const double DefaultGuardSeconds = 1.0;

    private readonly CoverageInterval[] _intervals;

    private Coverage(CoverageInterval[] intervals, double durationSeconds, bool isComplete)
    {
        _intervals = intervals;
        DurationSeconds = durationSeconds;
        IsComplete = isComplete;
    }

    /// <summary>Nothing covered, of a file of no known length.</summary>
    public static Coverage Empty { get; } = new([], 0.0, isComplete: false);

    /// <summary>The covered stretches, in order, apart and not touching.</summary>
    public IReadOnlyList<CoverageInterval> Intervals => _intervals;

    /// <summary>The length of the file the Coverage is of, in seconds.</summary>
    public double DurationSeconds { get; }

    /// <summary>
    /// Every window is finished, so the whole file, <c>[0, duration]</c>, is
    /// covered.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>Nothing is covered yet.</summary>
    public bool IsEmpty => _intervals.Length == 0;

    /// <summary>
    /// Coverage of a file of <paramref name="durationSeconds"/>, planned with
    /// <see cref="TranscriptionWindows.Plan"/>.
    /// </summary>
    /// <inheritdoc cref="From" path="/param"/>
    /// <inheritdoc cref="From" path="/exception"/>
    public static Coverage ForDuration(
        double durationSeconds,
        IEnumerable<int> finished,
        double guardSeconds = DefaultGuardSeconds
    )
    {
        // Checked before planning: Plan would never finish an infinite file.
        ThrowIfNegativeOrNotFinite(durationSeconds);
        return From(
            TranscriptionWindows.Plan(durationSeconds),
            durationSeconds,
            finished,
            guardSeconds
        );
    }

    /// <summary>
    /// Coverage of a file of <paramref name="durationSeconds"/>, transcribed
    /// with <paramref name="plan"/>, once the windows at the indices in
    /// <paramref name="finished"/> are done.
    /// </summary>
    /// <param name="plan">
    /// The windows, in order, whose shares tile the timeline: the first starts
    /// at or before 0, the last ends at or after the duration, and each starts
    /// exactly where the one before ends.
    /// </param>
    /// <param name="durationSeconds">The length of the file; finite, not negative.</param>
    /// <param name="finished">
    /// Indices into <paramref name="plan"/> of the finished windows, in any
    /// order; repeats are ignored.
    /// </param>
    /// <param name="guardSeconds">
    /// Seconds trimmed at an edge next to an unfinished window; finite, not
    /// negative.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A finished index is outside the plan, or the duration or guard is
    /// negative or not finite.
    /// </exception>
    /// <exception cref="ArgumentException">The plan is empty or its shares do not tile the file.</exception>
    public static Coverage From(
        IReadOnlyList<TranscriptionWindow> plan,
        double durationSeconds,
        IEnumerable<int> finished,
        double guardSeconds = DefaultGuardSeconds
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(finished);
        ThrowIfNegativeOrNotFinite(durationSeconds);
        ThrowIfNegativeOrNotFinite(guardSeconds);
        ThrowUnlessTiles(plan, durationSeconds);

        var done = new bool[plan.Count];
        foreach (var index in finished)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index, nameof(finished));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                index,
                plan.Count,
                nameof(finished)
            );
            done[index] = true;
        }

        if (Array.TrueForAll(done, d => d))
        {
            // Nothing is unfinished, so no edge is guarded - even on a
            // zero-length file, whose only point is then covered.
            return new([new CoverageInterval(0.0, durationSeconds)], durationSeconds, true);
        }

        var intervals = new List<CoverageInterval>();
        var i = 0;
        while (i < plan.Count)
        {
            if (!done[i])
            {
                i++;
                continue;
            }

            var first = i;
            while (i + 1 < plan.Count && done[i + 1])
            {
                i++;
            }

            var last = i;
            i++;

            var from = Clamp(plan[first].KeepFrom, durationSeconds);
            var to = Clamp(plan[last].KeepTo, durationSeconds);
            if (first > 0 && from > 0.0)
            {
                from += guardSeconds;
            }

            if (last < plan.Count - 1 && to < durationSeconds)
            {
                to -= guardSeconds;
            }

            // Only a whole finished file may be a single point; a run that its
            // guards (or clamping) leave with no length covers nothing.
            if (to > from)
            {
                intervals.Add(new CoverageInterval(from, to));
            }
        }

        return new([.. intervals], durationSeconds, false);
    }

    /// <summary>
    /// Whether all of <c>[<paramref name="from"/>, <paramref name="to"/>]</c>
    /// lies inside one covered interval, ends included. Equal ends ask about a
    /// single point. A stretch across a gap, or reaching before 0 or past the
    /// end, is not covered.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An end is NaN, or <paramref name="to"/> is before <paramref name="from"/>.
    /// </exception>
    public bool Contains(double from, double to)
    {
        ThrowIfNaN(from);
        ThrowIfNaN(to);
        if (to < from)
        {
            throw new ArgumentException(
                $"The stretch ends ({to}) before it starts ({from}).",
                nameof(to)
            );
        }

        return IntervalAt(from) is { } interval && to <= interval.To;
    }

    /// <summary>
    /// Seconds of unbroken Coverage from <paramref name="position"/> onwards:
    /// how far the playhead can go before it reaches something not yet final.
    /// Zero when <paramref name="position"/> itself is not covered, or is at
    /// the end of its interval.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="position"/> is NaN.</exception>
    public double CoveredAheadOf(double position)
    {
        ThrowIfNaN(position);
        return IntervalAt(position) is { } interval ? interval.To - position : 0.0;
    }

    private CoverageInterval? IntervalAt(double position)
    {
        foreach (var interval in _intervals)
        {
            if (position < interval.From)
            {
                return null;
            }

            if (position <= interval.To)
            {
                return interval;
            }
        }

        return null;
    }

    private static double Clamp(double seconds, double durationSeconds) =>
        Math.Clamp(seconds, 0.0, durationSeconds);

    private static void ThrowUnlessTiles(IReadOnlyList<TranscriptionWindow> plan, double duration)
    {
        if (plan.Count == 0)
        {
            throw new ArgumentException("The plan has no windows.", nameof(plan));
        }

        if (plan[0].KeepFrom > 0.0)
        {
            throw new ArgumentException(
                $"The first share starts at {plan[0].KeepFrom}, leaving the start of the file to no window.",
                nameof(plan)
            );
        }

        if (plan[^1].KeepTo < duration)
        {
            throw new ArgumentException(
                $"The last share ends at {plan[^1].KeepTo}, before the file does at {duration}.",
                nameof(plan)
            );
        }

        for (var i = 1; i < plan.Count; i++)
        {
            // Exact equality on purpose: Plan copies KeepTo into the next
            // KeepFrom, and merging by index relies on the shares meeting.
            if (plan[i].KeepFrom != plan[i - 1].KeepTo)
            {
                throw new ArgumentException(
                    $"Window {i}'s share starts at {plan[i].KeepFrom}, not where window {i - 1}'s ends ({plan[i - 1].KeepTo}).",
                    nameof(plan)
                );
            }
        }
    }

    private static void ThrowIfNegativeOrNotFinite(
        double seconds,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(seconds))]
            string? paramName = null
    )
    {
        if (!double.IsFinite(seconds) || seconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                seconds,
                "Must be a finite, non-negative number of seconds."
            );
        }
    }

    private static void ThrowIfNaN(
        double seconds,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(seconds))]
            string? paramName = null
    )
    {
        if (double.IsNaN(seconds))
        {
            throw new ArgumentException("Must be a number of seconds, not NaN.", paramName);
        }
    }
}
