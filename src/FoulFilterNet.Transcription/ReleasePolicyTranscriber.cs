using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Transcription;

/// <summary>
/// <c>UNLOAD_MODELS_AFTER_JOB</c> as an implementation rather than a branch:
/// wraps the real engine and drops its model after a job only when the flag says
/// to.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline releases the transcriber after every job - success, failure and
/// cancellation alike - and never asks whether the feature is on, exactly as it
/// never asks whether Smart Cut is. The flag belongs to the engine's policy, so
/// it lives here, where the option is already bound and where a future engine's
/// own idea of "release" can join it.
/// </para>
/// <para>
/// Everything that is not the release passes straight through. Releasing must
/// stay safe to call when no model was ever loaded - a job that resumed a cached
/// transcript never transcribed, and must not fail on the way out - which is a
/// requirement this type passes on to the engine it wraps rather than one it can
/// satisfy by itself.
/// </para>
/// </remarks>
public sealed class ReleasePolicyTranscriber : ITranscriber
{
    private readonly ITranscriber _engine;
    private readonly bool _unloadAfterJob;

    public ReleasePolicyTranscriber(ITranscriber engine, TranscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);

        _engine = engine;
        _unloadAfterJob = options.UnloadAfterJob;
    }

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        CancellationToken cancellationToken = default) =>
        _engine.TranscribeAsync(audioPath, cancellationToken);

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeShiftedAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default) =>
        _engine.TranscribeShiftedAsync(audioPath, offsetSeconds, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// With the flag off this is the no-op that keeps the model resident, which
    /// is the faster arrangement on a machine that runs nothing else.
    /// </remarks>
    public ValueTask ReleaseAsync() =>
        _unloadAfterJob ? _engine.ReleaseAsync() : ValueTask.CompletedTask;
}
