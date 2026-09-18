using FoulFilterNet.Transcription;

namespace FoulFilterNet.Watch;

/// <summary>
/// Which transcription window a Watch Session should run next, given the plan,
/// the windows already finished and where the viewer's playhead is. Pure: the
/// session owns the window in flight and asks again after each one, with the
/// latest reported playhead, so a seek takes effect after the window in flight.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> Start from the window whose <em>share</em>
/// (<c>KeepFrom</c> ≤ playhead &lt; <c>KeepTo</c>) holds the playhead - not
/// the window whose audio range does, because in an overlap two windows hear
/// the playhead but only one of them decides its words (the same midpoint rule
/// <see cref="TranscriptionWindows.Stitch"/> uses, so a playhead exactly on a
/// share boundary belongs to the later window). Take the first unfinished
/// window at or after it in index order, which is what the Playback Gate is
/// waiting for as the video plays forwards. When everything from there to the
/// end is finished, wrap round to the unfinished windows behind the playhead,
/// <b>nearest first</b>.
/// </para>
/// <para>
/// <b>Why nearest first when wrapping.</b> By the time the scheduler wraps,
/// everything ahead of the viewer is final; the windows behind only matter if
/// the viewer seeks back. The likeliest backward seek is a short one - the
/// arrow key's 5 s, "what did they say?", or overshooting a skip - and it lands
/// just behind the playhead. Working backwards from the playhead grows the
/// covered run that already holds the playhead, so a rewind of any distance up
/// to what has been filled in lands on final Hits with no hold, and Coverage
/// stays one unbroken run with no guard gaps inside it. Lowest index first
/// would fill the start of the file while the stretch just behind the viewer
/// waits until last. A jump right back to the start is no worse off either
/// way: it moves the playhead, and the very next pick is the window holding
/// it, so the hold is one window (about a second on the GPU).
/// </para>
/// <para>
/// <b>The playhead.</b> Any number is accepted and clamped to the plan: a
/// playhead before the first share (a negative one, for a plan from
/// <see cref="TranscriptionWindows.Plan"/>, whose shares are open-ended) picks
/// from the first window, and one at or past the last share's end (past the
/// duration, or infinite) from the last. NaN is refused with an
/// <see cref="ArgumentException"/>, as <see cref="Coverage"/> refuses it: a
/// NaN playhead is a bug upstream, and quietly treating it as 0 would send the
/// session back to the start of the file while the viewer waits somewhere
/// else.
/// </para>
/// <para>
/// The plan is checked the way <see cref="Coverage"/> checks it, minus the
/// duration: it must not be empty, and each share must have length and start
/// exactly where the one before ends, so every playhead is held by exactly one
/// share. Finished indices outside the plan throw, as they do in
/// <see cref="Coverage"/>, and repeats are ignored. The finished set is read
/// once. Both methods are linear in the plan (about 170 windows for an hour).
/// </para>
/// </remarks>
public static class WindowScheduler
{
    /// <summary>
    /// The index of the window to transcribe next, or <see langword="null"/>
    /// when every window is finished. Always the first element of
    /// <see cref="Order"/> for the same arguments.
    /// </summary>
    /// <param name="plan">
    /// The windows, in order, whose shares meet with no gap or overlap.
    /// </param>
    /// <param name="finished">
    /// Indices into <paramref name="plan"/> of the finished windows, in any
    /// order; repeats are ignored.
    /// </param>
    /// <param name="playheadSeconds">
    /// The latest reported playhead; clamped to the plan, not NaN.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A finished index is outside the plan.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The plan is empty or its shares do not tile the timeline, or the
    /// playhead is NaN.
    /// </exception>
    public static int? Next(
        IReadOnlyList<TranscriptionWindow> plan,
        IEnumerable<int> finished,
        double playheadSeconds
    )
    {
        foreach (var index in Remaining(plan, finished, playheadSeconds))
        {
            return index;
        }

        return null;
    }

    /// <summary>
    /// Every unfinished window, in the order <see cref="Next"/> would hand them
    /// out if the playhead stayed where it is: from the window holding the
    /// playhead to the end, then back from the playhead to the start. Empty
    /// when every window is finished. Meant for tests and diagnostics; a Watch
    /// Session asks <see cref="Next"/> after every window instead, because the
    /// playhead moves.
    /// </summary>
    /// <inheritdoc cref="Next" path="/param"/>
    /// <inheritdoc cref="Next" path="/exception"/>
    public static IReadOnlyList<int> Order(
        IReadOnlyList<TranscriptionWindow> plan,
        IEnumerable<int> finished,
        double playheadSeconds
    ) => [.. Remaining(plan, finished, playheadSeconds)];

    /// <summary>
    /// The one place the order is decided, so <see cref="Next"/> and
    /// <see cref="Order"/> cannot disagree. The arguments are checked before
    /// the first index is yielded.
    /// </summary>
    private static IEnumerable<int> Remaining(
        IReadOnlyList<TranscriptionWindow> plan,
        IEnumerable<int> finished,
        double playheadSeconds
    )
    {
        var done = Prepare(plan, finished, playheadSeconds);
        var holding = IndexHolding(plan, playheadSeconds);

        for (var i = holding; i < done.Length; i++)
        {
            if (!done[i])
            {
                yield return i;
            }
        }

        for (var i = holding - 1; i >= 0; i--)
        {
            if (!done[i])
            {
                yield return i;
            }
        }
    }

    private static bool[] Prepare(
        IReadOnlyList<TranscriptionWindow> plan,
        IEnumerable<int> finished,
        double playheadSeconds
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(finished);
        if (double.IsNaN(playheadSeconds))
        {
            throw new ArgumentException(
                "Must be a number of seconds, not NaN.",
                nameof(playheadSeconds)
            );
        }

        ThrowUnlessTiles(plan);

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

        return done;
    }

    /// <summary>
    /// The last window whose share starts at or before the playhead, which is
    /// the one holding it since the shares tile; the first window when the
    /// playhead is before every share, and the last when it is past them all.
    /// </summary>
    private static int IndexHolding(IReadOnlyList<TranscriptionWindow> plan, double playhead)
    {
        var holding = 0;
        for (var i = 1; i < plan.Count && plan[i].KeepFrom <= playhead; i++)
        {
            holding = i;
        }

        return holding;
    }

    private static void ThrowUnlessTiles(IReadOnlyList<TranscriptionWindow> plan)
    {
        if (plan.Count == 0)
        {
            throw new ArgumentException("The plan has no windows.", nameof(plan));
        }

        for (var i = 0; i < plan.Count; i++)
        {
            // Written so a NaN end fails too.
            if (!(plan[i].KeepFrom < plan[i].KeepTo))
            {
                throw new ArgumentException(
                    $"Window {i}'s share, [{plan[i].KeepFrom}, {plan[i].KeepTo}), holds no playhead.",
                    nameof(plan)
                );
            }

            // Exact equality on purpose, as in Coverage: Plan copies KeepTo
            // into the next KeepFrom.
            if (i > 0 && plan[i].KeepFrom != plan[i - 1].KeepTo)
            {
                throw new ArgumentException(
                    $"Window {i}'s share starts at {plan[i].KeepFrom}, not where window {i - 1}'s ends ({plan[i - 1].KeepTo}).",
                    nameof(plan)
                );
            }
        }
    }
}
