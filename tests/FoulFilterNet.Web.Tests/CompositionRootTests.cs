using System.Text.Json;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Media;
using FoulFilterNet.Pipeline;
using FoulFilterNet.SmartCut;
using FoulFilterNet.Sources;
using FoulFilterNet.Watch;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
    public void CutsTheBuiltInPriorityWordsWiderInJobs() =>
        (
            (MediaPipeline)_services.GetRequiredService<IMediaPipeline>()
        ).CutPadding.PriorityWords.ShouldBe(Domain.PriorityWordList.Default);

    [Fact]
    public void GivesJobsTheOneRegisteredCutPadding() =>
        ((MediaPipeline)_services.GetRequiredService<IMediaPipeline>()).CutPadding.ShouldBeSameAs(
            _services.GetRequiredService<Domain.CutPadding>()
        );

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

/// <summary>
/// W10: Watch Sessions are composed from the same storage and the same engine
/// as Jobs, so a video watched in the browser and a file uploaded share one
/// Transcript cache, one Bad Words List and one GPU lane.
/// </summary>
public sealed class WhenTheHostComposesWatchSessions : IDisposable
{
    private readonly RealPipelineApplication _app = new();
    private readonly IServiceProvider _services;
    private readonly StorageOptions _storage;
    private readonly WatchOptions _watch;

    public WhenTheHostComposesWatchSessions()
    {
        _services = _app.Services;
        _storage = _services.GetRequiredService<IOptions<StorageOptions>>().Value;
        _watch = _services.GetRequiredService<WatchSessionManager>().Options;
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public void ResolvesTheManager() =>
        _services.GetRequiredService<WatchSessionManager>().ShouldNotBeNull();

    [Fact]
    public void FetchesWithYtDlp() =>
        _services.GetRequiredService<IWebAudioSource>().ShouldBeOfType<YtDlpAudioSource>();

    [Fact]
    public void RunsProcessesForReal() =>
        _services.GetRequiredService<IProcessRunner>().ShouldBeOfType<ProcessRunner>();

    [Fact]
    public void SharesTheOneEngine() =>
        _services
            .GetRequiredService<Transcription.IWhisperEngine>()
            .ShouldBeSameAs(_services.GetRequiredService<Transcription.IWhisperEngine>());

    [Fact]
    public void SavesTranscriptsWhereJobsDo() =>
        _watch.TranscriptDirectory.ShouldBe(_storage.ResolvedTranscriptDirectory);

    [Fact]
    public void ReadsTheBadWordsListJobsRead() =>
        _watch.BadWordsPath.ShouldBe(_storage.BadWordsPath);

    /// <summary>Under scratch, so startup housekeeping clears a crashed session's download too.</summary>
    [Fact]
    public void WorksInAWatchFolderOfScratch() =>
        _watch.ScratchDirectory.ShouldBe(Path.Combine(_storage.ScratchDirectory, "watch"));

    [Fact]
    public void KeepsTheDefaultIdleTimeout() =>
        _watch.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(2));

    [Fact]
    public void SweepsExpiredSessions() =>
        _services
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<WatchSessionSweeper>()
            .ShouldHaveSingleItem();

    [Fact]
    public void ProbesAvailabilityOnce() =>
        _services
            .GetRequiredService<WebVideoAvailabilityCache>()
            .ShouldBeSameAs(_services.GetRequiredService<WebVideoAvailabilityCache>());

    [Fact]
    public void FindsYtDlpOnPathByDefault() =>
        _services.GetRequiredService<IOptions<SourcesOptions>>().Value.YtDlpPath.ShouldBe("yt-dlp");

    [Fact]
    public void LeavesDenoToYtDlpsOwnSearchByDefault() =>
        _services
            .GetRequiredService<IOptions<SourcesOptions>>()
            .Value.DenoPath.ShouldBeNullOrEmpty();

    /// <summary>
    /// Only localhost's own names, so a DNS-rebinding page cannot reach the API
    /// through a hostname it controls.
    /// </summary>
    [Fact]
    public void AllowsOnlyLocalHosts() =>
        _services
            .GetRequiredService<IConfiguration>()["AllowedHosts"]
            .ShouldBe("localhost;127.0.0.1;[::1]");
}

/// <summary>The Sources and Watch sections reach the options they name.</summary>
public sealed class WhenWatchAndSourcesAreConfigured : IDisposable
{
    private readonly RealPipelineApplication _app = new();
    private readonly IServiceProvider _services;

    public WhenWatchAndSourcesAreConfigured()
    {
        _services = _app.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Sources:YtDlpPath", @"C:\tools\yt-dlp.exe");
            builder.UseSetting("Sources:DenoPath", @"C:\tools\deno.exe");
            builder.UseSetting("Watch:IdleTimeout", "00:05:00");
            builder.UseSetting("Watch:ScratchDirectory", @"C:\somewhere\else");
        }).Services;
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public void UsesTheConfiguredYtDlp() =>
        _services
            .GetRequiredService<IOptions<SourcesOptions>>()
            .Value.YtDlpPath.ShouldBe(@"C:\tools\yt-dlp.exe");

    [Fact]
    public void UsesTheConfiguredDeno() =>
        _services
            .GetRequiredService<IOptions<SourcesOptions>>()
            .Value.DenoPath.ShouldBe(@"C:\tools\deno.exe");

    [Fact]
    public void UsesTheConfiguredIdleTimeout() =>
        _services
            .GetRequiredService<WatchSessionManager>()
            .Options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(5));

    /// <summary>
    /// The paths follow <c>Storage</c>, like a Job's, whatever the Watch section
    /// says: one place to move the data, not two that can disagree.
    /// </summary>
    [Fact]
    public void KeepsScratchUnderStorageRegardless() =>
        _services
            .GetRequiredService<WatchSessionManager>()
            .Options.ScratchDirectory.ShouldBe(
                Path.Combine(
                    _services.GetRequiredService<IOptions<StorageOptions>>().Value.ScratchDirectory,
                    "watch"
                )
            );
}
