using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// The arithmetic behind the Rescan Pass, free of any engine or I/O.
/// </summary>
/// <remarks>
/// <para>
/// A second detection pass transcribes the same audio with
/// <see cref="DefaultOffsetSeconds"/> seconds of silence prepended, so every
/// chunk boundary moves. A word that was garbled straddling a boundary in the
/// first pass lands cleanly inside a chunk in the second. The price is that
/// every timestamp comes back shifted later by the offset, and the padding
/// itself may be transcribed as spurious leading segments.
/// </para>
/// <para>
/// Ported from <c>transcribe_shifted</c> in <c>Legacy/src/transcriber.py</c>
/// and <c>_union_segments</c> in <c>Legacy/src/pipeline.py</c>.
/// </para>
/// </remarks>
public static class RescanPass
{
    /// <summary>
    /// Seconds of silence prepended before the second pass
    /// (<c>RESCAN_OFFSET_S</c>).
    /// </summary>
    public const double DefaultOffsetSeconds = 4.0;

    /// <summary>
    /// Rebase segments from the padded timeline onto the original one.
    /// A segment ending at or before zero lay entirely inside the padding and
    /// is dropped; one straddling zero has its start clamped.
    /// </summary>
    public static IReadOnlyList<Segment> Shift(
        IReadOnlyList<Segment> segments,
        double offsetSeconds
    )
    {
        ArgumentNullException.ThrowIfNull(segments);

        var shifted = new List<Segment>(segments.Count);
        foreach (var segment in segments)
        {
            var end = segment.End - offsetSeconds;
            if (end <= 0)
            {
                continue;
            }

            var start = Math.Max(0.0, segment.Start - offsetSeconds);
            shifted.Add(new Segment(Times.Round(start), Times.Round(end), segment.Text));
        }

        return shifted;
    }

    /// <summary>
    /// Rebase words from the padded timeline onto the original one, on the same
    /// terms as <see cref="Shift(IReadOnlyList{Segment}, double)"/>. Needed
    /// because the whisper.cpp engine emits words alongside segments.
    /// </summary>
    public static IReadOnlyList<Word> Shift(IReadOnlyList<Word> words, double offsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(words);

        var shifted = new List<Word>(words.Count);
        foreach (var word in words)
        {
            var end = word.End - offsetSeconds;
            if (end <= 0)
            {
                continue;
            }

            var start = Math.Max(0.0, word.Start - offsetSeconds);
            shifted.Add(new Word(word.Text, Times.Round(start), Times.Round(end)));
        }

        return shifted;
    }

    /// <summary>Rebase a whole result - segments and words together.</summary>
    public static TranscriptionResult Shift(TranscriptionResult result, double offsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new TranscriptionResult(
            Shift(result.Segments, offsetSeconds),
            Shift(result.Words, offsetSeconds)
        );
    }

    /// <summary>
    /// Merge a rescan into the primary transcript without duplicating spans
    /// both passes heard the same way. Segments are ordered by start time and a
    /// segment whose text repeats the previous kept segment's is dropped when
    /// the two overlap - the same text at a genuinely different time survives.
    /// </summary>
    public static IReadOnlyList<Segment> Union(
        IReadOnlyList<Segment> primary,
        IReadOnlyList<Segment> extra
    )
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(extra);

        // Concatenate primary first so a stable sort keeps the original pass
        // ahead of the rescan whenever two segments start at the same instant.
        var ordered = primary.Concat(extra).OrderBy(s => s.Start);

        var union = new List<Segment>(primary.Count + extra.Count);
        foreach (var segment in ordered)
        {
            if (union.Count > 0)
            {
                var previous = union[^1];
                if (
                    string.Equals(segment.Text, previous.Text, StringComparison.Ordinal)
                    && segment.Start < previous.End
                )
                {
                    continue;
                }
            }

            union.Add(segment);
        }

        return union;
    }
}
