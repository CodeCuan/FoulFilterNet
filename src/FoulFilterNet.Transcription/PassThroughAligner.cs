using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Transcription;

/// <summary>
/// Alignment for transcribers that already returned word timestamps: hands the
/// transcriber's words straight back.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0001 split transcription from alignment only because CTranslate2 had no
/// usable ROCm build, so the Python transcribed with Hugging Face Whisper and
/// recovered word boundaries with WhisperX's wav2vec2 forced aligner. The
/// whisper.cpp engine that replaces both emits word timestamps directly from
/// its DTW pass, which leaves this stage with nothing to do.
/// </para>
/// <para>
/// The stage is kept rather than deleted because DTW boundaries are looser than
/// a forced aligner's. If the measured error (T13, T34) exceeds the 0.15 s /
/// 0.25 s hit padding, a real aligner drops in behind <see cref="IAligner"/>
/// and the pipeline does not change. That is the whole point of this type.
/// </para>
/// <para>
/// Consequently it ignores <c>audioPath</c> and <c>segments</c> - it never
/// opens the audio - and reports no progress, because it does no work. With no
/// words to pass through it returns none rather than throwing, so the pipeline
/// cannot tell the difference between this aligner and skipping alignment
/// altogether.
/// </para>
/// </remarks>
public sealed class PassThroughAligner : IAligner
{
    /// <inheritdoc />
    public Task<IReadOnlyList<Word>> AlignAsync(
        string audioPath,
        IReadOnlyList<Segment> segments,
        IReadOnlyList<Word> existingWords,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingWords);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(existingWords);
    }

    /// <inheritdoc />
    /// <remarks>Holds no model and no VRAM, so releasing is a no-op and repeatable.</remarks>
    public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
}
