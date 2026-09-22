using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// What merging the Priority Word Pass into a window's primary hearing gave.
/// </summary>
/// <param name="Result">
/// The primary result with the added words in its <c>Words</c>, in time order;
/// the very same instance when nothing was added.
/// </param>
/// <param name="Added">The priority words that were added, in time order.</param>
public sealed record PriorityMerge(TranscriptionResult Result, IReadOnlyList<Word> Added);

/// <summary>
/// The arithmetic behind the Priority Word Pass, free of any engine or I/O:
/// re-hearing one transcription window in short overlapping sub-windows, keeping
/// only the priority words they heard, and adding those the primary pass missed
/// (docs/05-crosstalk-plan.md).
/// </summary>
/// <remarks>
/// <para>
/// Everything here is on the <em>window's</em> timeline, starting at zero, which
/// is the timeline a window's primary result is on before
/// <see cref="TranscriptionWindows.Stitch"/> moves it onto the file's. So the
/// merged result can be handed back as the window's result and every consumer
/// of windows gets the extra words unchanged.
/// </para>
/// <para>
/// Sub-windows are planned exactly as windows are, by the same code: the last is
/// pulled back to end with the window, and each word is kept by the one
/// sub-window whose share its midpoint falls in.
/// </para>
/// </remarks>
public static class PriorityWindows
{
    /// <summary>
    /// Seconds per sub-window. Five-second prompted windows found 9 of 12
    /// swears spoken fully under another voice where 28-second ones found none.
    /// </summary>
    public const double LengthSeconds = 5.0;

    /// <summary>Seconds between sub-window starts: half a sub-window, so every instant is heard twice.</summary>
    public const double StepSeconds = 2.5;

    /// <summary>
    /// Cover a window of <paramref name="windowSeconds"/> with sub-windows, on
    /// the window's timeline. A window no longer than one sub-window is one
    /// sub-window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative.</exception>
    public static IReadOnlyList<TranscriptionWindow> Plan(double windowSeconds) =>
        TranscriptionWindows.Plan(windowSeconds, LengthSeconds, StepSeconds);

    /// <summary>
    /// Join what each sub-window heard, on its own timeline starting at zero,
    /// into the priority words on the window's timeline: only words that
    /// normalize to a token on <paramref name="priorityWords"/>, each kept by
    /// exactly one sub-window, as heard (text untouched), in time order.
    /// </summary>
    public static IReadOnlyList<Word> Stitch(
        IReadOnlyList<(TranscriptionWindow SubWindow, TranscriptionResult Heard)> subWindows,
        PriorityWordList priorityWords
    )
    {
        ArgumentNullException.ThrowIfNull(subWindows);
        ArgumentNullException.ThrowIfNull(priorityWords);

        return
        [
            .. TranscriptionWindows
                .Stitch(subWindows)
                .Words.Where(w => priorityWords.Matches(w.Text))
                .OrderBy(w => w.Start),
        ];
    }

    /// <summary>
    /// Add <paramref name="priorityWords"/> to what the primary pass heard,
    /// dropping any that duplicate a primary word - the same normalized token,
    /// overlapping it in time. Words that only touch (one ends where the other
    /// starts) are two words. Segments are not touched.
    /// </summary>
    public static PriorityMerge Merge(
        TranscriptionResult primary,
        IReadOnlyList<Word> priorityWords
    )
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(priorityWords);

        var primaryTokens = primary
            .Words.Select(w => (Word: w, Token: Tokenizer.Normalize(w.Text)))
            .ToList();

        var added = priorityWords
            .Where(candidate =>
            {
                var token = Tokenizer.Normalize(candidate.Text);
                return !primaryTokens.Exists(p => p.Token == token && Overlap(p.Word, candidate));
            })
            .OrderBy(w => w.Start)
            .ToList();

        if (added.Count == 0)
        {
            return new PriorityMerge(primary, []);
        }

        var words = primary.Words.Concat(added).OrderBy(w => w.Start).ToList();
        return new PriorityMerge(primary with { Words = words }, added);
    }

    private static bool Overlap(Word a, Word b) => a.Start < b.End && b.Start < a.End;
}
