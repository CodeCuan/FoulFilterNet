using System.Text.Json;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Pipeline;
using FoulFilterNet.SmartCut;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// The real host with nothing substituted, so the orchestrator and every engine
/// it composes have to actually resolve.
/// </summary>
internal sealed class RealPipelineApplication : WebApplicationFactory<Program>
{
    private readonly TempDirectory _data = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:DataDirectory", _data.Path);
        builder.UseSetting("Storage:BadWordsPath", Path.Combine(_data.Path, "bad_words.txt"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _data.Dispose();
        }
    }
}

/// <summary>
/// Until T21 the host bound <c>IMediaPipeline</c> to a placeholder that threw,
/// and nothing called <c>AddSmartCut</c>, so no advisor was resolvable at all.
/// A host that cannot build the orchestrator fails at startup, which is
/// exactly the failure a unit test of the pipeline cannot catch.
/// </summary>
public sealed class WhenTheHostComposesTheOrchestrator : IDisposable
{
    private readonly RealPipelineApplication _app = new();
    private readonly IServiceProvider _services;

    public WhenTheHostComposesTheOrchestrator()
    {
        _services = _app.Services;

        _services.ShouldNotBeNull();
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public void ResolvesTheRealPipelineRatherThanAPlaceholder() =>
        _services.GetRequiredService<IMediaPipeline>().ShouldBeOfType<MediaPipeline>();

    [Fact]
    public void ResolvesTheAdvisorThePipelineAsksAboutEveryHit() =>
        _services.GetRequiredService<ISmartCutAdvisor>().ShouldNotBeNull();

    [Fact]
    public void LeavesSmartCutOffUntilSomeoneEnablesIt() =>
        _services.GetRequiredService<ISmartCutAdvisor>().IsEnabled.ShouldBeFalse();

    [Fact]
    public void ResolvesTheProber() =>
        _services.GetRequiredService<IMediaProber>().ShouldBeOfType<Media.FFprobeMediaProber>();

    [Fact]
    public void ResolvesTheAudioPreparer() =>
        _services.GetRequiredService<IAudioPreparer>().ShouldBeOfType<Media.FFmpegAudioPreparer>();

    [Fact]
    public void ResolvesTheEditor() =>
        _services.GetRequiredService<IMediaEditor>().ShouldBeOfType<Media.MediaEditor>();

    [Fact]
    public void ResolvesTheAlignerSeam() =>
        _services.GetRequiredService<IAligner>().ShouldBeOfType<Transcription.PassThroughAligner>();

    [Fact]
    public void ResolvesATranscriberSoTheHostStartsWithoutOne() =>
        _services.GetRequiredService<ITranscriber>().ShouldNotBeNull();

    /// <summary>
    /// The pipeline releases the transcriber after every job without asking
    /// whether the flag is on, so the flag has to be honoured by whatever the
    /// host hands it - see <c>ReleasePolicyTranscriber</c>.
    /// </summary>
    [Fact]
    public void WrapsTheTranscriberInTheModelReleasePolicy() =>
        _services
            .GetRequiredService<ITranscriber>()
            .ShouldBeOfType<Transcription.ReleasePolicyTranscriber>();

    /// <summary>
    /// T13 put the real engine inside that policy in place of the placeholder
    /// that threw. Resolving it here also proves the engine is inert until it is
    /// used: this host has no GPU test, no weights and no native library loaded.
    /// </summary>
    [Fact]
    public void ResolvesTheWhisperEngineTheTranscriberDrives() =>
        _services
            .GetRequiredService<Transcription.IWhisperEngine>()
            .ShouldBeOfType<Transcription.WhisperNetEngine>();

    [Fact]
    public void ResolvesTheTranscriberThatConvertsAudioForTheEngine() =>
        _services.GetRequiredService<Transcription.WhisperTranscriber>().ShouldNotBeNull();

    /// <summary>
    /// A legacy <c>.env</c> still configures the service: <c>DATA_DIR</c>,
    /// <c>WHISPER_MODEL</c> and the rest reach configuration through the shared
    /// translator, not through a copy of it.
    /// </summary>
    [Fact]
    public void ReadsTheLegacyEnvironmentVariables() =>
        ((IConfigurationRoot)_services.GetRequiredService<IConfiguration>())
            .Providers.OfType<LegacyEnvironmentConfigurationProvider>()
            .ShouldHaveSingleItem();

    [Fact]
    public void BuildsATranscriptStoreOverWhicheverDirectoryAJobNames() =>
        _services
            .GetRequiredService<TranscriptStoreFactory>()(Path.GetTempPath())
            .ShouldBeOfType<TranscriptStore>();
}

/// <summary>
/// Finding 1, the case that actually diverges: the flag is on but there is no
/// API key, so <c>AddSmartCut</c> resolved the no-op advisor and no hit will
/// ever be refined. Reporting the raw flag would light the badge anyway.
/// </summary>
public sealed class WhenSmartCutIsEnabledWithoutAnApiKey : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly JsonElement _config;

    public WhenSmartCutIsEnabledWithoutAnApiKey()
    {
        _client = _app.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("SmartCut:Enabled", "true");
                builder.UseSetting("SmartCut:Mode", "Google");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<GoogleApiKeySource>();
                    services.AddSingleton<GoogleApiKeySource>(new GoogleApiKeySource(() => null));
                });
            })
            .CreateClient();

        _config = Api.GetAsync(_client, "/config").GetAwaiter().GetResult();

        _config.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public void TellsTheUiTheBadgeMustNotAppear() =>
        _config.GetProperty("ai_enhance").GetBoolean().ShouldBeFalse();
}
