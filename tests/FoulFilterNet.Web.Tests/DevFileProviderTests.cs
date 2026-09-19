using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FoulFilterNet.Web.Tests;

// W16: the development-only `file` provider and the harness it serves. Off by
// default and impossible to reach while off; on, it runs the whole server path
// on files in a directory, and serves the extension's modules for the harness.

/// <summary>
/// The real host with the dev file provider switched on over a throwaway
/// media directory and a throwaway copy of the extension's layout. yt-dlp,
/// FFmpeg and the GPU are stubbed as in <see cref="FoulFilterApplication"/>.
/// </summary>
internal sealed class DevFileApplication : WebApplicationFactory<Program>
{
    private readonly TempDirectory _data = new();
    private readonly TempDirectory _media = new();
    private readonly TempDirectory _extension = new();
    private readonly string? _directory;
    private readonly string? _extensionDirectory;

    /// <param name="directory">Overrides the media directory (null: the throwaway one).</param>
    /// <param name="extensionDirectory">Overrides the extension directory (null: the throwaway one).</param>
    public DevFileApplication(string? directory = null, string? extensionDirectory = null)
    {
        _directory = directory;
        _extensionDirectory = extensionDirectory;

        File.WriteAllBytes(Path.Combine(_media.Path, "clip.mp4"), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        File.WriteAllText(Path.Combine(_media.Path, "manifest.json"), """{"fixtures":[]}""");
        File.WriteAllText(Path.Combine(_media.Path, ".hidden.mp4"), "hidden");

        var harness = Directory.CreateDirectory(Path.Combine(_extension.Path, "harness")).FullName;
        File.WriteAllText(
            Path.Combine(harness, "index.html"),
            "<!doctype html><title>Harness</title>"
        );
        File.WriteAllText(Path.Combine(harness, "harness.js"), "export const harness = 1;");
        File.WriteAllText(Path.Combine(harness, "harness.css"), "body { color: red; }");
        File.WriteAllText(Path.Combine(harness, "notes.md"), "# notes");
        var src = Directory.CreateDirectory(Path.Combine(_extension.Path, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "content-app.js"), "export const app = 1;");
        File.WriteAllText(Path.Combine(src, "readme.txt"), "not a module");
        File.WriteAllText(Path.Combine(_extension.Path, "manifest.json"), "{}");
    }

    public StubWebAudioSource YouTube { get; } = new();

    public StubWhisperEngine Engine { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public string MediaDirectory => _media.Path;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var badWords = Path.Combine(_data.Path, "bad_words.txt");
        File.WriteAllText(badWords, "damn" + Environment.NewLine);
        builder.UseSetting("Storage:DataDirectory", _data.Path);
        builder.UseSetting("Storage:BadWordsPath", badWords);
        builder.UseSetting("Watch:DevFileProvider:Enabled", "true");
        builder.UseSetting("Watch:DevFileProvider:Directory", _directory ?? _media.Path);
        builder.UseSetting(
            "Watch:DevFileProvider:ExtensionDirectory",
            _extensionDirectory ?? _extension.Path
        );

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureServices(services =>
        {
            // Only the real source under the dev provider is replaced, so the
            // provider itself is the host's own composition.
            services.RemoveAllKeyed<IWebAudioSource>(DevFileProvider.RealSourceKey);
            services.AddKeyedSingleton<IWebAudioSource>(DevFileProvider.RealSourceKey, YouTube);
            services.RemoveAll<IAudioPreparer>();
            services.AddSingleton<IAudioPreparer>(new StubAudioPreparer());
            services.RemoveAll<IWhisperEngine>();
            services.AddSingleton<IWhisperEngine>(Engine);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _data.Dispose();
            _media.Dispose();
            _extension.Dispose();
        }
    }
}

/// <summary>Keeps every log line, for asserting on the startup warning.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(LogLevel Level, string Category, string Message)> Entries { get; } =
        new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => owner.Entries.Enqueue((logLevel, category, formatter(state, exception)));
    }
}

