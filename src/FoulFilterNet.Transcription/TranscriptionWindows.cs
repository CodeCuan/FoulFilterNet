using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// One stretch of the analysis WAV that is transcribed on its own, and the part
/// of it whose words are believed.
/// </summary>
/// <param name="Start">Where the window begins in the file, in seconds.</param>
/// <param name="End">Where the window ends in the file, in seconds.</param>
/// <param name="KeepFrom">
/// The earliest midpoint a word or segment may have to be kept from this window.
/// </param>
/// <param name="KeepTo">
/// The midpoint (exclusive) from which the next window's words are kept instead.
/// </param>
public readonly record struct TranscriptionWindow(
    double Start,
    double End,
    double KeepFrom,
    double KeepTo
);

/// <summary>
/// The arithmetic behind transcribing a long file in overlapping windows, free
/// of any engine or I/O.
/// </summary>
/// <remarks>
/// <para>
/// With DTW word timestamps on, Whisper.net 1.9.1 returns only the first
/// 30-second window of whatever it is given: a 2-minute clip stops at 29.8 s,
/// and a 52-minute video at 29 s, with nothing to say the rest was dropped.
/// Turning DTW off transcribes the whole file but left five of the fixtures'
/// eleven spans partly audible even after padding (ADR-0006 measured why). So
/// the engine keeps DTW and never hands whisper.cpp more than one window.
/// </para>
/// <para>
/// A hard cut can split a word, so neighbouring windows overlap by
/// <see cref="OverlapSeconds"/> and each item is kept by exactly one window: the
/// one whose share of the timeline its midpoint falls in. The shares meet in the
/// middle of each overlap, so anything kept is at least half an overlap away
/// from the cut that might have garbled it.
/// </para>
/// </remarks>
public static class TranscriptionWindows
{
    /// <summary>
    /// Seconds per window: under whisper.cpp's 30-second input so every window
    /// is heard in one pass.
    /// </summary>
    public const double LengthSeconds = 28.0;

    /// <summary>Seconds shared by neighbouring windows.</summary>
    public const double OverlapSeconds = 6.0;

    /// <summary>
    /// Cover <paramref name="durationSeconds"/> with windows. A file no longer
    /// than one window is one window. Otherwise windows step by
    /// <c>LengthSeconds - OverlapSeconds</c>, and the last one is pulled back to
    /// end with the file, so no window is shorter than
    /// <see cref="LengthSeconds"/> - a sliver of trailing audio is easy for
    /// whisper.cpp to mishear.
    /// </summary>
    public static IReadOnlyList<TranscriptionWindow> Plan(double durationSeconds) =>
        Plan(durationSeconds, LengthSeconds, LengthSeconds - OverlapSeconds);

    /// <summary>
    /// Cover <paramref name="durationSeconds"/> with windows of
    /// <paramref name="length"/> stepping <paramref name="step"/>, the last pulled
    /// back to end with the file, each with a midpoint share. Shared with
    /// <see cref="PriorityWindows"/>, which plans sub-windows the same way.
    /// </summary>
    internal static IReadOnlyList<TranscriptionWindow> Plan(
        double durationSeconds,
        double length,
        double step
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationSeconds);

        var starts = new List<double>();
        var start = 0.0;
        while (start + length < durationSeconds)
        {
            starts.Add(start);
            start += step;
        }

        starts.Add(Math.Max(0.0, durationSeconds - length));

        var windows = new List<TranscriptionWindow>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = Math.Min(starts[i] + length, durationSeconds);
            var keepFrom = i == 0 ? double.NegativeInfinity : windows[i - 1].KeepTo;
            var keepTo =
                i == starts.Count - 1 ? double.PositiveInfinity : (end + starts[i + 1]) / 2;
            windows.Add(new TranscriptionWindow(starts[i], end, keepFrom, keepTo));
        }

        return windows;
    }

    /// <summary>
    /// How many of the first windows <see cref="Plan"/> gives exactly the same
    /// - start, end and share - for <em>every</em> file at least
    /// <paramref name="minimumDurationSeconds"/> long.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the end of a plan depends on the file's length: the last window is
    /// pulled back to end with the file, and each share ends halfway into the
    /// overlap with the next window. So a window is fixed once neither it nor
    /// the next one can be the last, which for window <c>i</c> means the window
    /// after it, <c>i + 1</c>, ends before the known length.
    /// </para>
    /// <para>
    /// This is what lets a Watch Session hear the start of a video from the
    /// first stretch of its audio, converted on its own, before the whole file
    /// exists to say how long it is (W17): the windows it hears then are the
    /// very windows, with the very audio, it would have heard from the whole
    /// file. Every one of them ends more than a step before the known length.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The length is negative, NaN or infinite.</exception>
    public static int FixedPrefixCount(double minimumDurationSeconds)
    {
        if (!double.IsFinite(minimumDurationSeconds) || minimumDurationSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumDurationSeconds),
                minimumDurationSeconds,
                "Must be a finite, non-negative number of seconds."
            );
        }

        var step = LengthSeconds - OverlapSeconds;
        var count = 0;
        while ((count + 1) * step + LengthSeconds < minimumDurationSeconds)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Join what each window heard, on its own timeline starting at zero, into
    /// one result on the file's timeline.
    /// </summary>
    public static TranscriptionResult Stitch(
        IReadOnlyList<(TranscriptionWindow Window, TranscriptionResult Heard)> windows
    )
    {
        ArgumentNullException.ThrowIfNull(windows);

        var segments = new List<Segment>();
        var words = new List<Word>();
        foreach (var (window, heard) in windows)
        {
            foreach (var segment in heard.Segments)
            {
                var start = segment.Start + window.Start;
                var end = segment.End + window.Start;
                if (Keeps(window, start, end))
                {
                    segments.Add(new Segment(Times.Round(start), Times.Round(end), segment.Text));
                }
            }

            foreach (var word in heard.Words)
            {
                var start = word.Start + window.Start;
                var end = word.End + window.Start;
                if (Keeps(window, start, end))
                {
                    words.Add(new Word(word.Text, Times.Round(start), Times.Round(end)));
                }
            }
        }

        return new TranscriptionResult(segments, words);
    }

    private static bool Keeps(TranscriptionWindow window, double start, double end)
    {
        var midpoint = (start + end) / 2;
        return midpoint >= window.KeepFrom && midpoint < window.KeepTo;
    }
}
