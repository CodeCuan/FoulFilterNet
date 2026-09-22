using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Watch;

/// <summary>
/// Everything a Watch Session has heard of one Web Video so far: the window
/// plan, the duration, the Bad Words List and what each finished window
/// transcribed, from which it builds the <see cref="HitSnapshot"/> the
/// extension is served. Immutable: <see cref="With"/> and
/// <see cref="WithBadWords"/> return a new instance, so a snapshot can be
/// handed to any number of readers while the session moves on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The batch pipeline's rules, unchanged.</b> Hits come from
/// <see cref="PhraseMatcher.FindCandidates"/>, <see cref="HitReconciler"/> and
/// <see cref="HitMerger"/>, sharing one <see cref="Domain.CutPadding"/> exactly as
/// <see cref="MediaPipeline"/> does (priority words cut wider; X03), over the
/// finished windows stitched with
/// <see cref="TranscriptionWindows.Stitch"/>. Recomputing from the whole
/// transcript so far after every window costs milliseconds, and it is what
/// lets an edited Bad Words List take effect without transcribing again. There
/// is no alignment stage: Whisper.net returns Words with every window (ADR-0006),
/// and a window that heard none leaves its Candidates to the reconciler's
/// segment estimate, as a batch job would. Smart Cut and the Rescan Pass are not
/// applied to web video (V1).
/// </para>
/// <para>
/// <b>One run at a time.</b> The rules run separately over each run of
/// consecutive finished windows, and the Hits of every run are merged
/// together at the end. Stitching windows 0 and 2 without 1 would put the last
/// Word of window 0's share next to the first of window 2's, with half a minute
/// nobody has heard between them, and a phrase could match across the gap as
/// one Hit spanning it. Within a run nothing is missing, so the rules see what
/// the batch would. When every window is finished there is one run, and the
/// result is <em>exactly</em> the batch pipeline's; windows finishing in any
/// order give the same snapshot, because runs and stitching go by window index.
/// A Hit's <see cref="Hit.WordIndex"/> is moved from its run's Words to the
/// whole <see cref="HitSnapshot.Transcript"/>'s.
/// </para>
/// <para>
/// <b>A phrase across a share boundary</b> needs no special case. Each Word is
/// kept by the one window whose share holds its midpoint, so a phrase whose
/// Words fall in two shares only exists in the Words once both windows are in
/// the same run. Before that, the one exception is a Segment: whisper puts a
/// Segment in the window that keeps its midpoint, and that window heard the
/// whole Segment, so a Candidate in it that its own Words cannot confirm yet
/// falls back to the Segment estimate, as the batch does for any unconfirmed
/// Candidate. It over-censors briefly and is replaced by the Words' Hit once the
/// neighbour is done. <see cref="Coverage"/>'s guard at an unfinished edge is
/// what keeps all of this from reaching audio the viewer is allowed to hear.
/// </para>
/// <para>
/// <b>Revision.</b> The accumulator owns it: 0 at <see cref="Start(double, BadWordsList)"/>,
/// and one more for every <see cref="With"/> and for every
/// <see cref="WithBadWords"/> that changes the phrases. So a session that swaps
/// in each new instance can answer "anything since revision <c>n</c>?" by
/// comparing numbers, and an unchanged list reread from disk costs nothing.
/// </para>
/// <para>
/// <b>A window is finished once.</b> Passing a result for a window already
/// finished throws <see cref="InvalidOperationException"/> rather than
/// replacing it. Coverage promises the finished shares' Hits are final - the
/// viewer may already have heard that stretch played through them - and a
/// session has no reason to transcribe a window twice, so a second result is
/// a bug in the caller.
/// </para>
/// </remarks>
public sealed class WatchProgress
{
    /// <summary>
    /// One padding for both, as in <see cref="MediaPipeline"/>: the reconciler's
    /// tolerance is derived from the padding the merger applies.
    /// </summary>
    private readonly HitReconciler _reconciler;

    private readonly HitMerger _merger;

    /// <summary>What each window heard, on its own timeline; null until finished.</summary>
    private readonly TranscriptionResult?[] _results;

    private readonly Lazy<HitSnapshot> _snapshot;

