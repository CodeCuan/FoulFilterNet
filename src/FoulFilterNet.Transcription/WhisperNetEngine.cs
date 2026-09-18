using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FoulFilterNet.Transcription;

/// <summary>
/// whisper.cpp inference through Whisper.net, on CUDA where there is a card.
/// </summary>
/// <remarks>
/// <para>
/// This is the engine ADR-0006 chose, and the reason the Python's second model
/// is gone: with token timestamps enabled whisper.cpp reports where each word
/// was spoken, so transcription and alignment are one pass rather than two
/// models sharing a card.
/// </para>
/// <para>
/// The model loads on the first open rather than in the constructor.
/// That is what lets the composition root resolve this type without touching a
/// GPU, and it is what makes releasing after a job that never transcribed - one
/// that resumed a cached transcript (ADR-0002) - a no-op instead of a failure.
/// One factory is kept for as many jobs as the release policy allows; each
/// opened audio builds its own processor over it.
/// </para>
/// <para>
/// Batch Jobs and Watch Sessions share this one engine, so it owns the one
/// <see cref="InferenceLane"/> every window of every opened audio takes: one
/// inference on the card at a time, Watch windows first (W07). The engine is
/// a singleton in every composition root, which is what makes the lane shared.
/// </para>
/// </remarks>
public sealed partial class WhisperNetEngine : IWhisperEngine, IDisposable
{
    private readonly TranscriptionOptions _options;
    private readonly WhisperModelSource _models;
    private readonly ILogger<WhisperNetEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly InferenceLane _lane = new();

    private WhisperFactory? _factory;
    private int _open;
    private bool _releasePending;

    public WhisperNetEngine(
        TranscriptionOptions options,
        WhisperModelSource models,
        ILogger<WhisperNetEngine> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _models = models;
        _logger = logger;
    }

    /// <summary>
    /// Whether a model is currently resident. False before the first
    /// transcription and after a release.
    /// </summary>
    public bool IsModelLoaded => Volatile.Read(ref _factory) is not null;

    /// <inheritdoc />
    /// <remarks>
    /// The model loads here, outside the lane, so a Job's first window never
    /// makes a waiting viewer sit through a model load as well. Each opened
    /// audio builds its own processor on its first window; the lane keeps them
    /// from ever running at once.
    /// </remarks>
    public async Task<IAnalysisAudio> OpenAsync(
        string wavPath,
        InferencePriority priority = InferencePriority.Normal,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(
                nameof(priority),
                priority,
                "Only Normal and High exist."
            );
        }

        var factory = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wav = await AnalysisWav.OpenAsync(wavPath, cancellationToken).ConfigureAwait(false);
            LogOpened(wav.DurationSeconds, wav.Windows.Count, priority);

