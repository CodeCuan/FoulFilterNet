using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Transcription;

/// <summary>
/// The <see cref="ITranscriber"/> the pipeline uses: everything around inference
/// that is not inference.
/// </summary>
/// <remarks>
/// <para>
/// whisper.cpp reads 16 kHz mono PCM, while a job arrives as an mp3, an m4b or a
/// video's extracted AAC track, so every pass converts first - which is FFmpeg's
/// job (T10) and is borrowed here rather than reimplemented. The Rescan Pass
/// needs the same conversion with silence in front of it, so both passes are one
/// code path with a different offset: zero for the first pass, four seconds for
/// the rescan. The converted file is a temporary one this type owns, and it is
/// deleted whether inference succeeded or not - an audiobook's analysis WAV is
/// hundreds of megabytes.
/// </para>
/// <para>
/// The Rescan arithmetic is <see cref="RescanPass"/>'s, not this type's: the raw
/// result comes back on the padded timeline and one call rebases its segments
/// and its words together.
/// </para>
/// </remarks>
public sealed class WhisperTranscriber : ITranscriber
{
    private readonly IWhisperEngine _engine;
    private readonly IAudioPreparer _audio;

    public WhisperTranscriber(IWhisperEngine engine, IAudioPreparer audio)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(audio);

        _engine = engine;
        _audio = audio;
    }

    /// <inheritdoc />
    public Task<TranscriptionResult> TranscribeAsync(
        string audioPath,
        CancellationToken cancellationToken = default
    ) => HearAsync(audioPath, offsetSeconds: 0.0, cancellationToken);

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeShiftedAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default
    ) =>
        RescanPass.Shift(
            await HearAsync(audioPath, offsetSeconds, cancellationToken).ConfigureAwait(false),
            offsetSeconds
        );

    /// <inheritdoc />
    /// <remarks>
    /// Whether this happens at all is <see cref="ReleasePolicyTranscriber"/>'s
    /// decision; this type only forwards it to the engine.
    /// </remarks>
    public ValueTask ReleaseAsync() => _engine.ReleaseAsync();

    /// <summary>
    /// Convert (and, for a rescan, pad), transcribe, and clean up. An offset of
    /// zero is the plain conversion: FFmpeg's <c>adelay=0|0</c> is a no-op that
    /// still produces the mono 16 kHz WAV inference needs.
    /// </summary>
    private async Task<TranscriptionResult> HearAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);

        var analysis = await _audio
            .PadStartAsync(audioPath, offsetSeconds, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return await _engine
                .TranscribeWavAsync(analysis, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDelete(analysis);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover analysis WAV is untidy, not a reason to fail a job that
            // has already been transcribed.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
