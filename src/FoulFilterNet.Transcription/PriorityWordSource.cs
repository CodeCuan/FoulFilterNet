using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// The Priority Word List the engine runs the Priority Word Pass with, and
/// where it came from - which the engine logs as the model loads, so a log
/// says whether the pass ran and on whose words.
/// </summary>
/// <param name="Words">The list. Empty turns the pass off.</param>
/// <param name="Origin">
/// The file it was read from, or <see cref="BuiltInOrigin"/>, or why there is
/// none.
/// </param>
public sealed record PriorityWordSource(PriorityWordList Words, string Origin)
{
    /// <summary>The origin of <see cref="PriorityWordList.Default"/>, used when there is no file.</summary>
    public const string BuiltInOrigin = "built-in default";

    /// <summary>The origin of the empty list configuration asked for.</summary>
    public const string SwitchedOffOrigin = "switched off by Transcription:PriorityPass";

    /// <summary>The origin of the empty list an engine built without one runs with.</summary>
    public const string NoneGivenOrigin = "none given to the engine";

    /// <summary>No pass: what an engine constructed without a list uses.</summary>
    public static PriorityWordSource None { get; } =
        new(PriorityWordList.FromLines([]), NoneGivenOrigin);
}