    private WatchProgress(
        IReadOnlyList<TranscriptionWindow> plan,
        double durationSeconds,
        BadWordsList badWords,
        TranscriptionResult?[] results,
        long revision,
        Coverage coverage,
        CutPadding padding
    )
    {
        Plan = plan;
        CutPadding = padding;
        _reconciler = new HitReconciler(padding);
        _merger = new HitMerger(padding);
        DurationSeconds = durationSeconds;
        BadWords = badWords;
        _results = results;
        Revision = revision;
        Coverage = coverage;
        FinishedWindows =
        [
            .. Enumerable.Range(0, results.Length).Where(i => results[i] is not null),
        ];
        _snapshot = new Lazy<HitSnapshot>(BuildSnapshot);
    }

    /// <summary>The windows the video is transcribed in, in order.</summary>
    public IReadOnlyList<TranscriptionWindow> Plan { get; }

    /// <summary>The length of the video, in seconds.</summary>
    public double DurationSeconds { get; }

    /// <summary>The list the Hits are found with.</summary>
    public BadWordsList BadWords { get; }

    /// <summary>How the Hits are padded: priority words wider, as a job pads them.</summary>
    public CutPadding CutPadding { get; }

    /// <summary>
    /// The Coverage guard for <paramref name="padding"/>: the default 1 s, or
    /// wider when a short priority Hit can reach further back than that from
    /// its end (0.8 s minimum + 0.25 s pre-roll = 1.05 s by default). A Word is
    /// kept by the share holding its midpoint, so a 10 ms Hit just inside an
    /// unfinished neighbour can still cut that far into this side of the edge.
    /// </summary>
    public static double GuardSecondsFor(CutPadding padding)
    {
        ArgumentNullException.ThrowIfNull(padding);
        return Math.Max(Coverage.DefaultGuardSeconds, padding.ShortHitReachSeconds);
    }

    /// <summary>
    /// How many changes this instance is from its <see cref="Start(double, BadWordsList)"/>:
    /// one per finished window and one per change of Bad Words List.
    /// </summary>
    public long Revision { get; }

    /// <summary>The indices of the finished windows, ascending, whatever order they finished in.</summary>
    public IReadOnlyList<int> FinishedWindows { get; }

    /// <summary>The Coverage of the finished windows, with the default guard.</summary>
    public Coverage Coverage { get; }

    /// <summary>
    /// The Hits, Coverage and transcript at this revision. Built on first read
    /// and then kept, so every poll between two changes is served the same
    /// instance.
    /// </summary>
    public HitSnapshot Snapshot => _snapshot.Value;

