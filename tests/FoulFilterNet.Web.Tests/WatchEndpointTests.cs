using System.Net;
using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Sources;

namespace FoulFilterNet.Web.Tests;

/// <summary>A host with stubbed yt-dlp, FFmpeg and GPU, and a client for it.</summary>
public abstract class WatchScenario : IDisposable
{
    private protected WatchScenario()
    {
        App = new FoulFilterApplication();
        Client = App.CreateClient();
    }

    private protected FoulFilterApplication App { get; }

    private protected HttpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        App.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The first heartbeat for a video, caught while yt-dlp is still fetching: the
/// endpoint answers at once with the session as it stands.
/// </summary>
public sealed class WhenAWatchIsStarted : WatchScenario
{
    private readonly HttpResponseMessage _response;
    private readonly JsonElement _view;

    public WhenAWatchIsStarted()
    {
        var fetch = App.WebAudio.Hold = new Gate();

        _response = WatchApi.PostAsync(Client, position: 12.3).GetAwaiter().GetResult();
        _view = Api.ReadAsync(_response).GetAwaiter().GetResult();
        fetch.WaitUntilEntered();

        _response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _view.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void AnswersWithJson() =>
        _response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

    /// <summary>
    /// The contract the extension (W11–W15) is written against: exactly these
    /// snake_case names, and nothing else - in particular no transcript, which
    /// would be most of the payload of a once-a-second poll.
    /// </summary>
    [Fact]
    public void HasExactlyTheContractsFields() =>
        WatchApi
            .PropertyNames(_view)
            .ShouldBe([
                "key",
                "provider",
                "video_id",
                "session",
                "state",
                "reason",
                "failure_kind",
                "title",
                "duration",
                "revision",
                "unchanged",
                "coverage",
                "hits",
                "windows_done",
                "windows_total",
                "progress",
                "realtime_factor",
                "keeping_up",
                "from_cache",
            ]);

    [Fact]
    public void NeverSendsTheTranscript() =>
        _view.TryGetProperty("transcript", out _).ShouldBeFalse();

    [Fact]
    public void IsKeyedByTheVideo() =>
        _view.GetProperty("key").GetString().ShouldBe("youtube-jNQXAC9IVRw");

    [Fact]
    public void NamesTheProvider() => _view.GetProperty("provider").GetString().ShouldBe("youtube");

    [Fact]
    public void NamesTheVideo() =>
        _view.GetProperty("video_id").GetString().ShouldBe(WatchApi.Video);

    [Fact]
    public void NamesTheSession() =>
        _view.GetProperty("session").GetString().ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void IsStillWorking() =>
        _view.GetProperty("state").GetString().ShouldBeOneOf("queued", "fetching");

    [Fact]
    public void HasNoReason() => _view.GetProperty("reason").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void HasNoFailureKind() =>
        _view.GetProperty("failure_kind").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void HasNoTitleYet() =>
        _view.GetProperty("title").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void HasNoDurationYet() =>
        _view.GetProperty("duration").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void IsAtRevisionZero() => _view.GetProperty("revision").GetInt64().ShouldBe(0);

    [Fact]
    public void IsNotAnUnchangedAnswer() =>
        _view.GetProperty("unchanged").GetBoolean().ShouldBeFalse();

    [Fact]
    public void CoversNothing() => _view.GetProperty("coverage").GetArrayLength().ShouldBe(0);

    [Fact]
    public void HasAnEmptyHitListRatherThanNull() =>
        _view.GetProperty("hits").ValueKind.ShouldBe(JsonValueKind.Array);

    [Fact]
    public void HasNoHits() => _view.GetProperty("hits").GetArrayLength().ShouldBe(0);

    [Fact]
    public void HasFinishedNoWindows() => _view.GetProperty("windows_done").GetInt32().ShouldBe(0);

    [Fact]
    public void HasPlannedNoWindows() => _view.GetProperty("windows_total").GetInt32().ShouldBe(0);

    [Fact]
    public void HasNoProgress() => _view.GetProperty("progress").GetDouble().ShouldBe(0.0);

    [Fact]
    public void HasNoMeasuredSpeed() =>
        _view.GetProperty("realtime_factor").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void IsKeepingUpUntilMeasuredOtherwise() =>
        _view.GetProperty("keeping_up").GetBoolean().ShouldBeTrue();

    [Fact]
    public void IsNotFromTheCache() => _view.GetProperty("from_cache").GetBoolean().ShouldBeFalse();

    [Fact]
    public void StartsTheFetch() => App.WebAudio.Fetches.ShouldBe(1);
}

/// <summary>A one-window video heard to the end, with one swear in it.</summary>
public sealed class WhenAWatchCompletes : WatchScenario
{
    private readonly JsonElement _view;

