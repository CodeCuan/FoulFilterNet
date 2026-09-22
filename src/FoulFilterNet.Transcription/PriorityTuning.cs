using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// The tuning of the Priority Word Pass: how a priority word's cut window is
/// widened, and how the sub-windows that hunt for those words are laid out
/// (docs/05-crosstalk-plan.md, ADR-0008).
/// </summary>
/// <remarks>
/// <para>
/// The shipped values are the measured ones. They are configuration so a
/// different model, a different card or different material can be tuned for
/// without a rebuild - not because anything is expected to beat them:
/// </para>
/// <list type="bullet">
/// <item><c>Transcription:PriorityPaddingPre</c> (0.25 s)</item>
/// <item><c>Transcription:PriorityPaddingPost</c> (0.5 s)</item>
/// <item><c>Transcription:PriorityMinimumCutSeconds</c> (0.8 s)</item>
/// <item><c>Transcription:PrioritySubWindowSeconds</c> (5 s)</item>
/// <item><c>Transcription:PrioritySubWindowStepSeconds</c> (2.5 s)</item>
/// </list>
/// <para>
/// <b>Zero means the default.</b> A key left out of configuration binds as zero
/// and so does one written as <c>0</c>, and there is no way to tell the two
/// apart; a zero-length sub-window or a step of zero is nonsense anyway, and
/// zero padding is what <c>Transcription:PriorityPass=false</c> is for. So every
/// zero falls back to the shipped value. Anything else that cannot work - a
/// negative or non-finite number, a step longer than a sub-window - is a
/// configuration error, thrown as an <see cref="InvalidOperationException"/>
/// naming the key, at startup rather than at the first job.
/// </para>
/// </remarks>
public sealed record PriorityTuning
{
    private const string PreKey = "Transcription:PriorityPaddingPre";
    private const string PostKey = "Transcription:PriorityPaddingPost";
    private const string MinimumKey = "Transcription:PriorityMinimumCutSeconds";
    private const string SubWindowKey = "Transcription:PrioritySubWindowSeconds";
    private const string StepKey = "Transcription:PrioritySubWindowStepSeconds";

    /// <param name="padding">How far a priority Hit is padded either side.</param>
    /// <param name="minimumCutSeconds">The shortest a priority Hit is taken to be, before padding.</param>
    /// <param name="subWindows">How each window is cut up for the pass.</param>
    /// <exception cref="ArgumentOutOfRangeException">The minimum is negative or not finite.</exception>
    public PriorityTuning(
        HitPadding padding,
        double minimumCutSeconds,
        PrioritySubWindows subWindows
    )
    {
        ArgumentNullException.ThrowIfNull(padding);
        ArgumentNullException.ThrowIfNull(subWindows);
        if (!double.IsFinite(minimumCutSeconds) || minimumCutSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumCutSeconds),
                minimumCutSeconds,
                "Must be a finite, non-negative number of seconds."
            );
        }

        Padding = padding;
        MinimumCutSeconds = minimumCutSeconds;
        SubWindows = subWindows;
    }

    /// <summary>The tuning ADR-0008 measured, and the one every host ships with.</summary>
    public static PriorityTuning Default { get; } =
        new(
            CutPadding.DefaultPriorityPadding,
            CutPadding.DefaultPriorityMinimumSeconds,
            PrioritySubWindows.Default
        );

    /// <summary>How far a priority Hit is padded either side.</summary>
    public HitPadding Padding { get; }

    /// <summary>The shortest a priority Hit is taken to be, before padding.</summary>
    public double MinimumCutSeconds { get; }

    /// <summary>How each transcription window is cut up for the pass.</summary>
    public PrioritySubWindows SubWindows { get; }

    /// <summary>
    /// The tuning <paramref name="options"/> asks for: each value it leaves at
    /// zero is the shipped default.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A value cannot work - negative, not a number, infinite, or a sub-window
    /// step longer than a sub-window. The message names the key.
    /// </exception>
    public static PriorityTuning ForOptions(TranscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var pre = Seconds(
            PreKey,
            options.PriorityPaddingPre,
            CutPadding.DefaultPriorityPadding.Pre
        );
        var post = Seconds(
            PostKey,
            options.PriorityPaddingPost,
            CutPadding.DefaultPriorityPadding.Post
        );
        var minimum = Seconds(
            MinimumKey,
            options.PriorityMinimumCutSeconds,
            CutPadding.DefaultPriorityMinimumSeconds
        );
        var length = Seconds(
            SubWindowKey,
            options.PrioritySubWindowSeconds,
            PrioritySubWindows.DefaultLengthSeconds
        );
        var step = Seconds(
            StepKey,
            options.PrioritySubWindowStepSeconds,
            PrioritySubWindows.DefaultStepSeconds
        );

        if (step > length)
        {
            throw new InvalidOperationException(
                $"{StepKey} is {step} s, which is longer than {SubWindowKey} ({length} s). "
                    + "Sub-windows must overlap, or the audio between them is never heard by the Priority Word Pass."
            );
        }

        return new PriorityTuning(
            new HitPadding(pre, post),
            minimum,
            new PrioritySubWindows(length, step)
        );
    }

    /// <summary>
    /// How a job pads its Hits with <paramref name="priorityWords"/>: this
    /// tuning for those words, <see cref="HitPadding.Default"/> for everything
    /// else. An empty list is <see cref="CutPadding.Default"/>.
    /// </summary>
    public CutPadding PaddingFor(PriorityWordList priorityWords)
    {
        ArgumentNullException.ThrowIfNull(priorityWords);

        return priorityWords.IsEmpty
            ? CutPadding.Default
            : new CutPadding(HitPadding.Default, priorityWords, Padding, MinimumCutSeconds);
    }

    /// <summary>
    /// <paramref name="value"/> as a number of seconds: zero (which is also how
    /// a key left out of configuration binds) means <paramref name="fallback"/>,
    /// and anything negative or non-finite is a configuration error.
    /// </summary>
    private static double Seconds(string key, double value, double fallback)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new InvalidOperationException(
                $"{key} is {value}, which is not a finite, non-negative number of seconds. "
                    + $"Leave it out, or set it to 0, for the default of {fallback} s."
            );
        }

        return value == 0.0 ? fallback : value;
    }
}
