using FoulFilterNet.Domain;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Wave;

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
/// The model loads on the first transcription rather than in the constructor.
/// That is what lets the composition root resolve this type without touching a
/// GPU, and it is what makes releasing after a job that never transcribed - one
/// that resumed a cached transcript (ADR-0002) - a no-op instead of a failure.
/// One factory is kept for as many jobs as the release policy allows; each
/// transcription builds its own processor over it.
/// </para>
/// </remarks>
public sealed partial class WhisperNetEngine : IWhisperEngine, IDisposable
{
    private readonly TranscriptionOptions _options;
    private readonly WhisperModelSource _models;
    private readonly ILogger<WhisperNetEngine> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WhisperFactory? _factory;

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
    public async Task<TranscriptionResult> TranscribeWavAsync(
        string wavPath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);

        var factory = await LoadAsync(cancellationToken).ConfigureAwait(false);

        await using var processor = BuildProcessor(factory);
        await using var audio = File.OpenRead(wavPath);

        var wave = new WaveParser(audio);
        await wave.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (wave.BitsPerSample != 16)
        {
            throw new NotSupportedException(
                $"Expected 16-bit PCM in '{wavPath}', found {wave.BitsPerSample}-bit."
            );
        }

        var frameBytes = wave.Channels * sizeof(short);
        var frameCount = (long)wave.DataChunkSize / frameBytes;
        var windows = TranscriptionWindows.Plan((double)frameCount / wave.SampleRate);

        var heard = new List<(TranscriptionWindow, TranscriptionResult)>(windows.Count);
        foreach (var window in windows)
        {
            var firstFrame = (long)Math.Round(window.Start * wave.SampleRate);
            var frames = (int)(
                Math.Min(frameCount, (long)Math.Round(window.End * wave.SampleRate)) - firstFrame
            );
            var samples = await ReadWindowAsync(
                    audio,
                    (long)wave.DataChunkPosition + firstFrame * frameBytes,
                    frames,
                    wave.Channels,
                    cancellationToken
                )
                .ConfigureAwait(false);

            heard.Add(
                (
                    window,
                    await TranscribeWindowAsync(processor, samples, cancellationToken)
                        .ConfigureAwait(false)
                )
            );
        }

        var result = TranscriptionWindows.Stitch(heard);
        LogTranscribed(result.Segments.Count, result.Words.Count, windows.Count);
        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent, and cheap when there is nothing to drop: it now sits on the
    /// critical path of every job, because the pipeline releases in a
    /// <c>finally</c> whether the job succeeded, failed or was cancelled.
    /// </remarks>
    public async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var factory = _factory;
            if (factory is null)
            {
                return;
            }

            _factory = null;
            factory.Dispose();
            LogReleased();
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
    /// One window's frames as mono samples in [-1, 1), channels averaged the way
    /// Whisper.net's own parser does.
    /// </summary>
    private static async Task<float[]> ReadWindowAsync(
        FileStream audio,
        long position,
        int frames,
        int channels,
        CancellationToken cancellationToken
    )
    {
        var bytes = new byte[frames * channels * sizeof(short)];
        audio.Position = position;
        var read = await audio
            .ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        var samples = new float[read / (channels * sizeof(short))];
        for (var frame = 0; frame < samples.Length; frame++)
        {
            var sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += BitConverter.ToInt16(bytes, (frame * channels + channel) * sizeof(short));
            }

            samples[frame] = sum / (channels * 32768f);
        }

        return samples;
    }

    /// <summary>What one window heard, on the window's own timeline.</summary>
    private static async Task<TranscriptionResult> TranscribeWindowAsync(
        WhisperProcessor processor,
        float[] samples,
        CancellationToken cancellationToken
    )
    {
        var segments = new List<Segment>();
        var tokens = new List<WhisperToken>();

        await foreach (
            var heard in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false)
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

    /// <summary>
    /// The model, loaded once. Cancellation is checked before anything is
    /// resolved or loaded, so a job cancelled while it queued for the GPU stops
    /// instead of paying for a model it will never use.
    /// </summary>
    private async Task<WhisperFactory> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var loaded = _factory;
        if (loaded is not null)
        {
            return loaded;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is not null)
            {
                return _factory;
            }

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
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A processor per transcription. <c>WithTokenTimestamps</c> is the member
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
        Message = "Transcribed {Segments} segment(s), {Words} word(s) in {Windows} window(s)"
    )]
    private partial void LogTranscribed(int segments, int words, int windows);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Transcription model released; VRAM handed back"
    )]
    private partial void LogReleased();
}