/// <summary>A host with the dev file provider on, and a client that does not follow redirects.</summary>
public abstract class DevFileScenario : IDisposable
{
    private protected DevFileScenario()
    {
        App = new DevFileApplication();
        Client = App.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }
        );
    }

    private protected DevFileApplication App { get; }

    private protected HttpClient Client { get; }

    private protected static CancellationToken Token => TestContext.Current.CancellationToken;

    private protected HttpResponseMessage Get(string path) =>
        Client.GetAsync(new Uri(path, UriKind.Relative), Token).GetAwaiter().GetResult();

    private protected static string Body(HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(Token).GetAwaiter().GetResult();

    public void Dispose()
    {
        Client.Dispose();
        App.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Off, which is the default: nothing of it can be reached.</summary>
public sealed class WhenTheDevFileProviderIsOff : WatchScenario
{
    private HttpResponseMessage Get(string path) =>
        Client.GetAsync(new Uri(path, UriKind.Relative), WatchApi.Token).GetAwaiter().GetResult();

    [Fact]
    public async Task RefusesAFileHeartbeat() =>
        (
            await WatchApi.PostAsync(Client, provider: "file", videoId: "sample_video.mp4")
        ).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task RefusesAFileRead() =>
        (await WatchApi.GetAsync(Client, "sample_video.mp4", provider: "file")).StatusCode.ShouldBe(
            HttpStatusCode.BadRequest
        );

    [Fact]
    public async Task RefusesAFileCancel() =>
        (
            await WatchApi.DeleteAsync(Client, "sample_video.mp4", provider: "file")
        ).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

    [Fact]
    public async Task StartsNoSessionForAFile()
    {
        _ = await WatchApi.PostAsync(Client, provider: "file", videoId: "sample_video.mp4");
        App.WebAudio.Fetches.ShouldBe(0);
    }

    [Theory]
    [InlineData("/dev/harness")]
    [InlineData("/dev/harness/")]
    [InlineData("/dev/harness/harness.js")]
    [InlineData("/dev/src/content-app.js")]
    [InlineData("/dev/media")]
    [InlineData("/dev/media/sample_video.mp4")]
    public void ServesNothingUnderDev(string path) =>
        Get(path).StatusCode.ShouldBe(HttpStatusCode.NotFound);

    [Fact]
    public void ReportsItOff() =>
        App.Services.GetRequiredService<DevFileProviderStatus>().Enabled.ShouldBeFalse();
}

/// <summary>The shipped configuration keeps it off.</summary>
public sealed class WhenTheShippedConfigurationIsRead : IDisposable
{
    private readonly RealPipelineApplication _app = new();
    private readonly IServiceProvider _services;

    public WhenTheShippedConfigurationIsRead() => _services = _app.Services;

    public void Dispose() => _app.Dispose();

    [Fact]
    public void LeavesTheDevFileProviderOff() =>
        bool.Parse(
                _services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()[
                    "Watch:DevFileProvider:Enabled"
                ]!
            )
            .ShouldBeFalse();

    [Fact]
    public void ComposesNoDevFileSource() =>
        _services.GetRequiredService<IWebAudioSource>().ShouldBeOfType<YtDlpAudioSource>();

    [Fact]
    public void ReportsItOff() =>
        _services.GetRequiredService<DevFileProviderStatus>().Enabled.ShouldBeFalse();
}

/// <summary>A file in the directory, watched end to end through the real session.</summary>
public sealed class WhenAFileIsWatched : DevFileScenario
{
    private readonly JsonElement _first;
    private readonly JsonElement _view;

    public WhenAFileIsWatched()
    {
        using var response = WatchApi
            .PostAsync(Client, provider: "file", videoId: "clip.mp4")
            .GetAwaiter()
            .GetResult();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _first = Api.ReadAsync(response).GetAwaiter().GetResult();
        _view = WatchApi
            .WaitForStateAsync(Client, "complete", "clip.mp4", provider: "file")
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void NamesTheFileProvider() =>
        _first.GetProperty("provider").GetString().ShouldBe("file");

    [Fact]
    public void NamesTheFile() => _first.GetProperty("video_id").GetString().ShouldBe("clip.mp4");

    [Fact]
    public void IsKeyedByProviderAndFile() =>
        _view.GetProperty("key").GetString().ShouldBe("file-clip.mp4");

    [Fact]
    public void IsTitledWithTheFileName() =>
        _view.GetProperty("title").GetString().ShouldBe("clip.mp4");

    [Fact]
    public void FindsTheHit() =>
        _view.GetProperty("hits")[0].GetProperty("phrase").GetString().ShouldBe("damn");

    [Fact]
    public void NeverAsksYtDlp() => App.YouTube.Fetches.ShouldBe(0);

    [Fact]
    public void LeavesTheFileInPlace() =>
        File.Exists(Path.Combine(App.MediaDirectory, "clip.mp4")).ShouldBeTrue();
}

public sealed class WhenAFileThatIsNotThereIsWatched : DevFileScenario
{
    private readonly JsonElement _view;

    public WhenAFileThatIsNotThereIsWatched()
    {
        using var response = WatchApi
            .PostAsync(Client, provider: "file", videoId: "missing.mp4")
            .GetAwaiter()
            .GetResult();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _view = WatchApi
            .WaitForStateAsync(Client, "failed", "missing.mp4", provider: "file")
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void FailsAsUnavailable() =>
        _view.GetProperty("failure_kind").GetString().ShouldBe("unavailable");

    [Fact]
    public void SaysWhichFile() =>
        _view.GetProperty("reason").GetString()!.ShouldContain("missing.mp4");

    [Fact]
    public void DoesNotRevealTheDirectory() =>
        _view.GetProperty("reason").GetString()!.ShouldNotContain(App.MediaDirectory);
}

/// <summary>A hidden file is in the directory but not in its listing, so it cannot be watched.</summary>
public sealed class WhenAHiddenFileIsWatched : DevFileScenario
{
    [Fact]
    public async Task IsRefusedBeforeASessionStarts() =>
        (
            await WatchApi.PostAsync(Client, provider: "file", videoId: ".hidden.mp4")
        ).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
}

public sealed class WhenAnUnsafeFileNameIsSent : DevFileScenario
{
    [Theory]
    [InlineData("../clip.mp4")]
    [InlineData("..\\clip.mp4")]
    [InlineData("-o")]
    [InlineData("--exec=calc")]
    [InlineData("a/clip.mp4")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("")]
    [InlineData("clip.mp4 ")]
    public async Task IsRefused(string name) =>
        (await WatchApi.PostAsync(Client, provider: "file", videoId: name)).StatusCode.ShouldBe(
            HttpStatusCode.BadRequest
        );

    [Fact]
    public async Task StartsNothing()
    {
        _ = await WatchApi.PostAsync(Client, provider: "file", videoId: "../clip.mp4");
        App.Services.GetRequiredService<Watch.WatchSessionManager>().Count.ShouldBe(0);
    }
}

public sealed class WhenYouTubeIsWatchedWithTheDevFileProviderOn : DevFileScenario
{
    private readonly JsonElement _view;

    public WhenYouTubeIsWatchedWithTheDevFileProviderOn()
    {
        _ = WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        _view = WatchApi.WaitForStateAsync(Client, "complete").GetAwaiter().GetResult();
    }

    [Fact]
    public void StillFetchesWithTheRealSource() => App.YouTube.Fetches.ShouldBe(1);

    [Fact]
    public void IsStillKeyedAsYouTube() =>
        _view.GetProperty("key").GetString().ShouldBe("youtube-" + WatchApi.Video);
}

public sealed class WhenTheDevFileProviderStarts : DevFileScenario
{
    private readonly (LogLevel Level, string Category, string Message)[] _warnings;

    public WhenTheDevFileProviderStarts()
    {
        _ = Get("/health");
        _warnings =
        [
            .. App.Logs.Entries.Where(e =>
                e.Message.Contains("DEV FILE PROVIDER", StringComparison.Ordinal)
            ),
        ];
    }

    [Fact]
    public void WarnsOnce() => _warnings.ShouldHaveSingleItem();

    [Fact]
    public void WarnsAtWarningLevel() => _warnings[0].Level.ShouldBe(LogLevel.Warning);

    [Fact]
    public void NamesTheDirectory() => _warnings[0].Message.ShouldContain(App.MediaDirectory);

    [Fact]
    public void NamesTheHarness() => _warnings[0].Message.ShouldContain("/dev/harness/");

    [Fact]
    public void NamesTheSwitch() =>
        _warnings[0].Message.ShouldContain("Watch:DevFileProvider:Enabled");

    [Fact]
    public void ReportsItOn() =>
        App.Services.GetRequiredService<DevFileProviderStatus>().Enabled.ShouldBeTrue();

    [Fact]
    public void ComposesTheDevFileSource() =>
        App.Services.GetRequiredService<IWebAudioSource>().ShouldBeOfType<DevFileAudioSource>();
}

public sealed class WhenTheHarnessIsRequested : DevFileScenario
{
    [Fact]
    public void RedirectsTheBarePathToTheFolder()
    {
        using var response = Get("/dev/harness");
        response.Headers.Location!.OriginalString.ShouldBe("/dev/harness/");
    }

    [Fact]
    public void ServesTheIndex() =>
        Body(Get("/dev/harness/")).ShouldContain("<title>Harness</title>");

    [Fact]
    public void ServesTheIndexAsHtml() =>
        Get("/dev/harness/").Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

    [Fact]
    public void ServesTheIndexByName() =>
        Get("/dev/harness/index.html").StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public void ServesItsScriptAsJavaScript() =>
        Get("/dev/harness/harness.js")
            .Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");

    [Fact]
    public void ServesItsStyleSheetAsCss() =>
        Get("/dev/harness/harness.css").Content.Headers.ContentType!.MediaType.ShouldBe("text/css");

    [Fact]
    public void IsNeverCached() =>
        Get("/dev/harness/harness.js").Headers.CacheControl!.NoStore.ShouldBeTrue();

    [Fact]
    public void ServesTheExtensionsModules() =>
        Body(Get("/dev/src/content-app.js")).ShouldBe("export const app = 1;");

    [Fact]
    public void ServesTheModulesAsJavaScript() =>
        Get("/dev/src/content-app.js")
            .Content.Headers.ContentType!.MediaType.ShouldBe("text/javascript");

    [Theory]
    [InlineData("/dev/harness/notes.md")]
    [InlineData("/dev/harness/missing.js")]
    [InlineData("/dev/harness/..%2Fmanifest.json")]
    [InlineData("/dev/harness/..%5Cmanifest.json")]
    [InlineData("/dev/src/readme.txt")]
    [InlineData("/dev/src/..%2Fharness%2Fharness.js")]
    [InlineData("/dev/src/missing.js")]
    [InlineData("/dev/src/")]
    [InlineData("/dev/manifest.json")]
    public void ServesNothingElse(string path) =>
        Get(path).StatusCode.ShouldBe(HttpStatusCode.NotFound);
}

public sealed class WhenDevMediaIsRequested : DevFileScenario
{
    [Fact]
    public void ListsTheServableFiles() =>
        JsonDocument
            .Parse(Body(Get("/dev/media")))
            .RootElement.EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["clip.mp4", "manifest.json"]);

    [Fact]
    public async Task ServesAFile() =>
        (await Get("/dev/media/clip.mp4").Content.ReadAsByteArrayAsync(Token)).ShouldBe([
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            9,
        ]);

    [Fact]
    public void ServesVideoAsVideo() =>
        Get("/dev/media/clip.mp4").Content.Headers.ContentType!.MediaType.ShouldBe("video/mp4");

    [Fact]
    public void ServesTheManifestAsJson() =>
        Get("/dev/media/manifest.json")
            .Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

    [Fact]
    public void AcceptsRanges() =>
        Get("/dev/media/clip.mp4").Headers.AcceptRanges.ShouldContain("bytes");

    /// <summary>A &lt;video&gt; seeks with ranges.</summary>
    [Fact]
    public async Task AnswersARangeWithPartialContent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/dev/media/clip.mp4");
        request.Headers.Range = new RangeHeaderValue(2, 4);
        using var response = await Client.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
    }

    [Theory]
    [InlineData("/dev/media/missing.mp4")]
    [InlineData("/dev/media/.hidden.mp4")]
    [InlineData("/dev/media/CLIP.MP4")]
    [InlineData("/dev/media/..%2Fclip.mp4")]
    [InlineData("/dev/media/..%5C..%5Cwin.ini")]
    [InlineData("/dev/media/-o")]
    public void ServesNothingElse(string path) =>
        Get(path).StatusCode.ShouldBe(HttpStatusCode.NotFound);
}

public sealed class WhenTheDevFileProviderIsOnWithoutADirectory
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TheHostRefusesToStart(string directory)
    {
        using var app = new DevFileApplication(directory: directory);
        Should
            .Throw<Exception>(() => app.CreateClient())
            .ToString()
            .ShouldContain("Watch:DevFileProvider:Directory");
    }
}

public sealed class WhenTheDevFileDirectoryDoesNotExist
{
    [Fact]
    public void TheHostRefusesToStart()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "ffn-no-such-" + Guid.NewGuid().ToString("N")
        );
        using var app = new DevFileApplication(directory: missing);

        Should.Throw<Exception>(() => app.CreateClient()).ToString().ShouldContain(missing);
    }
}

