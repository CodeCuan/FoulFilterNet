using FoulFilterNet.Domain;

namespace FoulFilterNet.Transcription;

/// <summary>
/// Inference over one window's samples: in production a whisper.cpp processor,
/// behind an interface so that <see cref="WindowedAudio"/>'s rules - the lane,
/// cancellation, disposal - are tested in CI without a GPU.
/// </summary>
public interface IWindowListener : IAsyncDisposable
{
    /// <summary>
    /// What <paramref name="samples"/> (16 kHz mono, in [-1, 1)) say, on their
    /// own timeline starting at zero. Never called concurrently on one listener.
    /// </summary>
    Task<TranscriptionResult> HearAsync(float[] samples, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IAnalysisAudio"/> put together from its parts: an opened
/// <see cref="AnalysisWav"/>, the engine's <see cref="InferenceLane"/>, a
/// priority, and a way to build a listener.
/// </summary>
/// <remarks>
/// <para>
/// Per window: read the samples (outside the lane - it is a millisecond of
/// disk and nobody should wait on the GPU for it), take the lane at this
/// audio's priority, hear, leave. One listener is built on the first window and
/// reused for the rest, as the batch path always reused one processor per
/// transcription; audio opened but never transcribed builds none, so it holds
/// no GPU state.
/// </para>
/// <para>
/// A window cancelled while it is heard throws away its listener, and waits for
/// the disposal, <em>before</em> leaving the lane. Whisper.net stops a native
/// call through an abort callback that can land after the managed side has
/// already thrown, and its processor's <c>DisposeAsync</c> is what waits for
/// the native call to finish. Leaving first would let the next window onto the
/// GPU while this one is still there. The next window builds a fresh listener.
/// </para>
/// <para>
/// Calls on one instance are taken one at a time (the lane would serialise
/// them anyway, and a listener must not be used concurrently), and disposal
/// waits for a window in progress rather than pulling the listener out from
/// under it.
/// </para>
/// </remarks>
public sealed class WindowedAudio : IAnalysisAudio
{
    /// <summary>
    /// Silence appended to every window before it is heard, in seconds.
    /// </summary>
    /// <remarks>
    /// Without it, whisper.cpp ends the last segment wherever the samples run
    /// out, which can leave a sliver of a segment a few frames long. DTW then
    /// median-filters that sliver and trips a native assertion -
    /// <c>filter_width &lt; a-&gt;ne[2]</c> at whisper.cpp:8793 - which is a
    /// <em>fast-fail</em>: the process dies on the spot, with no exception to
    /// catch (seen on a real video, 2026-09-20; reproduced and measured, and
    /// still present in Whisper.net 1.9.2-preview1). A little silence lets the
    /// last real segment end on its own instead, and what is heard is
    /// otherwise identical.
    /// </remarks>
    public const double TrailingSilenceSeconds = 0.5;

    /// <summary>What whisper.cpp reads, and what <see cref="AnalysisWav"/> produces.</summary>
    private const int SampleRate = 16000;

    private readonly AnalysisWav _wav;
    private readonly InferenceLane _lane;
    private readonly Func<IWindowListener> _listen;
    private readonly Func<ValueTask>? _closed;
    private readonly SemaphoreSlim _turn = new(1, 1);

    private IWindowListener? _listener;
    private int _disposed;

    /// <param name="wav">The opened file. This instance owns it from here on.</param>
    /// <param name="lane">The lane every window takes, shared with all other audio.</param>
    /// <param name="priority">The priority every window of this audio queues at.</param>
    /// <param name="listen">Builds a listener, on the first window and after a cancellation.</param>
    /// <param name="closed">Told once, after everything is released, that this audio closed.</param>
    public WindowedAudio(
        AnalysisWav wav,
        InferenceLane lane,
        InferencePriority priority,
        Func<IWindowListener> listen,
        Func<ValueTask>? closed = null
    )
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(listen);
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(
                nameof(priority),
                priority,
                "Only Normal and High exist."
            );
        }

        _wav = wav;
        _lane = lane;
        Priority = priority;
        _listen = listen;
        _closed = closed;
    }

    /// <summary>The priority every window of this audio queues at.</summary>
    public InferencePriority Priority { get; }

    /// <inheritdoc />
    public double DurationSeconds => _wav.DurationSeconds;

    /// <inheritdoc />
    public IReadOnlyList<TranscriptionWindow> Windows => _wav.Windows;

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeWindowAsync(
        int index,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Windows.Count);
        ThrowIfDisposed();

        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var window = Windows[index];
            if (CrashTrace.IsEnabled)
            {
                CrashTrace.Write(
                    "window.begin",
                    $"index={index}/{Windows.Count} from={window.Start:F3} to={window.End:F3} priority={Priority}"
                );
            }

            var samples = await _wav.ReadWindowAsync(index, cancellationToken)
                .ConfigureAwait(false);

            var heardFor = (double)samples.Length / SampleRate;
            samples = WithTrailingSilence(samples);

            using var lease = await _lane
                .EnterAsync(Priority, cancellationToken)
                .ConfigureAwait(false);

            _listener ??= _listen();
            try
            {
                var heard = Within(
                    heardFor,
                    await _listener.HearAsync(samples, cancellationToken).ConfigureAwait(false)
                );

                if (CrashTrace.IsEnabled)
                {
                    CrashTrace.Write(
                        "window.end",
                        $"index={index} segments={heard.Segments.Count} words={heard.Words.Count}"
                    );
                }

                return heard;
            }
            catch (OperationCanceledException)
            {
                CrashTrace.Write("window.cancelled", $"index={index}");

                var interrupted = _listener;
                _listener = null;
                await interrupted.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>The window's samples, followed by <see cref="TrailingSilenceSeconds"/> of quiet.</summary>
    private static float[] WithTrailingSilence(float[] samples)
    {
        var padded = new float[samples.Length + (int)(TrailingSilenceSeconds * SampleRate)];
        samples.CopyTo(padded, 0);

        return padded;
    }

    /// <summary>
    /// What was heard of the window itself, dropping anything that begins in
    /// the silence after it.
    /// </summary>
    /// <remarks>
    /// The silence is not part of the file, so nothing whisper.cpp says about
    /// it belongs in a transcript. Stitching would drop most of it anyway - it
    /// falls outside the window's share - but not for the last window, whose
    /// share runs to the end of time.
    /// </remarks>
    private static TranscriptionResult Within(double seconds, TranscriptionResult heard)
    {
        var segments = heard.Segments.Where(segment => segment.Start < seconds).ToList();
        var words = heard.Words.Where(word => word.Start < seconds).ToList();

        return segments.Count == heard.Segments.Count && words.Count == heard.Words.Count
            ? heard
            : new TranscriptionResult(segments, words);
    }

    /// <summary>
    /// Wait for a window in progress, release the listener and the file, and
    /// tell the engine. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CrashTrace.Write("audio.dispose.begin", $"windows={Windows.Count}");

        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listener is { } listener)
            {
                _listener = null;
                await listener.DisposeAsync().ConfigureAwait(false);
            }

            _wav.Dispose();
            CrashTrace.Write("audio.dispose.end");
        }
        finally
        {
            // Released, not disposed: a call already queued for its turn wakes,
            // sees the audio closed and throws, rather than waiting forever.
            _turn.Release();
        }

        if (_closed is not null)
        {
            await _closed().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
