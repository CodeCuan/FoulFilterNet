using FoulFilterNet.Media;

namespace FoulFilterNet.Sources.Tests;

/// <summary>
/// The adapter against the real yt-dlp and the real YouTube: one short public
/// video ("Me at the zoo", 19 s, about 250 kB of Opus), fetched into a scratch
/// directory by the same single call production makes.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, as the GPU tests are: it needs yt-dlp, Deno and the network, and
/// YouTube changes under it. <c>RUN_WEB_TESTS=1 dotnet test</c> runs it.
/// <c>FOULFILTER_YTDLP</c> and <c>FOULFILTER_DENO</c> point at the executables
/// when they are not on <c>PATH</c> (winget installs yt-dlp without adding it
/// to the path of an already-running shell).
/// </para>
/// <para>
/// The fetch happens once for the class, in <see cref="Fetched"/>, rather than
/// once per fact in the constructor: a download per assertion would be slow and
/// rude to YouTube.
/// </para>
/// </remarks>
public sealed class LiveYtDlpAudioSourceTests
{
    private const string OptIn =
        "opt-in: set RUN_WEB_TESTS=1 with yt-dlp, Deno and a network connection";

    private static readonly VideoRef Video = VideoRef.Create("youtube", "jNQXAC9IVRw");

    private static readonly Lazy<Task<(WebAudio Audio, string Directory)>> Fetched = new(
        FetchAsync
    );

    /// <summary>Gate, read the same way the GPU tests read theirs.</summary>
    public static bool WebTestsEnabled =>
        (Environment.GetEnvironmentVariable("RUN_WEB_TESTS") ?? string.Empty).ToLowerInvariant()
            is "1"
                or "true"
                or "yes";

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task ReportsTheTitle() =>
        (await Fetched.Value).Audio.Title.ShouldBe("Me at the zoo");

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task ReportsTheDuration() =>
        (await Fetched.Value).Audio.DurationSeconds.ShouldBe(19);

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task DownloadsTheAudioIntoTheDirectory()
    {
        var (audio, directory) = await Fetched.Value;

        Path.GetDirectoryName(audio.AudioPath).ShouldBe(directory);
    }

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task DownloadsARealFile() =>
        new FileInfo((await Fetched.Value).Audio.AudioPath).Length.ShouldBeGreaterThan(50_000);

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task NamesItAfterTheFixedStem() =>
        Path.GetFileNameWithoutExtension((await Fetched.Value).Audio.AudioPath)
            .ShouldBe(YtDlpArguments.AudioFileStem);

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task LeavesNothingElseBehind() =>
        Directory.GetFiles((await Fetched.Value).Directory).Length.ShouldBe(1);

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task HadAJavaScriptRuntime() =>
        (await Fetched.Value).Audio.JsRuntimeMissing.ShouldBeFalse();

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task ReportsAnAudioCodec() =>
        (await Fetched.Value).Audio.Codec.ShouldNotBeNullOrWhiteSpace();

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task ReportsTheToolsAsAvailable() =>
        (
            await CreateSource().CheckAvailabilityAsync(TestContext.Current.CancellationToken)
        ).IsAvailable.ShouldBeTrue();

    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task ReportsANonexistentVideoAsUnavailable()
    {
        var directory = NewDirectory();

        var thrown = await Should.ThrowAsync<WebAudioException>(() =>
            CreateSource()
                .FetchAudioAsync(
                    VideoRef.Create("youtube", "aaaaaaaaaaa"),
                    directory,
                    TestContext.Current.CancellationToken
                )
        );

        thrown.Kind.ShouldBe(WebAudioFailure.Unavailable);
    }

    /// <summary>
    /// The real runner kills yt-dlp's process tree and the source deletes the
    /// partial file. The 60-minute W01 video (about 56 MB) is cancelled a few
    /// seconds in, while it is still extracting or downloading; either way
    /// nothing may be left.
    /// </summary>
    [Fact(Skip = OptIn, SkipUnless = nameof(WebTestsEnabled))]
    public async Task CancellingMidDownloadLeavesNothingBehind()
    {
        var directory = NewDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            CreateSource()
                .FetchAudioAsync(
                    VideoRef.Create("youtube", "2SXr48OYxbA"),
                    directory,
                    cancellation.Token
                )
        );

        Directory.GetFiles(directory).ShouldBeEmpty();
    }

    private static async Task<(WebAudio, string)> FetchAsync()
    {
        var directory = NewDirectory();
        return (await CreateSource().FetchAudioAsync(Video, directory), directory);
    }

    private static YtDlpAudioSource CreateSource() =>
        new(
            new ProcessRunner(),
            new SourcesOptions
            {
                YtDlpPath = Environment.GetEnvironmentVariable("FOULFILTER_YTDLP")
                    is { Length: > 0 } ytDlp
                    ? ytDlp
                    : "yt-dlp",
                DenoPath = Environment.GetEnvironmentVariable("FOULFILTER_DENO")
                    is { Length: > 0 } deno
                    ? deno
                    : null,
            }
        );

    /// <summary>Under the system temp directory; left for the OS to clean, like the GPU tests' WAVs.</summary>
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), $"ffn_live_ytdlp_{Guid.NewGuid():N}");
}
