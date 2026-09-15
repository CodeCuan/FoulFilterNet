using System.Net.Http.Headers;
using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// The real host, over a throwaway data directory and a stub pipeline. Stream E
/// depends on no engine: everything below <see cref="IMediaPipeline"/> is T21's,
/// and these tests must pass before a line of it exists.
/// </summary>
internal sealed class FoulFilterApplication(int maxUploadMegabytes = 4096) : WebApplicationFactory<Program>
{
    private readonly TempDirectory _data = new();

    public StubMediaPipeline Pipeline { get; } = new();

    public string DataDirectory => _data.Path;

    public string UploadDirectory => Path.Combine(_data.Path, "uploads");

    public string OutputDirectory => Path.Combine(_data.Path, "outputs");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // T30 makes the host do this at startup; until then the fixture does it.
        Directory.CreateDirectory(Path.Combine(_data.Path, "uploads"));
        Directory.CreateDirectory(Path.Combine(_data.Path, "outputs"));
        Directory.CreateDirectory(Path.Combine(_data.Path, "scratch"));

        builder.UseSetting("Storage:DataDirectory", _data.Path);
        builder.UseSetting("Storage:BadWordsPath", Path.Combine(_data.Path, "bad_words.txt"));
        builder.UseSetting(
            "Storage:MaxUploadMegabytes",
            maxUploadMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMediaPipeline>();
            services.AddSingleton<IMediaPipeline>(Pipeline);
        });
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
/// The pipeline Stream E tests against. By default it writes the output file the
/// job promises and reports no hits.
/// </summary>
internal sealed class StubMediaPipeline : IMediaPipeline
{
    public Func<JobRequest, IProgress<JobProgress>?, CancellationToken, Task<JobSummary>> Behaviour { get; set; } =
        static (request, _, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            File.WriteAllText(request.OutputPath, "censored audio");
            return Task.FromResult(new JobSummary([], 12, false, false));
        };

    public List<JobRequest> Requests { get; } = [];

    public Task<JobSummary> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Behaviour(request, progress, cancellationToken);
    }
}

/// <summary>Talking to the API without drowning each test in ceremony.</summary>
internal static class Api
{
    /// <summary>The token xUnit cancels a test with.</summary>
    public static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Posts <paramref name="fileNames"/> as one multipart upload.</summary>
    public static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        IEnumerable<string> fileNames,
        string censorMethod = "silence",
        int sizeBytes = 16,
        bool debug = false,
        bool rescan = false)
    {
        using var form = new MultipartFormDataContent();
        foreach (var name in fileNames)
        {
            var content = new ByteArrayContent(new byte[sizeBytes]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(content, "files", name);
        }

        form.Add(new StringContent(censorMethod), "censor_method");
        form.Add(new StringContent(debug ? "true" : "false"), "debug");
        form.Add(new StringContent(rescan ? "true" : "false"), "rescan");

        return await client.PostAsync(new Uri("/upload", UriKind.Relative), form, Token);
    }

    /// <summary>Uploads one file and returns the job id it was queued under.</summary>
    public static async Task<string> UploadOneAsync(
        HttpClient client,
        string fileName = "book.mp3",
        string censorMethod = "silence",
        bool rescan = false)
    {
        using var response = await UploadAsync(client, [fileName], censorMethod, rescan: rescan);
        var body = await ReadAsync(response);
        return body.GetProperty("job_ids")[0].GetString()!;
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.Clone();

    public static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
        return await ReadAsync(response);
    }

    /// <summary>Polls <c>/status</c> until the job reaches <paramref name="status"/>.</summary>
    public static async Task<JsonElement> WaitForStatusAsync(HttpClient client, string jobId, string status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var job = await GetAsync(client, $"/status/{jobId}");
            if (job.TryGetProperty("status", out var current) && current.GetString() == status)
            {
                return job;
            }

            await Task.Delay(20, Token);
        }

        throw new TimeoutException($"Job {jobId} never reached '{status}'.");
    }
}