/// <summary>The provider still works without the extension; only the harness is missing.</summary>
public sealed class WhenTheExtensionCannotBeFound : IDisposable
{
    private readonly DevFileApplication _app = new(
        extensionDirectory: Path.Combine(
            Path.GetTempPath(),
            "ffn-no-extension-" + Guid.NewGuid().ToString("N")
        )
    );
    private readonly HttpClient _client;

    public WhenTheExtensionCannotBeFound() => _client = _app.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task ServesNoHarness() =>
        (
            await _client.GetAsync(new Uri("/dev/harness/", UriKind.Relative), WatchApi.Token)
        ).StatusCode.ShouldBe(HttpStatusCode.NotFound);

    [Fact]
    public async Task StillServesMedia() =>
        (
            await _client.GetAsync(new Uri("/dev/media/clip.mp4", UriKind.Relative), WatchApi.Token)
        ).StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public async Task SaysSoInTheLog()
    {
        _ = await _client.GetAsync(new Uri("/health", UriKind.Relative), WatchApi.Token);
        _app.Logs.Entries.ShouldContain(e =>
            e.Message.Contains("extension", StringComparison.OrdinalIgnoreCase)
            && e.Level == LogLevel.Warning
            && e.Message.Contains("harness", StringComparison.Ordinal)
        );
    }
}

