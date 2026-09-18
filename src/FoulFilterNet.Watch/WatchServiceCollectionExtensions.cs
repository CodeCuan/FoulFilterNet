using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.Watch;

/// <summary>
/// Registration for Watch Sessions: the <see cref="WatchSessionManager"/>
/// singleton, its <see cref="WatchSessionSweeper"/>, and a
/// <see cref="FileBadWordsSource"/> over <see cref="WatchOptions.BadWordsPath"/>.
/// </summary>
/// <remarks>
/// The host still owns the adapters it already registers for Jobs - the
/// <see cref="IWhisperEngine"/> (which must be the <em>same</em> singleton, so
/// Jobs and sessions share one priority lane), the <see cref="IAudioPreparer"/>
/// - and the <see cref="IWebAudioSource"/>. The Transcript cache is built with
/// the host's <see cref="TranscriptStoreFactory"/> when it registers one, and
/// is a plain <see cref="TranscriptStore"/> otherwise.
/// </remarks>
public static class WatchServiceCollectionExtensions
{
    /// <summary>Bind <see cref="WatchOptions"/> from the <c>Watch</c> section, and register.</summary>
    public static IServiceCollection AddWatch(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<WatchOptions>(configuration.GetSection(WatchOptions.SectionName));
        return services.AddWatch();
    }

    /// <summary>Configure <see cref="WatchOptions"/> in code, and register.</summary>
    public static IServiceCollection AddWatch(
        this IServiceCollection services,
        Action<WatchOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return services.AddWatch();
    }

    /// <summary>Register against whatever <see cref="WatchOptions"/> the caller has configured.</summary>
    public static IServiceCollection AddWatch(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WatchOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBadWordsSource>(provider => new FileBadWordsSource(
            provider.GetRequiredService<IOptions<WatchOptions>>().Value.BadWordsPath
        ));
        services.TryAddSingleton(CreateManager);

        // TryAddEnumerable: calling AddWatch twice must not sweep twice.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                Microsoft.Extensions.Hosting.IHostedService,
                WatchSessionSweeper
            >()
        );

        return services;
    }

    private static WatchSessionManager CreateManager(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<WatchOptions>>().Value;
        var stores = provider.GetService<TranscriptStoreFactory>();
        var transcripts = stores is null
            ? new TranscriptStore(options.TranscriptDirectory)
            : stores(options.TranscriptDirectory);

        return new WatchSessionManager(
            provider.GetRequiredService<IWebAudioSource>(),
            provider.GetRequiredService<IAudioPreparer>(),
            provider.GetRequiredService<IWhisperEngine>(),
            transcripts,
            provider.GetRequiredService<IBadWordsSource>(),
            options,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILoggerFactory>()
        );
    }
}