    public WhenAWatchCompletes()
    {
        WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        _view = WatchApi.WaitForStateAsync(Client, "complete").GetAwaiter().GetResult();

        _view.GetProperty("hits").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void IsComplete() => _view.GetProperty("state").GetString().ShouldBe("complete");

    [Fact]
    public void HasFinishedProgress() => _view.GetProperty("progress").GetDouble().ShouldBe(1.0);

    [Fact]
    public void HasFinishedEveryWindow() =>
        _view.GetProperty("windows_done").GetInt32().ShouldBe(1);

    [Fact]
    public void PlannedOneWindow() => _view.GetProperty("windows_total").GetInt32().ShouldBe(1);

    [Fact]
    public void HasTheTitleYtDlpReported() =>
        _view.GetProperty("title").GetString().ShouldBe("Me at the zoo");

    /// <summary>The analysis WAV's length replaces yt-dlp's whole-second figure.</summary>
    [Fact]
    public void HasTheAnalysedDuration() =>
        _view.GetProperty("duration").GetDouble().ShouldBe(20.0);

    [Fact]
    public void IsAtRevisionOne() => _view.GetProperty("revision").GetInt64().ShouldBe(1);

    [Fact]
    public void CoversTheWholeVideoAsOneInterval() =>
        _view.GetProperty("coverage").GetArrayLength().ShouldBe(1);

    [Fact]
    public void SendsCoverageAsFromToPairs() =>
        _view
            .GetProperty("coverage")[0]
            .EnumerateArray()
            .Select(e => e.GetDouble())
            .ShouldBe([0.0, 20.0]);

    [Fact]
    public void SendsEachHitAsStartEndAndPhrase() =>
        WatchApi.PropertyNames(_view.GetProperty("hits")[0]).ShouldBe(["start", "end", "phrase"]);

    [Fact]
    public void NamesThePhrase() =>
        _view.GetProperty("hits")[0].GetProperty("phrase").GetString().ShouldBe("damn");

    [Fact]
    public void StartsTheHitPaddedBeforeTheWord() =>
        _view.GetProperty("hits")[0].GetProperty("start").GetDouble().ShouldBe(4.85, 0.001);

    [Fact]
    public void EndsTheHitPaddedAfterTheWord() =>
        _view.GetProperty("hits")[0].GetProperty("end").GetDouble().ShouldBe(5.65, 0.001);

    [Fact]
    public void ReportsHowFastItWent() =>
        _view.GetProperty("realtime_factor").GetDouble().ShouldBeGreaterThan(0.0);

    [Fact]
    public void IsKeepingUp() => _view.GetProperty("keeping_up").GetBoolean().ShouldBeTrue();

    [Fact]
    public void WasTranscribedRatherThanCached() =>
        _view.GetProperty("from_cache").GetBoolean().ShouldBeFalse();

    [Fact]
    public void SavesTheTranscriptWhereJobsLookForIt() =>
        Directory
            .GetFiles(App.TranscriptDirectory)
            .ShouldContain(path =>
                Path.GetFileName(path).StartsWith("youtube-", StringComparison.Ordinal)
            );
}

/// <summary>A three-window video caught with only the first window heard.</summary>
public sealed class WhenAWatchIsPartWayThrough : WatchScenario
{
    private readonly JsonElement _view;