            return new WindowedAudio(
                wav,
                _lane,
                priority,
                () => new ProcessorListener(BuildProcessor(factory)),
                CloseAsync
            );
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent, and cheap when there is nothing to drop: it now sits on the
    /// critical path of every job, because the pipeline releases in a
    /// <c>finally</c> whether the job succeeded, failed or was cancelled.
    /// With audio still open - a Watch Session using the model while a Job
    /// ends - disposing the factory would pull it out from under a processor,
    /// so the release is remembered and carried out when the last audio closes.
    /// </remarks>
    public async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_factory is null)
            {
                return;
            }

            if (_open > 0)
            {
                _releasePending = true;
                LogReleaseDeferred(_open);
                return;
            }

            DropFactory();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _gate.Dispose();
    }

    /// <summary>
    /// The model, loaded if need be, counted as in use by one more opened
    /// audio - both under the gate, so a release can never land between them.
    /// Cancellation is checked before anything is resolved or loaded, so a job
    /// cancelled while it queued stops instead of paying for a model it will
    /// never use.
    /// </summary>
    private async Task<WhisperFactory> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var factory = _factory ?? await LoadAsync(cancellationToken).ConfigureAwait(false);
            _open++;
            return factory;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One opened audio closed; carry out a release that waited for it.</summary>
    private async ValueTask CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _open--;
            if (_open == 0 && _releasePending)
            {
                DropFactory();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Under the gate, with no audio open.</summary>
    private void DropFactory()
    {
        _releasePending = false;
        var factory = _factory;
        if (factory is null)
        {
            return;
        }

        _factory = null;
        factory.Dispose();
        LogReleased();
    }

    /// <summary>
    /// Load the model. Called under the gate, with none resident.
    /// </summary>
    private async Task<WhisperFactory> LoadAsync(CancellationToken cancellationToken)
    {
        var path = await _models
            .ResolveAsync(_options.Model, cancellationToken)
            .ConfigureAwait(false);

        // Whisper.net picks the first native runtime it can load from this
        // global order, and only reads it while the first factory loads.
        WhisperRuntime.Apply(_options.Device, RuntimeOptions.RuntimeLibraryOrder);

        // DTW word timestamps, which need the model's own alignment heads;
        // a model whose heads whisper.cpp does not know falls back to the
        // cruder token timestamps (ADR-0006).
        var heads = WhisperModelFiles.AlignmentHeadsFor(_options.Model);
        var factoryOptions = WhisperRuntime.FactoryOptions(_options.Device, heads);

        LogLoading(_options.Model, path, _options.Device, heads);
        _factory = WhisperFactory.FromPath(path, factoryOptions);
        LogLoaded(RuntimeOptions.LoadedLibrary);

        return _factory;
    }

    /// <summary>
    /// A processor per opened audio. <c>WithTokenTimestamps</c> is the member
    /// that makes word boundaries available at all - without it every token
    /// carries its segment's times and <see cref="WhisperWords"/> would emit
    /// segment-wide words.
    /// </summary>
    private WhisperProcessor BuildProcessor(WhisperFactory factory)
    {
        var builder = factory.CreateBuilder().WithTokenTimestamps();

        // Blank configuration means detect, matching the Python's `or None`.
        builder = _options.Language is { Length: > 0 } language
            ? builder.WithLanguage(language)
            : builder.WithLanguageDetection();

        return builder.Build();
    }

    /// <summary>
    /// A whisper.cpp processor as an <see cref="IWindowListener"/>: what one
    /// window heard, on the window's own timeline.
    /// </summary>
    private sealed class ProcessorListener(WhisperProcessor processor) : IWindowListener
    {
        public async Task<TranscriptionResult> HearAsync(
            float[] samples,
            CancellationToken cancellationToken
        )
        {
            var segments = new List<Segment>();
            var tokens = new List<WhisperToken>();

            await foreach (
                var heard in processor
                    .ProcessAsync(samples, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                var text = (heard.Text ?? string.Empty).Trim();
                if (text.Length > 0 && heard.End > heard.Start)
                {
                    segments.Add(
                        new Segment(
                            Times.Round(heard.Start.TotalSeconds),
                            Times.Round(heard.End.TotalSeconds),
                            text
                        )
                    );
                }

                if (heard.Tokens is { Length: > 0 } spoken)
                {
                    tokens.AddRange(spoken);
                }
            }

            // Joined over the whole window rather than segment by segment: a word
            // starts at the instant of the token before it, and for a segment's
            // first word that token belongs to the segment before.
            return new TranscriptionResult(segments, WhisperWords.Join(tokens));
        }

        /// <remarks>
        /// <c>DisposeAsync</c>, never <c>Dispose</c>: after a cancellation
        /// Whisper.net's asynchronous disposal waits for the native call to
        /// stop, where the synchronous one throws.
        /// </remarks>
        public ValueTask DisposeAsync() => processor.DisposeAsync();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Loading transcription model {Model} from {Path}, device preference {Device}, alignment heads {Heads}"
    )]
    private partial void LogLoading(
        string model,
        string path,
        TranscriptionDevice device,
        WhisperAlignmentHeadsPreset? heads
    );

    /// <remarks>
    /// The loaded runtime is the one fact that says whether this machine is
    /// transcribing on its card or quietly on its CPU. Logged as the enum rather
    /// than as a string so nothing is formatted when logging is off (CA1873).
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Transcription model ready on the {Runtime} runtime"
    )]
    private partial void LogLoaded(RuntimeLibrary? runtime);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Opened {Duration} s of analysis audio in {Windows} window(s) at {Priority} priority"
    )]
    private partial void LogOpened(double duration, int windows, InferencePriority priority);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Model release deferred until {Open} open analysis audio close"
    )]
    private partial void LogReleaseDeferred(int open);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Transcription model released; VRAM handed back"
    )]
    private partial void LogReleased();
}
