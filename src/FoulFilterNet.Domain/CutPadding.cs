namespace FoulFilterNet.Domain;

/// <summary>
/// How each Hit's cut window is widened: <see cref="HitPadding.Default"/> for
/// ordinary phrases, and a wider padding plus a minimum length for a Hit on a
/// Priority Word List word (docs/05-crosstalk-plan.md, "Priority padding").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why priority words are cut wider.</b> The F-word family is what the
/// Priority Word Pass hunts for under crosstalk, where DTW word timings are
/// loose: measured on <c>tests/fixtures/crosstalk</c> (large-v3-turbo), a
/// priority word that kept a real length ended up to 0.44 s early, and one
/// masked by the other speaker collapsed to a 10 ms point sitting at or up to
/// 0.47 s <em>after</em> the word's true end. So a priority Hit shorter than
/// <see cref="PriorityMinimumSeconds"/> is first grown <em>backward</em> from
/// its reported end to that length, then padded with <see cref="Priority"/>.
/// The primary pass's F-words get the same treatment: they are just as loose
/// under crosstalk, and a Word carries no record of which pass heard it.
/// </para>
/// <para>
/// <b>Which Hits.</b> One whose phrase normalizes to a single token on the
/// Priority Word List. A window the merger fused carries its phrases joined
/// with <c>+</c>; it counts when any of them does (Smart Cut re-pads such
/// windows).
/// </para>
/// <para>
/// <see cref="Widen"/> does not clamp at zero: the merger clamps, and the
/// evaluator wants the unclamped margin. The ordinary padding is unchanged, so
/// every filtergraph built from ordinary Hits is byte-for-byte what it was.
/// </para>
/// </remarks>
public sealed class CutPadding
{
    /// <summary>
    /// Padding for a priority Hit: 0.25 s before, 0.5 s after. The post-roll is
    /// twice the ordinary one because priority words under crosstalk were
    /// measured ending up to 0.44 s early; the pre-roll is the ordinary
    /// post-roll, the minimum length doing the heavy lifting at the start.
    /// </summary>
    public static HitPadding DefaultPriorityPadding { get; } = new(0.25, 0.5);

    /// <summary>
    /// The shortest a priority Hit is taken to be before padding: 0.8 s, a
    /// spoken F-word (0.5-0.66 s in the fixtures) plus the drift of a collapsed
    /// DTW timing past its end. With the 0.25 s pre-roll it reaches 1.05 s back
    /// from a 10 ms Hit's end, which covers the worst case measured (a "fuck"
    /// that ended 0.47 s before its reported point).
    /// </summary>
    public const double DefaultPriorityMinimumSeconds = 0.8;

    private readonly bool _hasPriority;

    /// <summary>
    /// Pad ordinary Hits with <paramref name="ordinary"/>; Hits on
    /// <paramref name="priorityWords"/> are first grown backward to
    /// <paramref name="priorityMinimumSeconds"/>, then padded with
    /// <paramref name="priority"/> (or <paramref name="ordinary"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The minimum is negative or not finite.</exception>
    public CutPadding(
        HitPadding ordinary,
        PriorityWordList? priorityWords = null,
        HitPadding? priority = null,
        double priorityMinimumSeconds = 0.0
    )
    {
        ArgumentNullException.ThrowIfNull(ordinary);
        if (!double.IsFinite(priorityMinimumSeconds) || priorityMinimumSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(priorityMinimumSeconds),
                priorityMinimumSeconds,
                "Must be a finite, non-negative number of seconds."
            );
        }

        Ordinary = ordinary;
        PriorityWords = priorityWords ?? PriorityWordList.FromLines([]);
        Priority = priority ?? ordinary;
        PriorityMinimumSeconds = priorityMinimumSeconds;
        _hasPriority = !PriorityWords.IsEmpty;
    }

    /// <summary><see cref="HitPadding.Default"/> for every Hit: no priority words.</summary>
    public static CutPadding Default { get; } = new(HitPadding.Default);

    /// <summary>No widening at all: for windows that have already been padded.</summary>
    public static CutPadding None { get; } = new(new HitPadding(0.0, 0.0));

    /// <summary>
    /// The padding a job uses with <paramref name="priorityWords"/>: the
    /// defaults above for its words, <see cref="HitPadding.Default"/> for
    /// everything else. An empty list is <see cref="Default"/>.
    /// </summary>
    public static CutPadding ForPriorityWords(PriorityWordList priorityWords)
    {
        ArgumentNullException.ThrowIfNull(priorityWords);

        return priorityWords.IsEmpty
            ? Default
            : new CutPadding(
                HitPadding.Default,
                priorityWords,
                DefaultPriorityPadding,
                DefaultPriorityMinimumSeconds
            );
    }

    /// <summary>The padding for every Hit not on the Priority Word List.</summary>
    public HitPadding Ordinary { get; }

    /// <summary>The padding for a Hit on the Priority Word List.</summary>
    public HitPadding Priority { get; }

    /// <summary>The shortest a priority Hit is taken to be, before padding.</summary>
    public double PriorityMinimumSeconds { get; }

    /// <summary>The words whose Hits get <see cref="Priority"/>; empty for none.</summary>
    public PriorityWordList PriorityWords { get; }

    /// <summary>
    /// How far before a Hit's reported end its cut window can begin when the
    /// Hit is (nearly) instantaneous: the minimum length plus the pre-roll for
    /// a priority Hit, the ordinary pre-roll otherwise. Watch's Coverage guard
    /// must be at least this wide.
    /// </summary>
    public double ShortHitReachSeconds =>
        _hasPriority ? Math.Max(Ordinary.Pre, PriorityMinimumSeconds + Priority.Pre) : Ordinary.Pre;

    /// <summary>
    /// True when <paramref name="phrase"/> (or, for a merged window, any of
    /// its <c>+</c>-joined phrases) normalizes to one token on the list.
    /// </summary>
    public bool IsPriority(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);

        if (!_hasPriority)
        {
            return false;
        }

        foreach (var part in phrase.Split('+'))
        {
            var tokens = Tokenizer.Tokenize(part);
            if (tokens.Count == 1 && PriorityWords.Contains(tokens[0]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The padding a Hit of <paramref name="phrase"/> gets.</summary>
    public HitPadding For(string phrase) => IsPriority(phrase) ? Priority : Ordinary;

    /// <summary>
    /// How far a Candidate's estimate may sit from an aligned Hit of
    /// <paramref name="phrase"/> and still be the same occurrence: its
    /// padding's pre plus post, the gap at which the merger fuses two windows.
    /// </summary>
    public double ToleranceFor(string phrase)
    {
        var padding = For(phrase);
        return Times.Round(padding.Pre + padding.Post);
    }

    /// <summary>
    /// The cut window for a Hit of <paramref name="phrase"/> reported at
    /// <paramref name="start"/>-<paramref name="end"/>: a priority Hit shorter
    /// than <see cref="PriorityMinimumSeconds"/> is grown backward from its
    /// end to that length, then the padding is applied. Not clamped or rounded.
    /// </summary>
    public (double Start, double End) Widen(string phrase, double start, double end)
    {
        var priority = IsPriority(phrase);
        var padding = priority ? Priority : Ordinary;

        if (priority && end - start < PriorityMinimumSeconds)
        {
            start = end - PriorityMinimumSeconds;
        }

        return (start - padding.Pre, end + padding.Post);
    }
}
