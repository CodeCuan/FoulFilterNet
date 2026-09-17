using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FoulFilterNet.SmartCut;

/// <summary>
/// Where the Google API key comes from. A delegate rather than a configuration
/// key so the secret has one deliberate source: the <c>GOOGLE_API_KEY</c>
/// environment variable. It is never read through configuration, because
/// configuration includes committed appsettings, and it must never be logged.
/// </summary>
public delegate string? GoogleApiKeySource();

/// <summary>
/// Registration for the Smart Cut stage, including the feature flag.
/// </summary>
/// <remarks>
/// The flag resolves to an <em>implementation</em>, not a branch: the pipeline
/// always receives an <see cref="ISmartCutAdvisor"/> and never asks whether the
/// feature is on. Enabled-but-unconfigured lands on the same no-op with a warning
/// - a missing API key is a deployment mistake, not a reason to fail every job.
/// </remarks>
public static class SmartCutServiceCollectionExtensions
{
    /// <summary>Named client, so the long timeout applies only to Smart Cut.</summary>
    public const string HttpClientName = "FoulFilterNet.SmartCut";

    /// <summary>The environment variable the Python read, kept for continuity.</summary>
    public const string GoogleApiKeyVariable = "GOOGLE_API_KEY";

    /// <summary>
    /// Bind <c>SmartCut</c> from configuration and register the advisor.
    /// </summary>
    /// <remarks>
    /// The API key is deliberately <b>not</b> read from
    /// <paramref name="configuration"/>. Configuration merges every JSON file the
    /// host loads, including the committed <c>appsettings.json</c>, so looking the
    /// key up there would let a key pasted into a tracked file switch Smart Cut on
    /// and travel with the repository. It comes from the <c>GOOGLE_API_KEY</c>
    /// environment variable and nowhere else.
    /// </remarks>
    public static IServiceCollection AddSmartCut(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SmartCutOptions>(configuration.GetSection(SmartCutOptions.SectionName));

        return services.AddSmartCut();
    }

    /// <summary>
    /// Register the advisor against whatever <see cref="SmartCutOptions"/> the
    /// caller has already configured. Defaults leave Smart Cut off.
    /// </summary>
    public static IServiceCollection AddSmartCut(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(
            HttpClientName,
            client => client.Timeout = OpenAiCompatibleTransport.RequestTimeout
        );
        services.TryAddSingleton<GoogleApiKeySource>(_ =>
            () => Environment.GetEnvironmentVariable(GoogleApiKeyVariable)
        );
        services.TryAddSingleton<ISmartCutAdvisor>(CreateAdvisor);

        return services;
    }

    private static ISmartCutAdvisor CreateAdvisor(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<SmartCutOptions>>().Value;
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(typeof(SmartCutServiceCollectionExtensions));

        if (!options.Enabled)
        {
            logger.LogDebug(
                "Smart Cut is disabled; hits will be used exactly as the matcher found them."
            );

            return NoOpSmartCutAdvisor.Instance;
        }

        var transport = CreateTransport(provider, options, loggerFactory, logger);

        if (transport is null)
        {
            return NoOpSmartCutAdvisor.Instance;
        }

        return new LlmSmartCutAdvisor(transport, loggerFactory.CreateLogger<LlmSmartCutAdvisor>());
    }

    /// <summary>
    /// Null means "enabled, but not usable" - already warned about, and the caller
    /// falls back to the no-op.
    /// </summary>
    /// <remarks>
    /// Only the Google path can be checked here. Whether a local server is actually
    /// listening is not knowable at startup, so that failure is handled per hit:
    /// <see cref="OpenAiCompatibleTransport"/> reports it unavailable and the hit
    /// keeps its original timestamps.
    /// </remarks>
    private static ISmartCutTransport? CreateTransport(
        IServiceProvider provider,
        SmartCutOptions options,
        ILoggerFactory loggerFactory,
        ILogger logger
    )
    {
        var clients = provider.GetRequiredService<IHttpClientFactory>();

        switch (options.Mode)
        {
            case SmartCutMode.Google:
                var apiKey = provider.GetRequiredService<GoogleApiKeySource>()();

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    logger.LogWarning(
                        "Smart Cut is enabled in Google mode but {Variable} is not set; falling back to the no-op advisor.",
                        GoogleApiKeyVariable
                    );

                    return null;
                }

                if (string.IsNullOrWhiteSpace(options.GoogleModel))
                {
                    logger.LogWarning(
                        "Smart Cut is enabled in Google mode but no model is configured; falling back to the no-op advisor."
                    );

                    return null;
                }

                return new GeminiTransport(
                    clients.CreateClient(HttpClientName),
                    apiKey,
                    options.GoogleModel,
                    loggerFactory.CreateLogger<GeminiTransport>()
                );

            case SmartCutMode.Local:
                if (
                    !Uri.TryCreate(options.LocalUrl, UriKind.Absolute, out var url)
                    || url.Scheme is not ("http" or "https")
                )
                {
                    logger.LogWarning(
                        "Smart Cut is enabled in Local mode but {Url} is not a usable endpoint; falling back to the no-op advisor.",
                        options.LocalUrl
                    );

                    return null;
                }

                return new OpenAiCompatibleTransport(
                    clients.CreateClient(HttpClientName),
                    options.LocalUrl,
                    options.LocalModel,
                    loggerFactory.CreateLogger<OpenAiCompatibleTransport>()
                );

            default:
                logger.LogWarning(
                    "Smart Cut is enabled but {Mode} is not a transport it knows; falling back to the no-op advisor.",
                    options.Mode
                );

                return null;
        }
    }
}
