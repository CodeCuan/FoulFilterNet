using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// The part of transcription that needs a GPU, a native library and a gigabyte
/// of weights: inference over one ready-made WAV, a window at a time.
/// </summary>
/// <remarks>
/// <para>
/// It exists so that everything <em>around</em> inference - converting the
/// audio, padding it for the Rescan Pass, rebasing the result, deleting the
/// temporary file, releasing the model after a job - is ordinary code that CI
/// can test, leaving only <see cref="WhisperNetEngine"/> to need hardware. That
/// is the same split <c>IFFmpegRunner</c> draws around launching a process.
/// </para>
/// <para>
/// The unit is the window (<see cref="TranscriptionWindows"/>) rather than the
/// file because a Watch Session needs windows one at a time, in an order it
/// chooses as the viewer seeks, from a WAV it opened once. A batch Job is the
/// special case of every window in order, stitched:
/// <see cref="WhisperEngineExtensions.TranscribeWavAsync"/>, which is what the
/// engine used to do inside a single call, so a Job's transcript is unchanged.
/// </para>
/// </remarks>
public interface IWhisperEngine
{
    /// <summary>
    /// Open a 16 kHz mono 16-bit PCM WAV for windowed transcription, loading the
    /// model if it is not resident. The path is a file the caller owns; the
    /// engine only reads it, and holds it open until the audio is disposed.
    /// </summary>
    /// <param name="wavPath">The analysis WAV.</param>
    /// <param name="priority">
    /// How every window of this audio queues for the GPU: <see
    /// cref="InferencePriority.High"/> for a Watch Session, <see
    /// cref="InferencePriority.Normal"/> for a batch Job. It belongs to the
    /// opened audio rather than to each call because it is a property of who
    /// is listening, which does not change window to window.
    /// </param>
    /// <param name="cancellationToken">Stops the open, including a model load.</param>
    /// <exception cref="NotSupportedException">The WAV is not 16-bit PCM.</exception>
    Task<IAnalysisAudio> OpenAsync(
        string wavPath,
        InferencePriority priority = InferencePriority.Normal,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Drop the model and hand its VRAM back. Called after every job, so it has
    /// to be cheap, repeatable, and safe when no model was ever loaded. While
    /// any audio is still open - a Watch Session outliving the Job that asked -
    /// the release waits for the last of it to close.
    /// </summary>
    ValueTask ReleaseAsync();
}

/// <summary>
/// An analysis WAV opened by <see cref="IWhisperEngine.OpenAsync"/>, planned
/// into windows, any of which can be transcribed on demand.
/// </summary>
public interface IAnalysisAudio : IAsyncDisposable
{
    /// <summary>The audio's length in seconds.</summary>
    double DurationSeconds { get; }

    /// <summary>
    /// <see cref="TranscriptionWindows.Plan"/> of <see cref="DurationSeconds"/>;
    /// the indices <see cref="TranscribeWindowAsync"/> accepts.
    /// </summary>
    IReadOnlyList<TranscriptionWindow> Windows { get; }

    /// <summary>
    /// What window <paramref name="index"/> heard, on the <em>window's own</em>
    /// timeline starting at zero - the shape
    /// <see cref="TranscriptionWindows.Stitch"/> takes. Waits its turn for the
    /// GPU at the priority the audio was opened with. Windows may be asked for
    /// in any order, and again.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Not an index of <see cref="Windows"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled while waiting or while heard.</exception>
    Task<TranscriptionResult> TranscribeWindowAsync(
        int index,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The batch path over the windowed contract.</summary>
public static class WhisperEngineExtensions
{
    /// <summary>
    /// Transcribe a whole 16 kHz mono PCM WAV, with word timestamps, on the
    /// file's timeline: open it at <see cref="InferencePriority.Normal"/>, hear
    /// every window in order, and stitch. The audio is closed before this
    /// returns or throws, so the caller can delete the WAV straight after.
    /// </summary>
    public static async Task<TranscriptionResult> TranscribeWavAsync(
        this IWhisperEngine engine,
        string wavPath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);

        var audio = await engine
            .OpenAsync(wavPath, InferencePriority.Normal, cancellationToken)
            .ConfigureAwait(false);
        await using (audio.ConfigureAwait(false))
        {
            var heard = new List<(TranscriptionWindow, TranscriptionResult)>(audio.Windows.Count);
            for (var index = 0; index < audio.Windows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                heard.Add(
                    (
                        audio.Windows[index],
                        await audio
                            .TranscribeWindowAsync(index, cancellationToken)
                            .ConfigureAwait(false)
                    )
                );
            }

            return TranscriptionWindows.Stitch(heard);
        }
    }
}
