using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// The part of transcription that needs a GPU, a native library and a gigabyte
/// of weights: inference over one ready-made WAV.
/// </summary>
/// <remarks>
/// It exists so that everything <em>around</em> inference - converting the
/// audio, padding it for the Rescan Pass, rebasing the result, deleting the
/// temporary file, releasing the model after a job - is ordinary code that CI
/// can test, leaving only <see cref="WhisperNetEngine"/> to need hardware. That
/// is the same split <c>IFFmpegRunner</c> draws around launching a process.
/// </remarks>
public interface IWhisperEngine
{
    /// <summary>
    /// Transcribe a 16 kHz mono PCM WAV, with word timestamps. The path is a
    /// file the caller owns; the engine only reads it.
    /// </summary>
    Task<TranscriptionResult> TranscribeWavAsync(string wavPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drop the model and hand its VRAM back. Called after every job, so it has
    /// to be cheap, repeatable, and safe when no model was ever loaded.
    /// </summary>
    ValueTask ReleaseAsync();
}
