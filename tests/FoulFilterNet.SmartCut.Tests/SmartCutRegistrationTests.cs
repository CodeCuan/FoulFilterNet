using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FoulFilterNet.SmartCut.Tests;

internal static class SmartCutContainer
{
    /// <summary>Resolve the advisor the way the host would, from options alone.</summary>
    public static ISmartCutAdvisor Resolve(Action<IServiceCollection>? arrange = null)
    {
        var services = new ServiceCollection();
        arrange?.Invoke(services);

        return services.AddSmartCut().BuildServiceProvider().GetRequiredService<ISmartCutAdvisor>();
    }

    /// <summary>Resolve it the way appsettings plus environment would.</summary>
    public static ISmartCutAdvisor ResolveFrom(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                settings.Select(setting => new KeyValuePair<string, string?>(
                    setting.Key,
                    setting.Value
                ))
            )
            .Build();

        return new ServiceCollection()
            .AddSmartCut(configuration)
            .BuildServiceProvider()
            .GetRequiredService<ISmartCutAdvisor>();
    }
}

/// <summary>
/// Out of the box. Smart Cut costs an LLM round trip per hit and is the only
/// stage that can change what gets cut, so it stays off until someone opts in.
/// </summary>
public class WhenNothingIsConfigured
{
    private readonly ISmartCutAdvisor _advisor;
    private readonly SmartCutDecision _decision;

    public WhenNothingIsConfigured()
    {
        _advisor = SmartCutContainer.Resolve();
        _decision = _advisor
            .RefineAsync(PromptWindow.Words, "hoe", centerIndex: 6, allowWidening: true)
            .GetAwaiter()
            .GetResult();

        _advisor.ShouldNotBeNull();
    }

    [Fact]
    public void ResolvesTheNoOpAdvisor() => _advisor.ShouldBeOfType<NoOpSmartCutAdvisor>();

    [Fact]
    public void ReportsItselfDisabledSoTheUiBadgeIsHonest() => _advisor.IsEnabled.ShouldBeFalse();

    [Fact]
    public void StillAnswersEveryHitSoThePipelineNeedsNoConditional() =>
        _decision.Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void NeverRejectsAHitWhileSwitchedOff() =>
        _decision.Outcome.ShouldNotBe(SmartCutOutcome.Reject);

    [Fact]
    public void IsAlsoWhatAnEmptyAppsettingsSectionProduces() =>
        SmartCutContainer.ResolveFrom(("SmartCut:Mode", "Local")).IsEnabled.ShouldBeFalse();
}

public class WhenTheFlagIsOnForALocalServer
{
    private readonly ISmartCutAdvisor _advisor;

    public WhenTheFlagIsOnForALocalServer()
    {
        _advisor = SmartCutContainer.ResolveFrom(
            ("SmartCut:Enabled", "true"),
            ("SmartCut:Mode", "Local"),
            ("SmartCut:LocalUrl", "http://localhost:8080/v1/chat/completions")
        );

        _advisor.ShouldNotBeNull();
    }

    [Fact]
    public void ResolvesTheRealAdvisor() => _advisor.ShouldBeOfType<LlmSmartCutAdvisor>();

    [Fact]
    public void ReportsItselfEnabled() => _advisor.IsEnabled.ShouldBeTrue();

    [Fact]
    public void TalksToAnOpenAiCompatibleEndpoint() =>
        _advisor
            .ShouldBeOfType<LlmSmartCutAdvisor>()
            .Transport.ShouldBeOfType<OpenAiCompatibleTransport>();
}

public class WhenTheFlagIsOnForGoogle
{
    private readonly ISmartCutAdvisor _advisor;

    public WhenTheFlagIsOnForGoogle()
    {
        _advisor = SmartCutContainer.Resolve(services =>
        {
            services.Configure<SmartCutOptions>(options =>
            {
                options.Enabled = true;
                options.Mode = SmartCutMode.Google;
            });
            services.AddSingleton<GoogleApiKeySource>(_ => () => "a-key-from-the-environment");
        });

        _advisor.ShouldNotBeNull();
    }

