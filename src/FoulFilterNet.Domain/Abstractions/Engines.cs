namespace FoulFilterNet.Domain.Abstractions;

/// <summary>
/// Speech-to-text. Implementations may or may not produce word-level
/// timestamps; see <see cref="TranscriptionResult.HasWordTimestamps"/>.
/// </summary>
public interface ITranscriber
{
    Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The Rescan Pass: transcribe with every chunk boundary shifted by
    /// <paramref name="offsetSeconds"/>, so a word garbled at a boundary in the
    /// first pass lands cleanly inside a chunk here. Returned timestamps are
    /// already rebased onto the original timeline.
    /// </summary>
    Task<TranscriptionResult> TranscribeShiftedAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default
    );

    /// <summary>Drop the model and hand its VRAM back (UNLOAD_MODELS_AFTER_JOB).</summary>
    ValueTask ReleaseAsync();
}

/// <summary>
/// Forced alignment: precise timestamps for every word in the transcript.
/// Implementations may be a pass-through when the transcriber already produced
/// words - the stage survives as a seam, per docs/01-python-analysis.md 9.1.
/// </summary>
public interface IAligner
{
    Task<IReadOnlyList<Word>> AlignAsync(
        string audioPath,
        IReadOnlyList<Segment> segments,
        IReadOnlyList<Word> existingWords,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default
    );

    ValueTask ReleaseAsync();
}

/// <summary>
/// The optional LLM refinement pass. Never creates hits; only narrows, widens
/// (where permitted) or rejects them.
/// </summary>
public interface ISmartCutAdvisor
{
    /// <summary>
    /// False when the feature flag is off or the transport is unconfigured, in
    /// which case the pipeline skips the stage entirely.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Refine one hit. Must never throw: transport failures return
    /// <see cref="SmartCutDecision.KeepOriginal"/>, because a flaky LLM must not
    /// be able to fail a job.
    /// </summary>
    /// <param name="allowWidening">
    /// Only true for <see cref="CensorMethod.Remove"/> on audio (ADR-0004).
    /// Everywhere else the decision is clamped to the target word.
    /// </param>
    Task<SmartCutDecision> RefineAsync(
        IReadOnlyList<Word> contextWindow,
        string phrase,
        int centerIndex,
        bool allowWidening,
        CancellationToken cancellationToken = default
    );
}