    public WhenAWatchIsPartWayThrough()
    {
        App.Engine.DurationSeconds = 60.0;
        App.Engine.Heard = window =>
            window == 0 ? StubWhisperEngine.Swearing : StubWhisperEngine.Nothing;
        var second = App.Engine.Hold(1);

        WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        second.WaitUntilEntered();

        using var response = WatchApi.GetAsync(Client).GetAwaiter().GetResult();
        _view = Api.ReadAsync(response).GetAwaiter().GetResult();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void IsTranscribing() => _view.GetProperty("state").GetString().ShouldBe("transcribing");

    [Fact]
    public void HasHeardOneWindow() => _view.GetProperty("windows_done").GetInt32().ShouldBe(1);

    [Fact]
    public void PlannedThreeWindows() => _view.GetProperty("windows_total").GetInt32().ShouldBe(3);

    [Fact]
    public void IsAThirdOfTheWayThrough() =>
        _view.GetProperty("progress").GetDouble().ShouldBe(1.0 / 3.0, 0.0001);

    [Fact]
    public void CoversFromTheStart() =>
        _view.GetProperty("coverage")[0][0].GetDouble().ShouldBe(0.0);

    /// <summary>
    /// The first window's share ends at 25 s; the guard holds back its unheard
    /// edge - 1.05 s rather than 1 s, because the host cuts the built-in
    /// priority words wider (0.8 s minimum + 0.25 s pre-roll).
    /// </summary>
    [Fact]
    public void StopsCoverageAGuardShortOfTheUnheardWindow() =>
        _view.GetProperty("coverage")[0][1].GetDouble().ShouldBe(23.95, 0.001);

    [Fact]
    public void AlreadyServesTheHitsHeardSoFar() =>
        _view.GetProperty("hits").GetArrayLength().ShouldBe(1);

    [Fact]
    public void KnowsTheDuration() => _view.GetProperty("duration").GetDouble().ShouldBe(60.0);
}

/// <summary>Two tabs on one video, or one tab beating once a second: one session, one download.</summary>
public sealed class WhenTheSameVideoIsPostedAgain : WatchScenario
{
    private readonly JsonElement _first;
    private readonly JsonElement _second;

    public WhenTheSameVideoIsPostedAgain()
    {
        var fetch = App.WebAudio.Hold = new Gate();

        _first = WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        fetch.WaitUntilEntered();
        _second = WatchApi.StartAsync(Client).GetAwaiter().GetResult();
    }

    [Fact]
    public void AnswersFromTheSameSession() =>
        _second
            .GetProperty("session")
            .GetString()
            .ShouldBe(_first.GetProperty("session").GetString());

    [Fact]
    public void FetchesTheAudioOnce() => App.WebAudio.Fetches.ShouldBe(1);
}

public sealed class WhenTwoVideosAreWatched : WatchScenario
{
    private readonly JsonElement _first;
    private readonly JsonElement _second;

    public WhenTwoVideosAreWatched()
    {
        App.WebAudio.Hold = new Gate();

        _first = WatchApi.StartAsync(Client, "jNQXAC9IVRw").GetAwaiter().GetResult();
        _second = WatchApi.StartAsync(Client, "dQw4w9WgXcQ").GetAwaiter().GetResult();
    }

    [Fact]
    public void GivesEachItsOwnSession() =>
        _second
            .GetProperty("session")
            .GetString()
            .ShouldNotBe(_first.GetProperty("session").GetString());

