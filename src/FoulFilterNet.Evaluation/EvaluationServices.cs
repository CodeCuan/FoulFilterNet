using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Media;
using FoulFilterNet.Pipeline;
using FoulFilterNet.SmartCut;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.Evaluation;

/// <summary>
/// The engines an evaluation runs: the same composition the CLI uses, so what is
/// scored is what a job would have done.
/// </summary>
public static class EvaluationServices
{
    /// <summary>
    /// Register the real pipeline. Weights are never downloaded: the model source
    /// is built without an acquisition delegate, so missing weights fail with the
    /// path that was expected.
    /// </summary>
    public static IServiceCollection AddEvaluationPipeline(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TranscriptionOptions>(configuration.GetSection("Transcription"));
        services.AddSmartCut(configuration);

        services.AddSingleton<IFFmpegRunner, FFmpegRunner>();
        services.AddSingleton<IMediaProber, FFprobeMediaProber>();
        services.AddSingleton<IAudioPreparer, FFmpegAudioPreparer>();
        services.AddSingleton<IMediaEditor, MediaEditor>();

        // One Priority Word List for the engine and the pipeline's CutPadding,
        // which the report also scores with, as a job would use them.
        services.AddSingleton(provider =>
            PriorityWordFile.Resolve(
                provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value,
                configuration[ConfigurationKeys.DataDirectory]
            )
        );
        services.AddSingleton(provider =>
            CutPadding.ForPriorityWords(provider.GetRequiredService<PriorityWordSource>().Words)
        );
        services.AddSingleton<IWhisperEngine>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value;
            return new WhisperNetEngine(
                options,
                new WhisperModelSource(options.ModelDirectory),
                provider.GetRequiredService<ILogger<WhisperNetEngine>>(),
                provider.GetRequiredService<PriorityWordSource>()
            );
        });

        services.AddSingleton<ITranscriber>(provider => new ReleasePolicyTranscriber(
            new WhisperTranscriber(
                provider.GetRequiredService<IWhisperEngine>(),
                provider.GetRequiredService<IAudioPreparer>()
            ),
            provider.GetRequiredService<IOptions<TranscriptionOptions>>().Value
        ));

        services.AddSingleton<IAligner, PassThroughAligner>();
        services.AddSingleton<TranscriptStoreFactory>(_ =>
            directory => new TranscriptStore(directory)
        );
        services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<SmartCutOptions>>().Value
        );
        services.AddSingleton<IMediaPipeline, MediaPipeline>();
        services.AddSingleton<FixtureEvaluator>();

        return services;
    }
}