    /// <summary>
    /// A Watch Session's starting point for a video of
    /// <paramref name="durationSeconds"/>, planned with
    /// <see cref="TranscriptionWindows.Plan"/>: nothing heard, revision 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The duration is negative, NaN or infinite.
    /// </exception>
    public static WatchProgress Start(
        double durationSeconds,
        BadWordsList badWords,
        CutPadding? padding = null
    )
    {
        ArgumentNullException.ThrowIfNull(badWords);

        // Checked before planning: Plan would never finish an infinite file.
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                durationSeconds,
                "Must be a finite, non-negative number of seconds."
            );
        }

        return Start(
            TranscriptionWindows.Plan(durationSeconds),
            durationSeconds,
            badWords,
            padding
        );
    }

    /// <summary>
    /// A Watch Session's starting point for a video of
    /// <paramref name="durationSeconds"/> transcribed with
    /// <paramref name="plan"/>: nothing heard, revision 0.
    /// </summary>
    /// <param name="plan">The windows, whose shares tile the file, as <see cref="Coverage.From"/> requires.</param>
    /// <param name="durationSeconds">The length of the video; finite, not negative.</param>
    /// <param name="badWords">The list to find Hits with.</param>
    /// <param name="padding">How to pad the Hits; <see cref="CutPadding.Default"/> when null.</param>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or not finite.</exception>
    /// <exception cref="ArgumentException">The plan is empty or its shares do not tile the file.</exception>
    public static WatchProgress Start(
        IReadOnlyList<TranscriptionWindow> plan,
        double durationSeconds,
        BadWordsList badWords,
        CutPadding? padding = null
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(badWords);

        // Coverage checks the duration and that the plan tiles the file, and
        // every later instance is built from a plan that has passed it.
        var effective = padding ?? CutPadding.Default;
        var coverage = Coverage.From(plan, durationSeconds, [], GuardSecondsFor(effective));
        return new(
            [.. plan],
            durationSeconds,
            badWords,
            new TranscriptionResult?[plan.Count],
            0,
            coverage,
            effective
        );
    }

    /// <summary>
    /// The finished progress of a video whose whole Transcript is already known
    /// - a Transcript cache hit - so a cached video is served through exactly
    /// the same rules, snapshot and <see cref="WithBadWords"/> as one heard
    /// window by window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cached Transcript carries no per-window results, so it is treated as
    /// one window spanning the file with an unbounded share: stitching it keeps
    /// every Segment and Word where it is, and the rules run once over the
    /// whole of it, which is the batch pipeline's result exactly. Revision is 1
    /// (one "window" finished) and the snapshot reports 1 of 1 windows.
    /// </para>
    /// <para>
    /// <b>Duration.</b> A Transcript does not record the video's length, so it
    /// is taken as where the last Segment or Word ends (0 for an empty one), and
    /// Coverage is complete over <c>[0, that]</c>. The real video can run on
    /// past its last word; a client must read a complete snapshot as covering
    /// the whole video, not only its intervals.
    /// </para>
    /// </remarks>
    /// <param name="transcript">The Segments and Words, on the file's timeline.</param>
    /// <param name="badWords">The list to find Hits with.</param>
    /// <param name="padding">How to pad the Hits; <see cref="CutPadding.Default"/> when null.</param>
    public static WatchProgress FromTranscript(
        TranscriptionResult transcript,
        BadWordsList badWords,
        CutPadding? padding = null
    )
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(badWords);

        var duration = Math.Max(
            0.0,
            Math.Max(
                transcript.Segments.Select(s => s.End).DefaultIfEmpty(0.0).Max(),
                transcript.Words.Select(w => w.End).DefaultIfEmpty(0.0).Max()
            )
        );

        TranscriptionWindow[] whole =
        [
            new(0.0, duration, double.NegativeInfinity, double.PositiveInfinity),
        ];
        return Start(whole, duration, badWords, padding).With(0, transcript);
    }

    /// <summary>Whether the window at <paramref name="index"/> is finished.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the plan.</exception>
    public bool IsFinished(int index)
    {
        ThrowUnlessInPlan(index);
        return _results[index] is not null;
    }

    /// <summary>
    /// This progress with the window at <paramref name="index"/> finished,
    /// having heard <paramref name="heard"/>, one revision on.
    /// </summary>
    /// <param name="index">The window's index in <see cref="Plan"/>.</param>
    /// <param name="heard">
    /// What the engine transcribed, on the window's own timeline (starting at
    /// zero at the window's <c>Start</c>), including whatever it heard outside
    /// the window's share; stitching keeps only the share.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the plan.</exception>
    /// <exception cref="InvalidOperationException">The window is already finished.</exception>
    public WatchProgress With(int index, TranscriptionResult heard)
    {
        ThrowUnlessInPlan(index);
        ArgumentNullException.ThrowIfNull(heard);

        if (_results[index] is not null)
        {
            throw new InvalidOperationException(
                $"Window {index} is already finished; a finished window's Hits are final."
            );
        }

        var results = (TranscriptionResult?[])_results.Clone();
        results[index] = heard;

        var finished = Enumerable.Range(0, results.Length).Where(i => results[i] is not null);
        var coverage = Coverage.From(Plan, DurationSeconds, finished, GuardSecondsFor(CutPadding));

        return new(Plan, DurationSeconds, BadWords, results, Revision + 1, coverage, CutPadding);
    }

    /// <summary>
    /// This progress finding Hits with <paramref name="badWords"/> instead,
    /// one revision on - or this same instance when the list holds the same
    /// phrases, so rereading an unchanged file does not look like a change.
    /// </summary>
    public WatchProgress WithBadWords(BadWordsList badWords)
    {
        ArgumentNullException.ThrowIfNull(badWords);

        if (
            ReferenceEquals(badWords, BadWords)
            || (badWords.Count == BadWords.Count && badWords.Phrases.All(BadWords.Contains))
        )
        {
            return this;
        }

        return new(Plan, DurationSeconds, badWords, _results, Revision + 1, Coverage, CutPadding);
    }

    /// <summary>
    /// This progress over <paramref name="plan"/> and
    /// <paramref name="durationSeconds"/> instead, keeping every finished
    /// window's result, one revision on - or this same instance when the plan
    /// and duration are the ones it already has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// W17: a Watch Session hears the first windows of a long video from the
    /// head of its audio, against a provisional plan, before the whole WAV
    /// exists to say how long the video really is. Those are windows
    /// <see cref="TranscriptionWindows.FixedPrefixCount"/> guarantees the real
    /// plan has too, so when the whole WAV arrives the session swaps the real
    /// plan in here and carries on.
    /// </para>
    /// <para>
    /// Every finished window must be in the new plan exactly as it is in this
    /// one - same audio, same share - because what it heard and the Hits it
    /// gave are final. Anything else throws rather than quietly re-timing
    /// heard words. So the result is exactly what hearing the same windows
    /// against <paramref name="plan"/> from the start would have given.
    /// </para>
    /// </remarks>
    /// <param name="plan">The windows, whose shares tile the file, as <see cref="Coverage.From"/> requires.</param>
    /// <param name="durationSeconds">The length of the video; finite, not negative.</param>
    /// <exception cref="ArgumentException">
    /// A finished window is missing from <paramref name="plan"/> or planned
    /// differently there, or the plan does not tile the file.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative or not finite.</exception>
    public WatchProgress WithPlan(IReadOnlyList<TranscriptionWindow> plan, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var index in FinishedWindows)
        {
            if (index >= plan.Count || plan[index] != Plan[index])
            {
                throw new ArgumentException(
                    $"Window {index} is finished, so the new plan must hold it unchanged "
                        + $"({Plan[index]}); it holds "
                        + (index >= plan.Count ? "no such window." : $"{plan[index]}."),
                    nameof(plan)
                );
            }
        }

        // Checks the duration and that the plan tiles the file.
        var coverage = Coverage.From(
            plan,
            durationSeconds,
            FinishedWindows,
            GuardSecondsFor(CutPadding)
        );

        if (durationSeconds.Equals(DurationSeconds) && plan.SequenceEqual(Plan))
        {
            return this;
        }

        var results = new TranscriptionResult?[plan.Count];
        foreach (var index in FinishedWindows)
        {
            results[index] = _results[index];
        }

        return new(
            [.. plan],
            durationSeconds,
            BadWords,
            results,
            Revision + 1,
            coverage,
            CutPadding
        );
    }

    private HitSnapshot BuildSnapshot()
    {
        var segments = new List<Segment>();
        var words = new List<Word>();
        var hits = new List<Hit>();

        foreach (var run in Runs())
        {
            var heard = TranscriptionWindows.Stitch(run);
            var candidates = PhraseMatcher.FindCandidates(heard.Segments, BadWords);

            foreach (var hit in _reconciler.Reconcile(candidates, heard.Words, BadWords))
            {
                hits.Add(
                    hit.WordIndex is { } wordIndex
                        ? hit with
                        {
                            WordIndex = wordIndex + words.Count,
                        }
                        : hit
                );
            }

            segments.AddRange(heard.Segments);
            words.AddRange(heard.Words);
        }

        return new HitSnapshot(
            Revision,
            Coverage,
            _merger.Merge(hits),
            new TranscriptionResult(segments, words),
            FinishedWindows.Count,
            Plan.Count
        );
    }

    /// <summary>Each run of consecutive finished windows, with what they heard, in index order.</summary>
    private IEnumerable<List<(TranscriptionWindow, TranscriptionResult)>> Runs()
    {
        var run = new List<(TranscriptionWindow, TranscriptionResult)>();
        for (var i = 0; i < _results.Length; i++)
        {
            if (_results[i] is { } heard)
            {
                run.Add((Plan[i], heard));
                continue;
            }

            if (run.Count > 0)
            {
                yield return run;
                run = [];
            }
        }

        if (run.Count > 0)
        {
            yield return run;
        }
    }

    private void ThrowUnlessInPlan(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _results.Length);
    }
}
