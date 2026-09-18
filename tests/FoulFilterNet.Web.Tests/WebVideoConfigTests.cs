using System.Text.Json;
using FoulFilterNet.Sources;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// <c>/config</c> says whether web video can work here, the way it already says
/// whether Smart Cut can, so the extension can explain rather than fail.
/// </summary>
public abstract class WebVideoConfigScenario : IDisposable
{
    private readonly HttpClient _client;

    private protected WebVideoConfigScenario(WebVideoAvailability availability)
    {
        App = new FoulFilterApplication();
        App.WebAudio.Availability = availability;
        _client = App.CreateClient();

        Config = Api.GetAsync(_client, "/config").GetAwaiter().GetResult();
        WebVideo = Config.GetProperty("web_video");

        WebVideo.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    private protected FoulFilterApplication App { get; }

    private protected JsonElement Config { get; }

    private protected JsonElement WebVideo { get; }

    private protected JsonElement Again() =>
        Api.GetAsync(_client, "/config").GetAwaiter().GetResult();

    public void Dispose()
    {
        _client.Dispose();
        App.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class WhenYtDlpAndDenoAreBothInstalled()
    : WebVideoConfigScenario(new WebVideoAvailability("2026.09.01", "2.5.0"))
{
    [Fact]
    public void SaysWebVideoIsAvailable() =>
        WebVideo.GetProperty("available").GetBoolean().ShouldBeTrue();

    [Fact]
    public void ReportsYtDlpsVersion() =>
        WebVideo.GetProperty("yt_dlp_version").GetString().ShouldBe("2026.09.01");

    [Fact]
    public void ReportsDenosVersion() =>
        WebVideo.GetProperty("deno_version").GetString().ShouldBe("2.5.0");

    [Fact]
    public void ReportsNoProblem() =>
        WebVideo.GetProperty("problem").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void HasExactlyTheseFields() =>
        WatchApi
            .PropertyNames(WebVideo)
            .ShouldBe(["available", "yt_dlp_version", "deno_version", "problem"]);

    /// <summary>
    /// The existing front end reads these four; they keep their names, order and
    /// meaning, and <c>web_video</c> is only appended.
    /// </summary>
    [Fact]
    public void KeepsTheExistingFieldsAndAppendsTheNewOne() =>
        WatchApi
            .PropertyNames(Config)
            .ShouldBe([
                "censor_methods",
                "ai_enhance",
                "whisper_model",
                "max_upload_mb",
                "web_video",
            ]);

    /// <summary>
    /// A yt-dlp start is over a second on Windows, and the UI asks for
    /// <c>/config</c> on every load: the answer is kept, not re-probed.
    /// </summary>
    [Fact]
    public void ProbesOnceForManyRequests()
    {
        Again();
        Again();

        App.WebAudio.Probes.ShouldBe(1);
    }
}

public sealed class WhenYtDlpIsNotInstalled()
    : WebVideoConfigScenario(new WebVideoAvailability(null, "2.5.0"))
{
    [Fact]
    public void SaysWebVideoIsNotAvailable() =>
        WebVideo.GetProperty("available").GetBoolean().ShouldBeFalse();

    [Fact]
    public void HasNoYtDlpVersion() =>
        WebVideo.GetProperty("yt_dlp_version").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void StillReportsDeno() =>
        WebVideo.GetProperty("deno_version").GetString().ShouldBe("2.5.0");

    [Fact]
    public void SaysYtDlpIsTheProblem() =>
        WebVideo
            .GetProperty("problem")
            .GetString()
            .ShouldNotBeNull()
            .ShouldContain("yt-dlp could not be run");

    [Fact]
    public void LeavesTheBatchFieldsAsTheyWere() =>
        Config.GetProperty("max_upload_mb").GetInt32().ShouldBe(4096);
}

public sealed class WhenDenoIsNotInstalled()
    : WebVideoConfigScenario(new WebVideoAvailability("2026.09.01", null))
{
    [Fact]
    public void SaysWebVideoIsNotAvailable() =>
        WebVideo.GetProperty("available").GetBoolean().ShouldBeFalse();

    [Fact]
    public void StillReportsYtDlp() =>
        WebVideo.GetProperty("yt_dlp_version").GetString().ShouldBe("2026.09.01");

    [Fact]
    public void HasNoDenoVersion() =>
        WebVideo.GetProperty("deno_version").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void SaysDenoIsTheProblem() =>
        WebVideo.GetProperty("problem").GetString().ShouldNotBeNull().ShouldContain("Deno");
}
