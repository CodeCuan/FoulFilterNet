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

            var samples = await _wav.ReadWindowAsync(index, cancellationToken)
                .ConfigureAwait(false);

            using var lease = await _lane
                .EnterAsync(Priority, cancellationToken)
                .ConfigureAwait(false);

            _listener ??= _listen();
            try
            {
                return await _listener.HearAsync(samples, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
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

        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_listener is { } listener)
            {
                _listener = null;
                await listener.DisposeAsync().ConfigureAwait(false);
            }

            _wav.Dispose();
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
