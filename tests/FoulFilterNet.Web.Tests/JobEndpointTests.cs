using System.Net;
using System.Text.Json;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Web.Tests;

public class WhenTwoFilesAreUploaded : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly JsonElement _body;

    public WhenTwoFilesAreUploaded()
    {
        _client = _app.CreateClient();

        using var response = Api.UploadAsync(_client, ["book: [one].MP3", "two.wav"])
            .GetAwaiter()
            .GetResult();
        _body = Api.ReadAsync(response).GetAwaiter().GetResult();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void QueuesOneJobPerFile() => _body.GetProperty("job_ids").GetArrayLength().ShouldBe(2);

    [Fact]
    public void SaysHowManyItQueued() =>
        _body.GetProperty("message").GetString().ShouldBe("2 job(s) queued.");

    [Fact]
    public async Task SanitizesTheNameItShowsBack()
    {
        var jobs = await Api.GetAsync(_client, "/jobs");

        jobs[0].GetProperty("filename").GetString().ShouldBe("book - one.mp3");
    }

    [Fact]
    public void WritesOneUploadPerFile() =>
        Directory.GetFiles(_app.UploadDirectory).Length.ShouldBe(2);

    [Fact]
    public void NamesEachUploadAfterItsJobSoTwoBooksCannotCollide() =>
        Directory
            .GetFiles(_app.UploadDirectory)
            .ShouldAllBe(path => Path.GetFileName(path).Contains("__", StringComparison.Ordinal));

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenAnUploadIsRejected : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;

    public WhenAnUploadIsRejected()
    {
        _client = _app.CreateClient();

        Directory.Exists(_app.UploadDirectory).ShouldBeTrue();
    }

    [Fact]
    public async Task RefusesAFileTypeThePipelineCannotOpen()
    {
        using var response = await Api.UploadAsync(_client, ["notes.txt"]);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExplainsWhyInTheFieldTheFrontEndReads()
    {
        using var response = await Api.UploadAsync(_client, ["notes.txt"]);
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail")
            .GetString()
            .ShouldNotBeNull()
            .ShouldContain("Unsupported file type");
    }

    [Fact]
    public async Task RefusesACensorMethodThatIsNotOneOfTheThree()
    {
        using var response = await Api.UploadAsync(
            _client,
            ["book.mp3"],
            censorMethod: "obliterate"
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LeavesNoUploadBehindWhenTheOptionsAreWrong()
    {
        using var response = await Api.UploadAsync(
            _client,
            ["book.mp3"],
            censorMethod: "obliterate"
        );

        Directory.GetFiles(_app.UploadDirectory).ShouldBeEmpty();
    }

    /// <summary>
    /// The Python returned its 400 and left whatever it had already written on
    /// disk, where only the next restart would clear it.
    /// </summary>
    [Fact]
    public async Task LeavesNoEarlierUploadBehindWhenALaterFileIsRefused()
    {
        using var response = await Api.UploadAsync(_client, ["good.mp3", "notes.txt"]);

        Directory.GetFiles(_app.UploadDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task QueuesNothing()
    {
        using var response = await Api.UploadAsync(_client, ["notes.txt"]);
        var jobs = await Api.GetAsync(_client, "/jobs");

        jobs.GetArrayLength().ShouldBe(0);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The cap has to answer 413 rather than let an oversized body settle on the
/// server's disk.
/// </summary>
public class WhenAnUploadIsTooBig : IDisposable
{
    private readonly FoulFilterApplication _app = new(maxUploadMegabytes: 1);
    private readonly HttpClient _client;
    private readonly HttpStatusCode _status;

    public WhenAnUploadIsTooBig()
    {
        _client = _app.CreateClient();

        using var response = Api.UploadAsync(_client, ["huge.mp3"], sizeBytes: 3 * 1024 * 1024)
            .GetAwaiter()
            .GetResult();
        _status = response.StatusCode;
    }

    [Fact]
    public void AnswersPayloadTooLarge() => _status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);

    [Fact]
    public void RemovesThePartialUpload() =>
        Directory.GetFiles(_app.UploadDirectory).ShouldBeEmpty();

    [Fact]
    public async Task QueuesNothing()
    {
        var jobs = await Api.GetAsync(_client, "/jobs");

        jobs.GetArrayLength().ShouldBe(0);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenAJobRunsThroughTheApi : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly string _jobId;
    private readonly JsonElement _completed;

    public WhenAJobRunsThroughTheApi()
    {
        _client = _app.CreateClient();
        _jobId = Api.UploadOneAsync(_client, censorMethod: "bleep", rescan: true)
            .GetAwaiter()
            .GetResult();
        _completed = Api.WaitForStatusAsync(_client, _jobId, "completed").GetAwaiter().GetResult();

        _completed.GetProperty("id").GetString().ShouldBe(_jobId);
    }

    [Fact]
    public void ReportsTheCensorMethodItWasGiven() =>
        _completed.GetProperty("censor_method").GetString().ShouldBe("bleep");

    [Fact]
    public void ReportsTheRescanFlagItWasGiven() =>
        _completed.GetProperty("rescan").GetBoolean().ShouldBeTrue();

    [Fact]
    public void OffersTheDownloadLink() =>
        _completed.GetProperty("download_url").GetString().ShouldBe($"/download/{_jobId}");

    [Fact]
    public void HandsThePipelineTheCensorMethodAsAnEnumRatherThanAString() =>
        _app.Pipeline.Requests.Single().CensorMethod.ShouldBe(CensorMethod.Bleep);

    [Fact]
    public void GivesThePipelineAScratchDirectoryOfItsOwn() =>
        _app.Pipeline.Requests.Single().ScratchDirectory.ShouldEndWith(_jobId);

    [Fact]
    public async Task ServesTheCensoredFile()
    {
        using var download = await _client.GetAsync(
            new Uri($"/download/{_jobId}", UriKind.Relative),
            Api.Token
        );

        download.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NamesTheDownloadAfterTheCensoredOutput()
    {
        using var download = await _client.GetAsync(
            new Uri($"/download/{_jobId}", UriKind.Relative),
            Api.Token
        );

        download
            .Content.Headers.ContentDisposition!.FileName.ShouldNotBeNull()
            .ShouldContain("censored_book.mp3");
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenAJobFailsInThePipeline : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly JsonElement _failed;

    public WhenAJobFailsInThePipeline()
    {
        _app.Pipeline.Behaviour = (_, _, _) =>
            throw new InvalidOperationException("ffmpeg said no");
        _client = _app.CreateClient();

        var jobId = Api.UploadOneAsync(_client).GetAwaiter().GetResult();
        _failed = Api.WaitForStatusAsync(_client, jobId, "failed").GetAwaiter().GetResult();
    }

    [Fact]
    public void TellsTheUiWhy() =>
        _failed.GetProperty("detail").GetString().ShouldBe("ffmpeg said no");

    [Fact]
    public void OffersNoDownload() =>
        _failed.GetProperty("download_url").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public async Task AnswersADownloadRequestWithA404()
    {
        var jobId = _failed.GetProperty("id").GetString();
        using var response = await _client.GetAsync(
            new Uri($"/download/{jobId}", UriKind.Relative),
            Api.Token
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The Python filtered its responses by stripping keys that end in
/// <c>_path</c>, which is one forgotten suffix away from serving the server's
/// filesystem layout. Nothing here has a path to strip.
/// </summary>
public class WhenReadingJobsBackOverHttp : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly string _payload;

    public WhenReadingJobsBackOverHttp()
    {
        _client = _app.CreateClient();

        var jobId = Api.UploadOneAsync(_client).GetAwaiter().GetResult();
        Api.WaitForStatusAsync(_client, jobId, "completed").GetAwaiter().GetResult();

        _payload = _client
            .GetStringAsync(new Uri("/jobs", UriKind.Relative), Api.Token)
            .GetAwaiter()
            .GetResult();

        _payload.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CarriesNoPathFieldAtAll() => _payload.ShouldNotContain("path", Case.Insensitive);

    [Fact]
    public void NeverNamesTheUploadDirectory() =>
        _payload.ShouldNotContain("uploads", Case.Insensitive);

    [Fact]
    public void NeverNamesTheOutputDirectory() =>
        _payload.ShouldNotContain("outputs", Case.Insensitive);

    [Fact]
    public void KeepsTheKeysTheFrontEndReads()
    {
        var job = JsonDocument.Parse(_payload).RootElement[0];

        foreach (var key in new[] { "id", "filename", "status", "stage", "progress", "detail" })
        {
            job.TryGetProperty(key, out _).ShouldBeTrue($"missing '{key}'");
        }
    }

    [Fact]
    public async Task AnswersAStatusRequestForAJobThatIsNotThereWithA404()
    {
        using var response = await _client.GetAsync(
            new Uri("/status/nosuchjob", UriKind.Relative),
            Api.Token
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnswersADownloadRequestForAJobThatIsNotThereWithA404()
    {
        using var response = await _client.GetAsync(
            new Uri("/download/nosuchjob", UriKind.Relative),
            Api.Token
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenDeletingAJobOverHttp : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;

    public WhenDeletingAJobOverHttp() => _client = _app.CreateClient();

    [Fact]
    public async Task CancelsAJobThatIsStillQueuedBehindALongOne()
    {
        _app.Pipeline.Behaviour = async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new JobSummary([], 0, false, false);
        };

        using var upload = await Api.UploadAsync(_client, ["first.mp3", "second.mp3"]);
        var queued = (await Api.ReadAsync(upload)).GetProperty("job_ids")[1].GetString();

        using var response = await _client.DeleteAsync(
            new Uri($"/jobs/{queued}", UriKind.Relative),
            Api.Token
        );

        (await Api.ReadAsync(response)).GetProperty("cancelled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task AnswersA404ForAJobThatIsNotThere()
    {
        using var response = await _client.DeleteAsync(
            new Uri("/jobs/nosuchjob", UriKind.Relative),
            Api.Token
        );

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>The UI reuses DELETE as "remove this row", so this is not an error.</summary>
    [Fact]
    public async Task ReportsNothingCancelledForAJobThatAlreadyFinished()
    {
        var jobId = await Api.UploadOneAsync(_client);
        await Api.WaitForStatusAsync(_client, jobId, "completed");

        using var response = await _client.DeleteAsync(
            new Uri($"/jobs/{jobId}", UriKind.Relative),
            Api.Token
        );

        (await Api.ReadAsync(response)).GetProperty("cancelled").GetBoolean().ShouldBeFalse();
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}