    [Fact]
    public void KeysEachByItsOwnVideo() =>
        _second.GetProperty("key").GetString().ShouldBe("youtube-dQw4w9WgXcQ");
}

/// <summary>GET and DELETE for a video nobody has POSTed.</summary>
public sealed class WhenNoSessionExists : WatchScenario
{
    [Fact]
    public async Task GetIsNotFound()
    {
        using var response = await WatchApi.GetAsync(Client);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetExplainsInTheFieldTheFrontEndReads()
    {
        using var response = await WatchApi.GetAsync(Client);
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>GET is a read: it must not start a download the extension did not ask for.</summary>
    [Fact]
    public async Task GetDoesNotStartASession()
    {
        using var first = await WatchApi.GetAsync(Client);
        using var second = await WatchApi.GetAsync(Client);

        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetDoesNotFetchAnything()
    {
        using var response = await WatchApi.GetAsync(Client);

        App.WebAudio.Fetches.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteIsNotFound()
    {
        using var response = await WatchApi.DeleteAsync(Client);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

public sealed class WhenAWatchIsDeleted : WatchScenario
{
    private readonly HttpResponseMessage _deleted;
    private readonly string _session;

    public WhenAWatchIsDeleted()
    {
        var fetch = App.WebAudio.Hold = new Gate();
        _session = WatchApi
            .StartAsync(Client)
            .GetAwaiter()
            .GetResult()
            .GetProperty("session")
            .GetString()!;
        fetch.WaitUntilEntered();

        _deleted = WatchApi.DeleteAsync(Client).GetAwaiter().GetResult();
    }

    [Fact]
    public void AnswersNoContent() => _deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

    [Fact]
    public async Task SendsNoBody() =>
        (await _deleted.Content.ReadAsStringAsync(WatchApi.Token)).ShouldBeEmpty();

    [Fact]
    public async Task IsGoneAfterwards()
    {
        using var response = await WatchApi.GetAsync(Client);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IsNotFoundASecondTime()
    {
        using var response = await WatchApi.DeleteAsync(Client);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ANewHeartbeatStartsAFreshSession()
    {
        var view = await WatchApi.StartAsync(Client);

        view.GetProperty("session").GetString().ShouldNotBe(_session);
    }
}

/// <summary>
/// Everything the extension sends is untrusted: only a validated
/// <see cref="VideoRef"/> gets anywhere near yt-dlp.
/// </summary>
public sealed class WhenAWatchNamesAVideoBadly : WatchScenario
{
    [Theory]
    [InlineData("")]
    [InlineData("jNQXAC9IVR")]
    [InlineData("jNQXAC9IVRwX")]
    [InlineData("jNQXAC9IVR!")]
    [InlineData("jNQXAC9 IVR")]
    [InlineData("jNQXAC9IVR=")]
    [InlineData("jNQXAC9IVR%")]
    [InlineData("jNQXAC9IVRé")]
    [InlineData("--exec=calc")]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    public async Task RefusesAnIdThatIsNotElevenIdCharacters(string id)
    {
        using var response = await WatchApi.PostAsync(Client, videoId: id);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExplainsWhyInTheFieldTheFrontEndReads()
    {
        using var response = await WatchApi.PostAsync(Client, videoId: "nope");
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("video_id");
    }

    [Fact]
    public async Task StartsNothingForABadId()
    {
        using var response = await WatchApi.PostAsync(Client, videoId: "--exec=calc");

        App.WebAudio.Fetches.ShouldBe(0);
    }

    [Fact]
    public async Task RefusesAMissingId()
    {
        using var response = await WatchApi.PostAsync(Client, videoId: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("vimeo")]
    [InlineData("")]
    [InlineData(" youtube")]
    [InlineData("youtube.com")]
    public async Task RefusesAProviderOtherThanYouTube(string provider)
    {
        using var response = await WatchApi.PostAsync(Client, provider: provider);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusesAMissingProvider()
    {
        using var response = await WatchApi.PostAsync(Client, provider: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NamesTheProviderWhenItIsWrong()
    {
        using var response = await WatchApi.PostAsync(Client, provider: "vimeo");
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("provider");
    }

    [Theory]
    [InlineData("vimeo", WatchApi.Video)]
    [InlineData("youtube", "jNQXAC9IVR")]
    [InlineData("youtube", "jNQXAC9IVR!")]
    public async Task GetRefusesTheSameThings(string provider, string id)
    {
        using var response = await WatchApi.GetAsync(Client, id, provider);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("vimeo", WatchApi.Video)]
    [InlineData("youtube", "jNQXAC9IVR")]
    public async Task DeleteRefusesTheSameThings(string provider, string id)
    {
        using var response = await WatchApi.DeleteAsync(Client, id, provider);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

/// <summary>
/// W03: a YouTube ID may start with <c>-</c>. It is safe because yt-dlp is only
/// ever given the watch URL built from it, never the bare ID.
/// </summary>
public sealed class WhenAVideoIdStartsWithADash : WatchScenario
{
    private readonly HttpResponseMessage _response;
    private readonly JsonElement _view;

    public WhenAVideoIdStartsWithADash()
    {
        App.WebAudio.Hold = new Gate();

        _response = WatchApi.PostAsync(Client, videoId: "-NQXAC9IVRw").GetAwaiter().GetResult();
        _view = Api.ReadAsync(_response).GetAwaiter().GetResult();
    }

    [Fact]
    public void AcceptsIt() => _response.StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public void KeysItWithADoubleDash() =>
        _view.GetProperty("key").GetString().ShouldBe("youtube--NQXAC9IVRw");

    [Fact]
    public async Task CanBeReadBackByItsRoute()
    {
        using var response = await WatchApi.GetAsync(Client, "-NQXAC9IVRw");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CanBeDeletedByItsRoute()
    {
        using var response = await WatchApi.DeleteAsync(Client, "-NQXAC9IVRw");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}

/// <summary>The provider is ours to name, so its case does not matter; the ID's does.</summary>
public sealed class WhenTheProviderIsSpelledInAnotherCase : WatchScenario
{
    private readonly HttpResponseMessage _response;
    private readonly JsonElement _view;

    public WhenTheProviderIsSpelledInAnotherCase()
    {
        App.WebAudio.Hold = new Gate();

        _response = WatchApi.PostAsync(Client, provider: "YouTube").GetAwaiter().GetResult();
        _view = Api.ReadAsync(_response).GetAwaiter().GetResult();
    }

    [Fact]
    public void AcceptsIt() => _response.StatusCode.ShouldBe(HttpStatusCode.OK);

    [Fact]
    public void AnswersWithTheCanonicalProvider() =>
        _view.GetProperty("provider").GetString().ShouldBe("youtube");

    [Fact]
    public async Task IsTheSameSessionAsTheLowerCaseRoute()
    {
        using var response = await WatchApi.GetAsync(Client, provider: "YOUTUBE");
        var view = await Api.ReadAsync(response);

        view.GetProperty("session").GetString().ShouldBe(_view.GetProperty("session").GetString());
    }
}

/// <summary>
/// The playhead picks the next window, so it must be a real, non-negative
/// number. It is required: every heartbeat knows where the video is, and a
/// missing one is a client bug better surfaced than papered over with 0.
/// </summary>
public sealed class WhenAWatchGivesABadPosition : WatchScenario
{
    [Theory]
    [InlineData("-1")]
    [InlineData("-0.001")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("1e400")]
    [InlineData("null")]
    [InlineData("\"twelve\"")]
    [InlineData("true")]
    [InlineData("[]")]
    public async Task RefusesIt(string position)
    {
        using var response = await WatchApi.PostJsonAsync(
            Client,
            $$"""{"provider":"youtube","video_id":"{{WatchApi.Video}}","position":{{position}}}"""
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusesAMissingPosition()
    {
        using var response = await WatchApi.PostJsonAsync(
            Client,
            $$"""{"provider":"youtube","video_id":"{{WatchApi.Video}}"}"""
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NamesThePositionAsTheProblem()
    {
        using var response = await WatchApi.PostAsync(Client, position: -1);
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("position");
    }

    [Fact]
    public async Task StartsNothingForABadPosition()
    {
        using var response = await WatchApi.PostAsync(Client, position: -1);

        App.WebAudio.Fetches.ShouldBe(0);
    }
}

public sealed class WhenAWatchGivesAGoodPosition : WatchScenario
{
    public WhenAWatchGivesAGoodPosition() => App.WebAudio.Hold = new Gate();

    [Theory]
    [InlineData("0")]
    [InlineData("0.0")]
    [InlineData("12.3")]
    [InlineData("86400")]
    [InlineData("1e300")]
    public async Task AcceptsIt(string position)
    {
        using var response = await WatchApi.PostJsonAsync(
            Client,
            $$"""{"provider":"youtube","video_id":"{{WatchApi.Video}}","position":{{position}}}"""
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task IgnoresFieldsItDoesNotKnow()
    {
        using var response = await WatchApi.PostJsonAsync(
            Client,
            $$"""{"provider":"youtube","video_id":"{{WatchApi.Video}}","position":1,"rate":2}"""
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

/// <summary>
/// Only a JSON body is a heartbeat. That is also a defence: a web page can send
/// <c>text/plain</c> cross-origin without a preflight, but not
/// <c>application/json</c>.
/// </summary>
public sealed class WhenAWatchIsNotJson : WatchScenario
{
    private static readonly string Body =
        $$"""{"provider":"youtube","video_id":"{{WatchApi.Video}}","position":0}""";

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("text/html")]
    public async Task IsAnUnsupportedMediaType(string contentType)
    {
        using var response = await WatchApi.PostRawAsync(Client, Body, contentType);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task IsAnUnsupportedMediaTypeWithNoContentTypeAtAll()
    {
        using var response = await WatchApi.PostRawAsync(Client, Body, contentType: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task ExplainsWhyInTheFieldTheFrontEndReads()
    {
        using var response = await WatchApi.PostRawAsync(Client, Body, "text/plain");
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task StartsNothing()
    {
        using var response = await WatchApi.PostRawAsync(Client, Body, "text/plain");

        App.WebAudio.Fetches.ShouldBe(0);
    }

    [Fact]
    public async Task AcceptsJsonWithACharset()
    {
        App.WebAudio.Hold = new Gate();
        using var response = await WatchApi.PostRawAsync(
            Client,
            Body,
            "application/json; charset=utf-8"
        );

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

public sealed class WhenAWatchIsMalformedJson : WatchScenario
{
    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"youtube\"")]
    [InlineData("42")]
    public async Task IsABadRequest(string body)
    {
        using var response = await WatchApi.PostJsonAsync(Client, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExplainsWhyInTheFieldTheFrontEndReads()
    {
        using var response = await WatchApi.PostJsonAsync(Client, "{");
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}

/// <summary>
/// yt-dlp refused the video: the extension shows the reason and, from the
/// kind, what to do about it (fail-closed, W15).
/// </summary>
public sealed class WhenTheAudioCannotBeFetched : WatchScenario
{
    private readonly JsonElement _view;

    public WhenTheAudioCannotBeFetched()
    {
        App.WebAudio.Failure = StubWebAudioSource.Failing(
            VideoRef.Create("youtube", WatchApi.Video),
            WebAudioFailure.SignInRequired,
            "Sign in to confirm your age."
        );

        WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        _view = WatchApi.WaitForStateAsync(Client, "failed").GetAwaiter().GetResult();
    }

    [Fact]
    public void IsFailed() => _view.GetProperty("state").GetString().ShouldBe("failed");

    [Fact]
    public void GivesYtDlpsReason() =>
        _view.GetProperty("reason").GetString().ShouldBe("Sign in to confirm your age.");

    [Fact]
    public void GivesTheKindInSnakeCase() =>
        _view.GetProperty("failure_kind").GetString().ShouldBe("sign_in_required");

    [Fact]
    public void HasNoHits() => _view.GetProperty("hits").GetArrayLength().ShouldBe(0);

    [Fact]
    public void HasNoProgress() => _view.GetProperty("progress").GetDouble().ShouldBe(0.0);

    /// <summary>A failure stays readable for its retention, and a repeat heartbeat does not retry it at once.</summary>
    [Fact]
    public async Task AnotherHeartbeatDoesNotRetryStraightAway()
    {
        await WatchApi.StartAsync(Client);

        App.WebAudio.Fetches.ShouldBe(1);
    }
}

public sealed class WhenTheVideoIsALivestream : WatchScenario
{
    private readonly JsonElement _view;

    public WhenTheVideoIsALivestream()
    {
        App.WebAudio.Failure = StubWebAudioSource.Failing(
            VideoRef.Create("youtube", WatchApi.Video),
            WebAudioFailure.Unsupported,
            "This live event will begin in a few moments."
        );

        WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        _view = WatchApi.WaitForStateAsync(Client, "unsupported").GetAwaiter().GetResult();
    }

    [Fact]
    public void IsUnsupported() => _view.GetProperty("state").GetString().ShouldBe("unsupported");

    [Fact]
    public void SaysWhy() =>
        _view
            .GetProperty("reason")
            .GetString()
            .ShouldBe("This live event will begin in a few moments.");

    [Fact]
    public void GivesTheKind() =>
        _view.GetProperty("failure_kind").GetString().ShouldBe("unsupported");
}

/// <summary>A failure after the fetch has a reason but no fetch-failure kind.</summary>
public sealed class WhenTheEngineFails : WatchScenario
{
    private readonly JsonElement _view;

    public WhenTheEngineFails()
    {
        App.Engine.OpenFailure = new InvalidOperationException("The model file is corrupt.");

        WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        _view = WatchApi.WaitForStateAsync(Client, "failed").GetAwaiter().GetResult();
    }

    [Fact]
    public void GivesTheReason() =>
        _view.GetProperty("reason").GetString().ShouldBe("The model file is corrupt.");

    [Fact]
    public void HasNoFailureKind() =>
        _view.GetProperty("failure_kind").ValueKind.ShouldBe(JsonValueKind.Null);
}

/// <summary>A video watched before: straight to complete from the Transcript cache, no yt-dlp.</summary>
public sealed class WhenTheVideoIsInTheTranscriptCache : WatchScenario
{
    private readonly JsonElement _view;

    public WhenTheVideoIsInTheTranscriptCache()
    {
        var heard = StubWhisperEngine.Swearing;
        new TranscriptStore(App.TranscriptDirectory)
            .SaveAsync(
                new Transcript(
                    Transcript.CurrentVersion,
                    "youtube-" + WatchApi.Video,
                    heard.Segments,
                    heard.Words
                ),
                "Me at the zoo",
                WatchApi.Token
            )
            .GetAwaiter()
            .GetResult();

        WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        _view = WatchApi.WaitForStateAsync(Client, "complete").GetAwaiter().GetResult();
    }

    [Fact]
    public void SaysItCameFromTheCache() =>
        _view.GetProperty("from_cache").GetBoolean().ShouldBeTrue();

    [Fact]
    public void FetchesNothing() => App.WebAudio.Fetches.ShouldBe(0);

    [Fact]
    public void ServesTheCachedHits() =>
        _view.GetProperty("hits")[0].GetProperty("phrase").GetString().ShouldBe("damn");

    [Fact]
    public void CoversEverything() => _view.GetProperty("progress").GetDouble().ShouldBe(1.0);
}

/// <summary>
/// W17: a video watched before is answered complete by the very first POST,
/// so the extension can release the gate without a second poll.
/// </summary>
public sealed class WhenACachedVideoIsFirstPosted : WatchScenario
{
    private readonly JsonElement _first;

    public WhenACachedVideoIsFirstPosted()
    {
        var heard = StubWhisperEngine.Swearing;
        new TranscriptStore(App.TranscriptDirectory)
            .SaveAsync(
                new Transcript(
                    Transcript.CurrentVersion,
                    "youtube-" + WatchApi.Video,
                    heard.Segments,
                    heard.Words
                ),
                "Me at the zoo",
                WatchApi.Token
            )
            .GetAwaiter()
            .GetResult();

        _first = WatchApi.StartAsync(Client).GetAwaiter().GetResult();
    }

    [Fact]
    public void IsCompleteInTheFirstAnswer() =>
        _first.GetProperty("state").GetString().ShouldBe("complete");

    [Fact]
    public void SaysItCameFromTheCache() =>
        _first.GetProperty("from_cache").GetBoolean().ShouldBeTrue();

    [Fact]
    public void CarriesTheHitsInTheFirstAnswer() =>
        _first.GetProperty("hits")[0].GetProperty("phrase").GetString().ShouldBe("damn");

    [Fact]
    public void FetchesNothing() => App.WebAudio.Fetches.ShouldBe(0);
}

/// <summary>W17: a miss is answered as soon as it is known to be one.</summary>
public sealed class WhenAMissIsFirstPosted : WatchScenario
{
    private readonly JsonElement _first;

    public WhenAMissIsFirstPosted()
    {
        App.WebAudio.Hold = new Gate();
        _first = WatchApi.StartAsync(Client).GetAwaiter().GetResult();
    }

    [Fact]
    public void IsFetchingInTheFirstAnswer() =>
        _first.GetProperty("state").GetString().ShouldBe("fetching");
}
