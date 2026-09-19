using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// A point a stub stops at until the test lets it through. Cancelling the
/// stub's token while it waits throws, as real work would.
/// </summary>
internal sealed class Gate
{
    private readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _released = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task Entered => _entered.Task;

    public void Release() => _released.TrySetResult();

    public void WaitUntilEntered() =>
        Entered.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// yt-dlp, faked: writes a placeholder <c>audio.webm</c> and reports a title,
/// or fails the way <see cref="Failure"/> says. Never touches the network.
/// </summary>
internal sealed class StubWebAudioSource : IWebAudioSource
{
    private int _fetches;
    private int _probes;

    public string Title { get; set; } = "Me at the zoo";

    public double? DurationSeconds { get; set; } = 19.0;

    public Exception? Failure { get; set; }

    public Gate? Hold { get; set; }

    public WebVideoAvailability Availability { get; set; } = new("2026.09.01", "2.5.0");

    public int Fetches => Volatile.Read(ref _fetches);

    public int Probes => Volatile.Read(ref _probes);

    public async Task<WebAudio> FetchAudioAsync(
        VideoRef video,
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _fetches);

        if (Hold is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "audio.webm");
        await File.WriteAllTextAsync(path, "not really audio", CancellationToken.None);
        return new WebAudio(video, Title, DurationSeconds, path, "webm", "251", "opus", false);
    }

    public Task<WebVideoAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _probes);
        return Task.FromResult(Availability);
    }

    public static WebAudioException Failing(VideoRef video, WebAudioFailure kind, string reason) =>
        new(video, kind, reason, $"yt-dlp failed: {reason}", exitCode: 1);
}

/// <summary>FFmpeg, faked: every conversion is a placeholder WAV next to its input.</summary>
internal sealed class StubAudioPreparer : IAudioPreparer
{
    public async Task<string> PadStartAsync(
        string audioPath,
        double offsetSeconds,
        CancellationToken cancellationToken = default
    )
    {
        var wav = Path.Combine(
            Path.GetDirectoryName(audioPath)!,
            $"ffn_pad_{Guid.NewGuid():N}.wav"
        );
        await File.WriteAllTextAsync(wav, "not really a wav", CancellationToken.None);
        return wav;
    }

    public Task ExtractAudioTrackAsync(
        string videoPath,
        string outputPath,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("The Watch endpoints never extract a track.");

    public Task<string> CropAsync(
        string audioPath,
        double offsetSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("The Watch endpoints never crop.");
}

/// <summary>
/// The GPU, faked: audio of <see cref="DurationSeconds"/> whose every window
/// hears what <see cref="Heard"/> says, after any hold set for it.
/// </summary>
internal sealed class StubWhisperEngine : IWhisperEngine
{
    private readonly Dictionary<int, Gate> _holds = [];

    /// <summary>One window's worth by default, so a session completes in one step.</summary>
    public double DurationSeconds { get; set; } = 20.0;

    /// <summary>Window index → what it heard, on the window's own timeline.</summary>
    public Func<int, TranscriptionResult> Heard { get; set; } = _ => Swearing;

    public Exception? OpenFailure { get; set; }

    /// <summary>"hello damn world": one Hit, on "damn" at 5.0–5.4 s of the first window.</summary>
    public static TranscriptionResult Swearing { get; } =
        new(
            [new Segment(4.5, 6.0, "hello damn world")],
            [new Word("hello", 4.5, 4.9), new Word("damn", 5.0, 5.4), new Word("world", 5.5, 6.0)]
        );

    public static TranscriptionResult Nothing { get; } = new([], []);

    public Gate Hold(int window)
    {
        lock (_holds)
        {
            if (!_holds.TryGetValue(window, out var gate))
            {
                _holds[window] = gate = new Gate();
            }

            return gate;
        }
    }

    public Task<IAnalysisAudio> OpenAsync(
        string wavPath,
        InferencePriority priority = InferencePriority.Normal,
        CancellationToken cancellationToken = default
    ) =>
        OpenFailure is { } failure
            ? Task.FromException<IAnalysisAudio>(failure)
            : Task.FromResult<IAnalysisAudio>(new Audio(this));

    public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    private Gate? HoldFor(int window)
    {
        lock (_holds)
        {
            return _holds.GetValueOrDefault(window);
        }
    }

    private sealed class Audio(StubWhisperEngine engine) : IAnalysisAudio
    {
        public double DurationSeconds { get; } = engine.DurationSeconds;

        public IReadOnlyList<TranscriptionWindow> Windows { get; } =
            TranscriptionWindows.Plan(engine.DurationSeconds);

        public async Task<TranscriptionResult> TranscribeWindowAsync(
            int index,
            CancellationToken cancellationToken = default
        )
        {
            if (engine.HoldFor(index) is { } gate)
            {
                await gate.PassAsync(cancellationToken);
            }

            return engine.Heard(index);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Talking to the Watch endpoints.</summary>
internal static class WatchApi
{
    public const string Video = "jNQXAC9IVRw";

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string? provider = "youtube",
        string? videoId = Video,
        double position = 0.0,
        string query = ""
    ) =>
        PostJsonAsync(
            client,
            JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["provider"] = provider,
                    ["video_id"] = videoId,
                    ["position"] = position,
                }
            ),
            query
        );

    public static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string json,
        string query = ""
    )
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/watch" + query, UriKind.Relative), content, Token);
    }

    public static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        string body,
        string? contentType
    )
    {
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = contentType is null
            ? null
            : MediaTypeHeaderValue.Parse(contentType);
        return await client.PostAsync(new Uri("/watch", UriKind.Relative), content, Token);
    }

    public static async Task<JsonElement> StartAsync(HttpClient client, string videoId = Video)
    {
        using var response = await PostAsync(client, videoId: videoId);
        response.EnsureSuccessStatusCode();
        return await Api.ReadAsync(response);
    }

    public static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string videoId = Video,
        string provider = "youtube",
        string query = ""
    ) => client.GetAsync(new Uri($"/watch/{provider}/{videoId}{query}", UriKind.Relative), Token);

    public static Task<HttpResponseMessage> DeleteAsync(
        HttpClient client,
        string videoId = Video,
        string provider = "youtube"
    ) => client.DeleteAsync(new Uri($"/watch/{provider}/{videoId}", UriKind.Relative), Token);

    /// <summary>Polls the snapshot until its state is <paramref name="state"/>.</summary>
    public static async Task<JsonElement> WaitForStateAsync(
        HttpClient client,
        string state,
        string videoId = Video,
        string provider = "youtube"
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await GetAsync(client, videoId, provider);
            if (response.IsSuccessStatusCode)
            {
                var view = await Api.ReadAsync(response);
                if (view.GetProperty("state").GetString() == state)
                {
                    return view;
                }
            }

            await Task.Delay(10, Token);
        }

        throw new TimeoutException($"The session for {videoId} never reached '{state}'.");
    }

    public static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(p => p.Name)];
}
