using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Web;

/// <summary>
/// Stands in for the speech-to-text engine until T13 lands, so the rest of the
/// application is wired up and running rather than waiting on it.
/// </summary>
/// <remarks>
/// <para>
/// This is the only engine still missing, and it is deliberately the narrowest
/// possible placeholder: a job that needs transcription fails with an
/// explanation the UI can show, and every other path works for real. A file
/// whose transcript is already cached does not need this at all - it resumes,
/// matches, aligns, merges and renders end to end (ADR-0002).
/// </para>
/// <para>Releasing is a no-op: it holds no model and no VRAM.</para>
/// </remarks>
internal sealed class PendingTranscriber : ITranscriber
{
    private const string Unavailable =
        "No transcription engine is configured in this build. A file whose transcript is already cached still runs.";

    public Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Unavailable);

    public Task<TranscriptionResult> TranscribeShiftedAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(Unavailable);

    public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
}
