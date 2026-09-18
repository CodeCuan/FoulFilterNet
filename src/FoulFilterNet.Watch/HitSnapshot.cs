using FoulFilterNet.Domain;

namespace FoulFilterNet.Watch;

/// <summary>
/// What a Watch Session knows so far about one Web Video, as it is served to
/// the extension: the Hits to censor, the Coverage that says which of them are
/// final, and the transcript they were found in. Built by
/// <see cref="WatchProgress.Snapshot"/> and immutable.
/// </summary>
/// <param name="Revision">
/// The <see cref="WatchProgress.Revision"/> this snapshot was built at. It
/// rises with every change, so a client that already holds a revision can be
/// told nothing has changed.
/// </param>
/// <param name="Coverage">The parts of the timeline whose Hits are final.</param>
/// <param name="Hits">
/// Every Hit found in the transcript so far, on the file's timeline, padded and
/// merged exactly as the batch pipeline pads and merges them, in start order.
/// <em>All</em> of them, not only those inside <paramref name="Coverage"/>: a
/// Hit outside it is the best answer yet and errs towards censoring, and it is
/// the Playback Gate's job, not this list's, to keep uncovered audio from
/// playing. A Hit outside Coverage may still move, merge or be replaced by a
/// better one once its neighbour window is heard.
/// </param>
/// <param name="Transcript">
/// The finished windows' Segments and Words, stitched onto the file's timeline
/// in time order, with gaps where windows are unfinished. Once
/// <paramref name="IsComplete"/> it is exactly what the batch engine's stitch
/// would give, and is the Transcript to save to the cache.
/// A Hit's <see cref="Hit.WordIndex"/> indexes <see cref="TranscriptionResult.Words"/>.
/// </param>
/// <param name="FinishedWindowCount">How many transcription windows are done.</param>
/// <param name="TotalWindows">How many windows the plan has.</param>
public sealed record HitSnapshot(
    long Revision,
    Coverage Coverage,
    IReadOnlyList<Hit> Hits,
    TranscriptionResult Transcript,
    int FinishedWindowCount,
    int TotalWindows
)
{
    /// <summary>Every window is done: the Hits and the Transcript are final.</summary>
    public bool IsComplete => FinishedWindowCount == TotalWindows;
}
