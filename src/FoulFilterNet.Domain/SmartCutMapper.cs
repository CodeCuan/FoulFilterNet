namespace FoulFilterNet.Domain;

/// <summary>
/// Turns the word indices Smart Cut returns back into timestamps. Ported from
/// <c>apply_smart_cut_indices</c> in <c>Legacy/src/ai_helper.py</c>.
/// </summary>
/// <remarks>
/// This is where ADR-0004 is actually enforced: when widening is not permitted,
/// whatever the advisor chose is discarded and the window collapses onto the
/// target word, so silence, bleep and video edits can only ever be narrowed or
/// rejected.
/// </remarks>
public static class SmartCutMapper
{
    /// <summary>The index pair that means "this hit is a false positive".</summary>
    public const int RejectIndex = -1;

    /// <summary>
    /// Map an advisor's <paramref name="startIndex"/>/<paramref name="endIndex"/>
    /// onto <paramref name="contextWindow"/>.
    /// </summary>
    /// <param name="centerIndex">
    /// Which word in the window is the target. Used as both bounds when
    /// <paramref name="allowWidening"/> is false.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The window is empty, so there is nothing an index could refer to.
    /// </exception>
    public static SmartCutDecision Map(
        int startIndex,
        int endIndex,
        IReadOnlyList<Word> contextWindow,
        int centerIndex,
        bool allowWidening
    )
    {
        ArgumentNullException.ThrowIfNull(contextWindow);

        if (startIndex == RejectIndex || endIndex == RejectIndex)
        {
            return SmartCutDecision.Reject;
        }

        if (contextWindow.Count == 0)
        {
            throw new ArgumentException(
                "Smart Cut context window is empty.",
                nameof(contextWindow)
            );
        }

        if (!allowWidening)
        {
            startIndex = endIndex = centerIndex;
        }

        var last = contextWindow.Count - 1;
        var start = Math.Clamp(startIndex, 0, last);
        var end = Math.Clamp(endIndex, 0, last);

        return SmartCutDecision.Adjust(contextWindow[start].Start, contextWindow[end].End);
    }
}
