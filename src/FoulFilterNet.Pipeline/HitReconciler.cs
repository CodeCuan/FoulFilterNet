using FoulFilterNet.Domain;

namespace FoulFilterNet.Pipeline;

/// <summary>
/// Turns Candidates and aligned Words into the Hit list the renderer cuts from.
/// Aligned words give exact times; a Candidate alignment did not recover falls
/// back to its interpolated segment estimate, so a word Whisper heard but the
/// aligner lost is still censored.
/// </summary>
/// <remarks>
/// <para>
/// This exists to fix finding 2. The Python collected the aligned phrases into a
/// <em>set of strings</em> and skipped any Candidate whose phrase appeared in it,
/// so if "damn" occurred five times and alignment recovered one, the other four
/// counted as covered and were silently never censored - a recall bug in a
/// profanity filter.
/// </para>
/// <para>
/// A Candidate is therefore covered only when an aligned Hit of the same phrase
/// actually sits near its estimated window. Both halves of that matter: position
/// alone would let one word's Hit suppress a different word's Candidate at the
/// same moment, and the phrase alone is what the Python got wrong.
/// </para>
/// <para>
/// Reconciling is pure - Candidates in, Hits out, no I/O. Padding and the
/// overlap merge belong to <see cref="HitMerger"/> and happen afterwards.
/// </para>
/// </remarks>
public sealed class HitReconciler
{
    private readonly double _tolerance;

    /// <summary>
    /// Reconcile with the tolerance implied by <paramref name="padding"/>, or by
    /// <see cref="HitPadding.Default"/>.
    /// </summary>
    public HitReconciler(HitPadding? padding = null)
    {
        var effective = padding ?? HitPadding.Default;
        _tolerance = Times.Round(effective.Pre + effective.Post);
    }

    /// <summary>
    /// How far an aligned Hit may sit from a Candidate's estimate and still count
    /// as the same occurrence: 0.40 s by default.
    /// </summary>
    /// <remarks>
    /// This is the pre-padding plus the post-padding, which is exactly the gap at
    /// which <see cref="HitMerger"/> fuses two windows into one. That is what
    /// makes it the right number rather than a guess. Closer than this, calling
    /// the Candidate covered and adding a fallback for it produce the same final
    /// cut window, so the decision cannot change what the listener hears; at this
    /// distance or beyond, a fallback becomes a cut of its own, which is what a
    /// separate occurrence of the word deserves. Widening the padding widens this
    /// with it, so the two stay consistent.
    /// </remarks>
    public double ToleranceSeconds => _tolerance;

    /// <summary>
    /// The Hits for one file: every phrase alignment confirmed, plus a segment
    /// estimate for every Candidate it did not, ordered by start time.
    /// </summary>
    public IReadOnlyList<Hit> Reconcile(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<Word> words,
        BadWordsList badWords)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(badWords);

        var aligned = PhraseMatcher.FindHits(words, badWords);

        var hits = new List<Hit>(aligned.Count + candidates.Count);
        hits.AddRange(aligned);

        foreach (var candidate in candidates)
        {
            if (IsCovered(candidate, aligned))
            {
                continue;
            }

            // No aligned timestamps for this one; the segment estimate is all
            // there is. A null word index is what marks it as such downstream.
            hits.Add(new Hit(candidate.Phrase, candidate.ApproxStart, candidate.ApproxEnd));
        }

        // OrderBy is stable, so an aligned Hit stays ahead of a fallback that
        // starts at the same instant.
        return [.. hits.OrderBy(hit => hit.Start)];
    }

    private bool IsCovered(Candidate candidate, IReadOnlyList<Hit> aligned)
    {
        foreach (var hit in aligned)
        {
            if (!string.Equals(hit.Phrase, candidate.Phrase, StringComparison.Ordinal))
            {
                continue;
            }

            if (Times.Round(GapBetween(candidate, hit)) <= _tolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Seconds between the two windows, or zero when they overlap.</summary>
    private static double GapBetween(Candidate candidate, Hit hit) =>
        Math.Max(0.0, Math.Max(candidate.ApproxStart - hit.End, hit.Start - candidate.ApproxEnd));
}
