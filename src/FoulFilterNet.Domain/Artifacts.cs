namespace FoulFilterNet.Domain;

/// <summary>
/// A contiguous span of transcribed speech. Produced by the transcription
/// stage; the unit that alignment windows are built from.
/// </summary>
public readonly record struct Segment(double Start, double End, string Text);

/// <summary>A single word with precise timestamps, produced by alignment.</summary>
public readonly record struct Word(string Text, double Start, double End);

/// <summary>
/// A token position whose normalized text matches the Bad Words List. Times are
/// interpolated across the segment and stay approximate until alignment.
/// </summary>
public sealed record Candidate(string Phrase, int SegmentIndex, double ApproxStart, double ApproxEnd);

/// <summary>
/// A confirmed edit window. <paramref name="WordIndex"/> is set when the hit came
/// from aligned words and null when it fell back to a segment estimate.
/// </summary>
public sealed record Hit(string Phrase, double Start, double End, int? WordIndex = null)
{
    /// <summary>Window length in seconds.</summary>
    public double Duration => End - Start;
}

/// <summary>
/// The complete transcription of one media file, persisted and keyed by content
/// hash so re-processing can skip transcription entirely (Resume).
/// </summary>
public sealed record Transcript(
    int Version,
    string FileHash,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<Word> Words)
{
    /// <summary>
    /// Schema version of persisted transcripts. A cached transcript carrying a
    /// different value is treated as a miss rather than reused - the Python it
    /// replaces wrote this field and never checked it.
    /// </summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// What a transcriber returned. <see cref="Words"/> is empty for engines that
/// only produce segment-level text; when it is populated, alignment is a no-op.
/// </summary>
public sealed record TranscriptionResult(IReadOnlyList<Segment> Segments, IReadOnlyList<Word> Words)
{
    /// <summary>True when the engine already produced word-level timestamps.</summary>
    public bool HasWordTimestamps => Words.Count > 0;
}

/// <summary>What probing a media file revealed.</summary>
public sealed record MediaInfo(MediaKind Kind, double DurationSeconds, int? AudioSampleRate);
