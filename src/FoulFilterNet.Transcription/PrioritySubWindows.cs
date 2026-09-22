namespace FoulFilterNet.Transcription;

/// <summary>
/// How a transcription window is cut into sub-windows for the Priority Word
/// Pass: how long each one is, and how far apart they start
/// (docs/05-crosstalk-plan.md).
/// </summary>
/// <remarks>
/// <para>
/// The shipped layout - <see cref="Default"/>, 5 s stepping 2.5 s - is the one
/// ADR-0008 measured: five-second prompted sub-windows found 9 of the 12 swears
/// spoken fully under another voice where 28-second windows found none, and a
/// step of half a sub-window means every instant is heard twice, so no word
/// falls across a seam.
/// </para>
/// <para>
/// It is configuration (<c>Transcription:PrioritySubWindowSeconds</c> and
/// <c>Transcription:PrioritySubWindowStepSeconds</c>) because it trades speed
/// against detection: a shorter step is more inferences per window and a longer
/// one is fewer, and both change what the pass hears. The shipped values are
/// the measured ones; anything else is an experiment.
/// </para>
/// </remarks>
public sealed record PrioritySubWindows
{
    /// <summary>The measured sub-window length, in seconds.</summary>
    public const double DefaultLengthSeconds = 5.0;

    /// <summary>The measured step: half a sub-window, so every instant is heard twice.</summary>
    public const double DefaultStepSeconds = 2.5;

    /// <summary>
    /// How far a sub-window's share may miss the window's and still be heard:
    /// stitching rounds times to the millisecond (once onto the window's
    /// timeline, once onto the file's), which can move a word's midpoint by a
    /// millisecond or two. Ten is ample.
    /// </summary>
    public const double ShareToleranceSeconds = 0.01;

    /// <param name="lengthSeconds">Seconds of audio per sub-window.</param>
    /// <param name="stepSeconds">Seconds between sub-window starts; at most one length.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either value is not a finite, positive number of seconds, or the step is
    /// longer than a sub-window (which would leave audio between sub-windows
    /// unheard by the pass).
    /// </exception>
    public PrioritySubWindows(double lengthSeconds, double stepSeconds)
    {
        if (!double.IsFinite(lengthSeconds) || lengthSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lengthSeconds),
                lengthSeconds,
                "Must be a finite, positive number of seconds."
            );
        }

        if (!double.IsFinite(stepSeconds) || stepSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepSeconds),
                stepSeconds,
                "Must be a finite, positive number of seconds."
            );
        }

        if (stepSeconds > lengthSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepSeconds),
                stepSeconds,
                $"Must not be longer than a sub-window ({lengthSeconds} s), or the audio between sub-windows is never heard."
            );
        }

        LengthSeconds = lengthSeconds;
        StepSeconds = stepSeconds;
    }

    /// <summary>The layout ADR-0008 measured: 5 s sub-windows stepping 2.5 s.</summary>
    public static PrioritySubWindows Default { get; } =
        new(DefaultLengthSeconds, DefaultStepSeconds);

    /// <summary>Seconds of audio per sub-window.</summary>
    public double LengthSeconds { get; }

    /// <summary>Seconds between sub-window starts.</summary>
    public double StepSeconds { get; }

    /// <summary>
    /// Cover a window of <paramref name="windowSeconds"/> with sub-windows, on
    /// the window's timeline. A window no longer than one sub-window is one
    /// sub-window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative.</exception>
    public IReadOnlyList<TranscriptionWindow> Plan(double windowSeconds) =>
        TranscriptionWindows.Plan(windowSeconds, LengthSeconds, StepSeconds);

    /// <summary>
    /// The sub-windows of <see cref="Plan(double)"/> worth hearing for a window
    /// that keeps only words whose midpoint is in [<paramref name="keepFrom"/>,
    /// <paramref name="keepTo"/>) on its own timeline: those whose share reaches
    /// that range. Every word a left-out sub-window could keep would be thrown
    /// away when the window is stitched, so leaving it out changes nothing but
    /// the time taken - one sub-window of eleven in the middle of a file, more
    /// in a last window pulled back to end with it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative.</exception>
    public IReadOnlyList<TranscriptionWindow> Plan(
        double windowSeconds,
        double keepFrom,
        double keepTo
    ) =>
        [
            .. Plan(windowSeconds)
                .Where(sub =>
                    sub.KeepTo > keepFrom - ShareToleranceSeconds
                    && sub.KeepFrom < keepTo + ShareToleranceSeconds
                ),
        ];
}
