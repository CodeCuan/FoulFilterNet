using System.IO.Compression;
using System.Net;
using System.Net.Http;

namespace FoulFilterNet.Web.Tests;

public class WhenDownloadingTwoFinishedOutputsAsAZip : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly ZipArchive _archive;
    private readonly string[] _entryNames;

    public WhenDownloadingTwoFinishedOutputsAsAZip()
    {
        _client = _app.CreateClient();

        var first = Api.UploadOneAsync(_client, "book.mp3").GetAwaiter().GetResult();
        var second = Api.UploadOneAsync(_client, "talk.wav").GetAwaiter().GetResult();
        Api.WaitForStatusAsync(_client, first, "completed").GetAwaiter().GetResult();
        Api.WaitForStatusAsync(_client, second, "completed").GetAwaiter().GetResult();

        _response = Zip.GetAsync(_client, first, second).GetAwaiter().GetResult();
        _archive = Zip.OpenAsync(_response).GetAwaiter().GetResult();
        _entryNames = [.. _archive.Entries.Select(entry => entry.FullName)];

        _response.StatusCode.ShouldBe(HttpStatusCode.OK);
        _entryNames.ShouldNotBeEmpty();
    }

    [Fact]
    public void ContainsOneEntryPerJob() => _entryNames.Length.ShouldBe(2);

    /// <summary>
    /// Finding 3: outputs are already named <c>censored_&lt;name&gt;</c>, and the
    /// Python zipped them under another <c>censored_</c>, so every download
    /// arrived full of <c>censored_censored_book.mp3</c>.
    /// </summary>
    [Fact]
    public void DoesNotPrefixNamesThatAreAlreadyPrefixed() =>
        _entryNames.ShouldAllBe(name => !name.StartsWith("censored_censored_", StringComparison.Ordinal));

    [Fact]
    public void NamesEachEntryAfterItsOutputFile() =>
        _entryNames.ShouldContain("censored_book.mp3");

    [Fact]
    public void AnnouncesAZip() =>
        _response.Content.Headers.ContentType?.MediaType.ShouldBe("application/zip");

    [Fact]
    public void OffersItUnderOneFileName() =>
        _response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            .ShouldBe("foulfilter_results.zip");

    [Fact]
    public void CarriesTheCensoredContent()
    {
        using var reader = new StreamReader(_archive.Entries[0].Open());

        reader.ReadToEnd().ShouldBe("censored audio");
    }

    public void Dispose()
    {
        _archive.Dispose();
        _response.Dispose();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The UI lets a row be ticked before it has finished, so a selection can name
/// jobs that have no output. Those are skipped rather than failing the download.
/// </summary>
public class WhenOnlySomeSelectedJobsHaveFinished : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly HttpResponseMessage _response;
    private readonly ZipArchive _archive;

    public WhenOnlySomeSelectedJobsHaveFinished()
    {
        _client = _app.CreateClient();

        var finished = Api.UploadOneAsync(_client, "done.mp3").GetAwaiter().GetResult();
        Api.WaitForStatusAsync(_client, finished, "completed").GetAwaiter().GetResult();

        _response = Zip.GetAsync(_client, finished, "nosuchjob").GetAwaiter().GetResult();
        _archive = Zip.OpenAsync(_response).GetAwaiter().GetResult();

        _response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void IncludesOnlyTheFinishedOne() => _archive.Entries.Count.ShouldBe(1);

    [Fact]
    public void NamesTheFinishedOne() =>
        _archive.Entries[0].FullName.ShouldBe("censored_done.mp3");

    public void Dispose()
    {
        _archive.Dispose();
        _response.Dispose();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenNothingSelectedHasFinished : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly HttpResponseMessage _unknown;
    private readonly HttpResponseMessage _empty;

    public WhenNothingSelectedHasFinished()
    {
        _client = _app.CreateClient();
        _unknown = Zip.GetAsync(_client, "nosuchjob").GetAwaiter().GetResult();
        _empty = _client.GetAsync(new Uri("/download_zip?ids=", UriKind.Relative), Api.Token)
            .GetAwaiter().GetResult();
    }

    [Fact]
    public void SaysThereIsNothingToZip() =>
        _unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

    [Fact]
    public void TreatsAnEmptySelectionTheSameWay() =>
        _empty.StatusCode.ShouldBe(HttpStatusCode.NotFound);

    public void Dispose()
    {
        _unknown.Dispose();
        _empty.Dispose();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The zip is built somewhere before it is sent. Asking twice must work, which
/// it would not if the first request left its scratch file behind and the
/// second collided with it.
/// </summary>
public class WhenTheSameZipIsRequestedTwice : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly HttpResponseMessage _first;
    private readonly HttpResponseMessage _second;

    public WhenTheSameZipIsRequestedTwice()
    {
        _client = _app.CreateClient();

        var jobId = Api.UploadOneAsync(_client, "book.mp3").GetAwaiter().GetResult();
        Api.WaitForStatusAsync(_client, jobId, "completed").GetAwaiter().GetResult();

        _first = Zip.GetAsync(_client, jobId).GetAwaiter().GetResult();
        _first.Content.ReadAsByteArrayAsync(Api.Token).GetAwaiter().GetResult();
        _second = Zip.GetAsync(_client, jobId).GetAwaiter().GetResult();
    }

    [Fact]
    public void BothSucceed()
    {
        _first.StatusCode.ShouldBe(HttpStatusCode.OK);
        _second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    public void Dispose()
    {
        _first.Dispose();
        _second.Dispose();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal static class Zip
{
    public static Task<HttpResponseMessage> GetAsync(HttpClient client, params string[] jobIds) =>
        client.GetAsync(
            new Uri($"/download_zip?ids={string.Join(',', jobIds)}", UriKind.Relative),
            Api.Token);

    public static async Task<ZipArchive> OpenAsync(HttpResponseMessage response)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(Api.Token);
        return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    }
}