    [Fact]
    public void ResolvesTheRealAdvisor() => _advisor.ShouldBeOfType<LlmSmartCutAdvisor>();

    [Fact]
    public void TalksToGemini() =>
        _advisor.ShouldBeOfType<LlmSmartCutAdvisor>().Transport.ShouldBeOfType<GeminiTransport>();

    [Fact]
    public void ReadsTheKeyFromTheEnvironmentRatherThanAppsettings() =>
        SmartCutContainer
            .ResolveFrom(
                ("SmartCut:Enabled", "true"),
                ("SmartCut:Mode", "Google"),
                ("SmartCut:GoogleApiKey", "a-key-somebody-committed")
            )
            .IsEnabled.ShouldBeFalse();

    /// <summary>
    /// The name the key is actually read under. Configuration merges every
    /// committed JSON file, so a top-level <c>GOOGLE_API_KEY</c> in
    /// <c>appsettings.json</c> arrives here exactly like this - and must be
    /// ignored just as firmly as the nested spelling above.
    /// </summary>
    [Fact]
    public void IgnoresTheKeyEvenUnderItsOwnNameInConfiguration() =>
        SmartCutContainer
            .ResolveFrom(
                ("SmartCut:Enabled", "true"),
                ("SmartCut:Mode", "Google"),
                ("GOOGLE_API_KEY", "a-key-somebody-committed")
            )
            .IsEnabled.ShouldBeFalse();
}

/// <summary>
/// The deployment mistake: the flag is on but there is nothing to talk to. A job
/// must still run - it simply runs without the refinement pass.
/// </summary>
public class WhenTheFlagIsOnButNothingIsReachable
{
    private readonly ISmartCutAdvisor _advisor;

    public WhenTheFlagIsOnButNothingIsReachable()
    {
        _advisor = SmartCutContainer.Resolve(services =>
            services.Configure<SmartCutOptions>(options =>
            {
                options.Enabled = true;
                options.Mode = SmartCutMode.Google;
            })
        );

        _advisor.ShouldNotBeNull();
    }

    [Fact]
    public void DegradesToTheNoOpRatherThanThrowingAtStartup() =>
        _advisor.ShouldBeOfType<NoOpSmartCutAdvisor>();

    [Fact]
    public void ReportsItselfDisabled() => _advisor.IsEnabled.ShouldBeFalse();

    [Fact]
    public void DegradesTheSameWayForAnUnusableLocalUrl() =>
        SmartCutContainer
            .ResolveFrom(
                ("SmartCut:Enabled", "true"),
                ("SmartCut:Mode", "Local"),
                ("SmartCut:LocalUrl", "not a url at all")
            )
            .ShouldBeOfType<NoOpSmartCutAdvisor>();

    [Fact]
    public void DegradesTheSameWayWhenGoogleHasNoModel() =>
        SmartCutContainer
            .Resolve(services =>
            {
                services.Configure<SmartCutOptions>(options =>
                {
                    options.Enabled = true;
                    options.Mode = SmartCutMode.Google;
                    options.GoogleModel = "";
                });
                services.AddSingleton<GoogleApiKeySource>(_ => () => "a-key");
            })
            .ShouldBeOfType<NoOpSmartCutAdvisor>();
}

public class WhenTheSmartCutHttpClientIsBuilt
{
    private readonly HttpClient _client;

    public WhenTheSmartCutHttpClientIsBuilt()
    {
        _client = new ServiceCollection()
            .AddSmartCut()
            .BuildServiceProvider()
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(SmartCutServiceCollectionExtensions.HttpClientName);

        _client.ShouldNotBeNull();
    }

    [Fact]
    public void AllowsForAColdServerLoadingWeightsIntoVram() =>
        _client.Timeout.ShouldBe(OpenAiCompatibleTransport.RequestTimeout);

    [Fact]
    public void IsNamedSoTheLongTimeoutDoesNotLeakOntoOtherCallers() =>
        SmartCutServiceCollectionExtensions.HttpClientName.ShouldNotBeNullOrWhiteSpace();
}