public sealed class WhenLookingForTheExtensionFolder : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly string _deep;

    public WhenLookingForTheExtensionFolder()
    {
        var extension = Directory.CreateDirectory(Path.Combine(_root.Path, "extension")).FullName;
        File.WriteAllText(Path.Combine(extension, "manifest.json"), "{}");
        Directory.CreateDirectory(Path.Combine(extension, "harness"));
        _deep = Directory.CreateDirectory(Path.Combine(_root.Path, "src", "Web", "bin")).FullName;
    }

    public void Dispose() => _root.Dispose();

    [Fact]
    public void FindsItAboveTheStartingFolder() =>
        DevFileProvider
            .FindExtensionDirectory(_deep)
            .ShouldBe(Path.Combine(_root.Path, "extension"));

    [Fact]
    public void FindsItFromTheRootItself() =>
        DevFileProvider
            .FindExtensionDirectory(_root.Path)
            .ShouldBe(Path.Combine(_root.Path, "extension"));

    [Fact]
    public void FindsNothingWhereThereIsNone()
    {
        using var empty = new TempDirectory();
        DevFileProvider.FindExtensionDirectory(empty.Path).ShouldBeNull();
    }

    [Fact]
    public void IgnoresAnExtensionFolderWithoutAHarness()
    {
        using var other = new TempDirectory();
        var extension = Directory.CreateDirectory(Path.Combine(other.Path, "extension")).FullName;
        File.WriteAllText(Path.Combine(extension, "manifest.json"), "{}");
        DevFileProvider.FindExtensionDirectory(other.Path).ShouldBeNull();
    }
}
