using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

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
    private readonly PriorityWordSource _priorityWords;
    private readonly PrioritySubWindows _subWindows;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly InferenceLane _lane = new();

    private WhisperFactory? _factory;
    private int _open;
    private bool _releasePending;

    /// <summary>Numbers the processors built, so a trace can follow one of them.</summary>
    private int _processors;

    /// <summary>
    /// Whether whisper.cpp's own log has been wired into the trace. Its
    /// registration is process-wide and cannot be undone, so it happens once.
    /// </summary>
    private static int _nativeLogging;

    /// <param name="options">Model, device and language.</param>
    /// <param name="models">Where the weights are found.</param>
    /// <param name="logger">The engine's log.</param>
    /// <param name="priorityWords">
    /// The Priority Word List every opened audio re-hears its windows for,
    /// resolved once by the composition root; none, or an empty list, turns the
    /// Priority Word Pass off.
    /// </param>
    public WhisperNetEngine(
        TranscriptionOptions options,
        WhisperModelSource models,
        ILogger<WhisperNetEngine> logger,
        PriorityWordSource? priorityWords = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _models = models;
        _logger = logger;
        _priorityWords = priorityWords ?? PriorityWordSource.None;

        // Configuration, validated here rather than at the first window: a
        // nonsense sub-window layout is a startup failure, not a job failure.
        _subWindows = PriorityTuning.ForOptions(options).SubWindows;
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

            if (CrashTrace.IsEnabled)
            {
                CrashTrace.Write(
                    "engine.open",
                    $"priority={priority} seconds={wav.DurationSeconds:F3} windows={wav.Windows.Count} open={Volatile.Read(ref _open)} file={Path.GetFileName(wavPath)}"
                );
            }

            var words = _priorityWords.Words;
            return new WindowedAudio(
                wav,
                _lane,
                priority,
                () => new ProcessorListener(BuildProcessor(factory, prompt: null), BuiltLanguage),
                CloseAsync,
                words.IsEmpty
                    ? null
                    : new PriorityPass(
                        words,
                        () =>
                            new ProcessorListener(
                                BuildProcessor(factory, words.Prompt),
                                BuiltLanguage
                            ),
                        _subWindows
                    ),
                _logger
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
    /// The same load <see cref="OpenAsync"/> does, under the same gate, but
    /// without counting an open audio. whisper.cpp loads synchronously, so the
    /// caller should run this off its own thread if it has other work to get on
    /// with.
    /// </remarks>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is null)
            {
                await LoadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
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
        CrashTrace.Write("engine.model.drop.begin");
        factory.Dispose();
        CrashTrace.Write("engine.model.drop.end");
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

        // whisper.cpp's own log, into the trace. It is the only place the
        // native side explains itself, and what it prints last before a
        // fast-fail is usually the whole story.
        if (CrashTrace.IsEnabled && Interlocked.Exchange(ref _nativeLogging, 1) == 0)
        {
            LogProvider.AddLogger(
                (level, message) =>
                    CrashTrace.Write($"whisper.{level}", (message ?? string.Empty).TrimEnd())
            );
        }

        CrashTrace.Write(
            "engine.model.load.begin",
            $"model={_options.Model} device={_options.Device}"
        );
        _factory = WhisperFactory.FromPath(path, factoryOptions);
        CrashTrace.Write("engine.model.load.end", $"library={RuntimeOptions.LoadedLibrary}");
        LogLoaded(RuntimeOptions.LoadedLibrary);
        if (_priorityWords.Words.IsEmpty)
        {
            LogPriorityPassOff(_priorityWords.Origin);
        }
        else
        {
            LogPriorityPassOn(_priorityWords.Words.Count, _priorityWords.Origin);
        }

        return _factory;
    }

    /// <summary>
    /// A processor per opened audio - two with the Priority Word Pass on, the
    /// second built identically but for its <paramref name="prompt"/>.
    /// <c>WithTokenTimestamps</c> is the member that makes word boundaries
    /// available at all - without it every token carries its segment's times
    /// and <see cref="WhisperWords"/> would emit segment-wide words.
    /// </summary>
    /// <param name="factory">The loaded model.</param>
    /// <param name="prompt">
    /// The initial prompt, or null for none. The bare Priority Word List,
    /// comma-separated, is what raised crosstalk detection from 16 to 29 of 33
    /// on 5 s windows (docs/05-crosstalk-plan.md).
    /// </param>
    private WhisperProcessor BuildProcessor(WhisperFactory factory, string? prompt)
    {
        var builder = factory.CreateBuilder().WithTokenTimestamps();
        if (prompt is { Length: > 0 })
        {
            builder = builder.WithPrompt(prompt);
        }

        // Blank configuration means detect, matching the Python's `or None`.
        builder = _options.Language is { Length: > 0 } language
            ? builder.WithLanguage(language)
            : builder.WithLanguageDetection();

        // Building allocates whisper.cpp's per-processor state, which on CUDA
        // means VRAM: a trace that stops between these two lines says the card
        // could not give it any.
        var id = Interlocked.Increment(ref _processors);
        CrashTrace.Write(
            "engine.processor.build.begin",
            $"processor={id} prompted={prompt is { Length: > 0 }}"
        );
        var processor = builder.Build();
        CrashTrace.Write("engine.processor.build.end", $"processor={id}");

        return processor;
    }

    /// <summary>
    /// The language <see cref="BuildProcessor"/> builds processors with, as
    /// whisper.cpp names it: the configured one, else <c>auto</c> (detect).
    /// </summary>
    private string BuiltLanguage =>
        _options.Language is { Length: > 0 } language ? language : "auto";

    /// <summary>
    /// A whisper.cpp processor as an <see cref="IWindowListener"/>: what one
    /// window heard, on the window's own timeline, and the language it heard.
    /// </summary>
    /// <param name="processor">The whisper.cpp processor, owned from here on.</param>
    /// <param name="builtLanguage">The language it was built with (<see cref="BuiltLanguage"/>).</param>
    private sealed class ProcessorListener(WhisperProcessor processor, string builtLanguage)
        : IWindowListener
    {
        private readonly string _builtLanguage = builtLanguage;
        private string _language = builtLanguage;

        public async Task<WindowHearing> HearAsync(
            float[] samples,
            string? language,
            CancellationToken cancellationToken
        )
        {
            // Asked for a language: hear in it (the prompted listener, told what
            // the primary one heard). Asked for none: as it was built.
            var wanted = language ?? _builtLanguage;
            if (!string.Equals(wanted, _language, StringComparison.Ordinal))
            {
                processor.ChangeLanguage(wanted);
                _language = wanted;
            }

            var segments = new List<Segment>();
            var tokens = new List<WhisperToken>();
            string? heardIn = null;

            // The native call. A trace that ends here is a crash inside
            // whisper.cpp itself; one that ends at the line after it is a
            // crash in what came next.
            if (CrashTrace.IsEnabled)
            {
                CrashTrace.Write(
                    "window.native.begin",
                    $"samples={samples.Length} seconds={samples.Length / 16000.0:F3} cancelled={cancellationToken.IsCancellationRequested}"
                );
            }

            await foreach (
                var heard in processor
                    .ProcessAsync(samples, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                // Per segment, so a trace says whether the window died while
                // whisper.cpp was still decoding or after its last segment -
                // which is when the word timestamps are worked out.
                if (CrashTrace.IsEnabled)
                {
                    CrashTrace.Write(
                        "window.native.segment",
                        $"n={segments.Count} from={heard.Start.TotalSeconds:F3} to={heard.End.TotalSeconds:F3} tokens={heard.Tokens?.Length ?? 0}"
                    );
                }

                if (heardIn is null && heard.Language is { Length: > 0 } segmentLanguage)
                {
                    heardIn = segmentLanguage;
                }

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

            if (CrashTrace.IsEnabled)
            {
                CrashTrace.Write(
                    "window.native.end",
                    $"segments={segments.Count} tokens={tokens.Count}"
                );
            }

            // Joined over the whole window rather than segment by segment: a word
            // starts at the instant of the token before it, and for a segment's
            // first word that token belongs to the segment before.
            return new WindowHearing(
                new TranscriptionResult(segments, WhisperWords.Join(tokens)),
                heardIn
            );
        }

        /// <remarks>
        /// <c>DisposeAsync</c>, never <c>Dispose</c>: after a cancellation
        /// Whisper.net's asynchronous disposal waits for the native call to
        /// stop, where the synchronous one throws.
        /// </remarks>
        public async ValueTask DisposeAsync()
        {
            // After a cancellation this is where the process waits for the
            // native call to stop, so a trace that ends between these two is a
            // crash while whisper.cpp was being torn down.
            CrashTrace.Write("window.processor.dispose.begin");
            await processor.DisposeAsync().ConfigureAwait(false);
            CrashTrace.Write("window.processor.dispose.end");
        }
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
        Message = "Priority Word Pass on: {Count} word(s) from {Origin}"
    )]
    private partial void LogPriorityPassOn(int count, string origin);

    [LoggerMessage(Level = LogLevel.Information, Message = "Priority Word Pass off: {Origin}")]
    private partial void LogPriorityPassOff(string origin);

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
