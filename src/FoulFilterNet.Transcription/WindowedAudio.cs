using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// The Priority Word Pass for one opened audio: the words it hunts for, and a
/// way to build the listener that hears sub-windows with those words as its
/// prompt (docs/05-crosstalk-plan.md).
/// </summary>
public sealed record PriorityPass
{
    /// <param name="words">What the pass keeps from the sub-windows. An empty list turns it off.</param>
    /// <param name="listen">Builds the prompted listener, on the first window and after a cancellation.</param>
    public PriorityPass(PriorityWordList words, Func<IWindowListener> listen)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(listen);

        Words = words;
        Listen = listen;
    }

    /// <summary>What the pass keeps from the sub-windows.</summary>
    public PriorityWordList Words { get; }

    /// <summary>Builds the prompted listener.</summary>
    public Func<IWindowListener> Listen { get; }
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
/// <para>
/// <b>The Priority Word Pass.</b> With a <see cref="PriorityPass"/> whose list
/// is not empty, a window heard by the primary listener is heard again in
/// <see cref="PriorityWindows"/> sub-windows by a second, prompted listener,
/// each sub-window padded with silence exactly as a window is; the priority
/// words only the sub-windows heard are merged into the window's words. So
/// every consumer of windows - Watch, batch, the Rescan Pass - gets them with
/// no change of its own. The prompted listener is built once, lazily, and
/// thrown away on a cancellation by the same rule as the primary one.
/// </para>
/// <para>
/// <b>Every inference takes its own turn in the lane</b> - the primary hearing
/// and each sub-window - rather than one turn covering the whole window. A
/// window with the pass is a dozen inferences; holding the lane across all of
/// them would let a Job's window keep a waiting Watch window off the GPU for
/// all twelve, where the lane promises at most one. The price is that two
/// audios' windows can interleave at sub-window granularity, which changes
/// nothing either of them hears. A cancellation stops the window at its next
/// turn.
/// </para>
/// </remarks>
public sealed partial class WindowedAudio : IAnalysisAudio
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
    private readonly Func<ValueTask>? _closed;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly ListenerSlot _primary;
    private readonly ListenerSlot? _prompted;
    private readonly PriorityWordList? _priorityWords;
    private readonly ILogger _logger;

    private int _disposed;

    /// <param name="wav">The opened file. This instance owns it from here on.</param>
    /// <param name="lane">The lane every window takes, shared with all other audio.</param>
    /// <param name="priority">The priority every window of this audio queues at.</param>
    /// <param name="listen">Builds a listener, on the first window and after a cancellation.</param>
    /// <param name="closed">Told once, after everything is released, that this audio closed.</param>
    /// <param name="priorityPass">
    /// The Priority Word Pass to run on every window. None, or an empty list,
    /// hears windows exactly as before and never builds a prompted listener.
    /// </param>
    /// <param name="logger">Where each window says how many priority words it added.</param>
    public WindowedAudio(
        AnalysisWav wav,
        InferenceLane lane,
        InferencePriority priority,
        Func<IWindowListener> listen,
        Func<ValueTask>? closed = null,
        PriorityPass? priorityPass = null,
        ILogger? logger = null
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
        _primary = new ListenerSlot(listen);
        _closed = closed;
        _logger = logger ?? NullLogger.Instance;

        if (priorityPass is { Words.IsEmpty: false })
        {
            _priorityWords = priorityPass.Words;
            _prompted = new ListenerSlot(priorityPass.Listen);
        }
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

            var heard = await HearAsync(_primary, samples, cancellationToken).ConfigureAwait(false);

            if (CrashTrace.IsEnabled)
            {
                CrashTrace.Write(
                    "window.end",
                    $"index={index} segments={heard.Segments.Count} words={heard.Words.Count}"
                );
            }

            if (_prompted is null || _priorityWords is null)
            {
                return heard;
            }

            var priorityWords = await HearPriorityWordsAsync(
                    _prompted,
                    _priorityWords,
                    samples,
                    cancellationToken
                )
                .ConfigureAwait(false);
            var merged = PriorityWindows.Merge(heard, priorityWords);

            LogPriorityWords(index, merged.Added.Count, priorityWords.Count);
            if (CrashTrace.IsEnabled)
            {
                CrashTrace.Write(
                    "window.priority",
                    $"index={index} heard={priorityWords.Count} added={merged.Added.Count}"
                );
            }

            return merged.Result;
        }
        catch (OperationCanceledException)
        {
            CrashTrace.Write("window.cancelled", $"index={index}");
            throw;
        }
        finally
        {
            _turn.Release();
        }
    }

    /// <summary>
    /// The priority words the sub-windows of a window heard, on the window's
    /// timeline: each sub-window cut from the window's own samples, then padded
    /// and heard in a turn of its own.
    /// </summary>
    private async Task<IReadOnlyList<Word>> HearPriorityWordsAsync(
        ListenerSlot prompted,
        PriorityWordList priorityWords,
        float[] window,
        CancellationToken cancellationToken
    )
    {
        var subWindows = PriorityWindows.Plan((double)window.Length / SampleRate);
        var heard = new List<(TranscriptionWindow, TranscriptionResult)>(subWindows.Count);
        foreach (var subWindow in subWindows)
        {
            var first = Math.Min(window.Length, (int)Math.Round(subWindow.Start * SampleRate));
            var end = Math.Min(window.Length, (int)Math.Round(subWindow.End * SampleRate));
            if (end <= first)
            {
                continue;
            }

            heard.Add(
                (
                    subWindow,
                    await HearAsync(prompted, window[first..end], cancellationToken)
                        .ConfigureAwait(false)
                )
            );
        }

        return PriorityWindows.Stitch(heard, priorityWords);
    }

    /// <summary>
    /// One inference in one turn of the lane: <paramref name="samples"/>, padded
    /// with silence, heard by the slot's listener, keeping only what began in
    /// the samples themselves. A cancellation while hearing throws the listener
    /// away - and waits for it - before the turn ends.
    /// </summary>
    private async Task<TranscriptionResult> HearAsync(
        ListenerSlot slot,
        float[] samples,
        CancellationToken cancellationToken
    )
    {
        var heardFor = (double)samples.Length / SampleRate;
        var padded = WithTrailingSilence(samples);

        using var lease = await _lane.EnterAsync(Priority, cancellationToken).ConfigureAwait(false);

        var listener = slot.Current ??= slot.Build();
        try
        {
            return Within(
                heardFor,
                await listener.HearAsync(padded, cancellationToken).ConfigureAwait(false)
            );
        }
        catch (OperationCanceledException)
        {
            slot.Current = null;
            await listener.DisposeAsync().ConfigureAwait(false);
            throw;
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
            await _primary.DisposeAsync().ConfigureAwait(false);
            if (_prompted is not null)
            {
                await _prompted.DisposeAsync().ConfigureAwait(false);
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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Window {Index}: the Priority Word Pass heard {Heard} priority word(s) and added {Added} the primary pass missed"
    )]
    private partial void LogPriorityWords(int index, int added, int heard);

    /// <summary>
    /// A listener built when it is first needed and kept until a cancellation
    /// throws it away or the audio closes. Only touched in this audio's turn.
    /// </summary>
    private sealed class ListenerSlot(Func<IWindowListener> build) : IAsyncDisposable
    {
        public IWindowListener? Current { get; set; }

        public IWindowListener Build() => build();

        public async ValueTask DisposeAsync()
        {
            if (Current is { } listener)
            {
                Current = null;
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
