using System.Net.Http;
using System.Text.Json;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// Reads a live <c>text/event-stream</c> response frame by frame.
/// </summary>
/// <remarks>
/// <para>
/// Connecting does not complete until the response actually starts, and the
/// response does not start until the endpoint yields its first event. So a test
/// that connects while there is nothing to send must do the thing that produces
/// an event <em>concurrently</em>, or it deadlocks against itself. Where the
/// jobs already exist, the snapshot is sent immediately and connecting returns
/// straight away.
/// </para>
/// <para>
/// Every read is bounded by a timeout and returns what arrived rather than
/// throwing, so a test expecting three events and seeing two fails on its
/// assertion instead of on plumbing.
/// </para>
/// </remarks>
internal sealed class SseSession : IAsyncDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseSession(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    /// <summary>Every line seen so far, including field lines and comments.</summary>
    public List<string> RawLines { get; } = [];

    public HttpResponseMessage Response => _response;

    public static async Task<SseSession> ConnectAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/events", UriKind.Relative));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Api.Token);
        var stream = await response.Content.ReadAsStreamAsync(Api.Token);
        return new SseSession(response, new StreamReader(stream));
    }

    /// <summary>Reads until <paramref name="count"/> data payloads arrive, or time runs out.</summary>
    public async Task<IReadOnlyList<JsonElement>> ReadDataAsync(int count, int timeoutSeconds = 10)
    {
        var payloads = new List<JsonElement>();
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var complete = false;
            while (!complete)
            {
                var line = await _reader.ReadLineAsync(expiry.Token);
                if (line is null)
                {
                    break;
                }

                RawLines.Add(line);

                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    payloads.Add(JsonDocument.Parse(line[6..]).RootElement.Clone());
                }

                // One event's fields run up to its terminating blank line, and
                // retry: is written after data:. Stopping the moment the payload
                // count is reached would read half a frame and miss the rest.
                complete = payloads.Count >= count && line.Length == 0;
            }
        }
        catch (OperationCanceledException)
        {
            // Out of time. The caller's assertion says what was missing.
        }
        catch (IOException)
        {
            // Cancelling a read aborts the request underneath, and the test host
            // surfaces that as an IOException rather than a cancellation.
        }

        return payloads;
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _response.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>
/// The live case: a listener is already connected when a job is queued. The
/// connect and the upload have to overlap - see <see cref="SseSession"/>.
/// </summary>
public class WhenAJobIsQueuedWhileAListenerWatches : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly SseSession _events;
    private readonly IReadOnlyList<JsonElement> _received;
    private readonly string _jobId;

    public WhenAJobIsQueuedWhileAListenerWatches()
    {
        _client = _app.CreateClient();

        var listening = Task.Run(async () =>
        {
            var session = await SseSession.ConnectAsync(_client);
            var data = await session.ReadDataAsync(1);
            return (session, data);
        });

        _jobId = Api.UploadOneAsync(_client).GetAwaiter().GetResult();
        (_events, _received) = listening.GetAwaiter().GetResult();

        _events.Response.IsSuccessStatusCode.ShouldBeTrue();
        _received.ShouldNotBeEmpty();
    }

    [Fact]
    public void AnnouncesAnEventStream() =>
        _events.Response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");

    [Fact]
    public void TellsTheClientHowLongToWaitBeforeReconnecting() =>
        _events.RawLines.ShouldContain(line => line.StartsWith("retry:", StringComparison.Ordinal));

    /// <summary>
    /// The front end listens with <c>es.onmessage</c>, which only fires for
    /// events of the default type. Naming the event would silently leave every
    /// row in the table frozen.
    /// </summary>
    [Fact]
    public void LeavesEventsUnnamedSoOnMessageFires() =>
        _events.RawLines.ShouldNotContain(line => line.StartsWith("event:", StringComparison.Ordinal));

    [Fact]
    public void SendsTheJobThatWasQueued() =>
        _received.ShouldContain(job => job.GetProperty("id").GetString() == _jobId);

    [Fact]
    public void SendsTheSameShapeTheJobsEndpointDoes() =>
        _received[0].TryGetProperty("filename", out _).ShouldBeTrue();

    [Fact]
    public void NeverLeaksAFilesystemPath() =>
        _received[0].EnumerateObject()
            .ShouldAllBe(property => !property.Name.EndsWith("_path", StringComparison.Ordinal));

    public void Dispose()
    {
        _events.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A client that connects late must still learn about jobs that already exist,
/// otherwise a browser refresh shows an empty table until something changes.
/// This is also what makes a dropped connection harmless, and therefore why the
/// endpoint needs no keepalive.
/// </summary>
public class WhenAListenerConnectsAfterAJobExists : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly SseSession _events;
    private readonly IReadOnlyList<JsonElement> _received;
    private readonly string _jobId;

    public WhenAListenerConnectsAfterAJobExists()
    {
        _client = _app.CreateClient();
        _jobId = Api.UploadOneAsync(_client).GetAwaiter().GetResult();

        _events = SseSession.ConnectAsync(_client).GetAwaiter().GetResult();
        _received = _events.ReadDataAsync(1).GetAwaiter().GetResult();

        _received.ShouldNotBeEmpty();
    }

    [Fact]
    public void ReplaysTheJobItMissed() =>
        _received.ShouldContain(job => job.GetProperty("id").GetString() == _jobId);

    public void Dispose()
    {
        _events.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// One worker feeds one GPU, so a listener must never be able to hold it up.
/// Here a stream is opened and then never read again while a job runs to
/// completion.
/// </summary>
public class WhenAListenerStopsReading : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;
    private readonly SseSession _events;
    private readonly JsonElement _job;

    public WhenAListenerStopsReading()
    {
        _client = _app.CreateClient();

        // Queue first so the snapshot is non-empty and connecting returns at once.
        var jobId = Api.UploadOneAsync(_client).GetAwaiter().GetResult();
        _events = SseSession.ConnectAsync(_client).GetAwaiter().GetResult();

        _job = Api.WaitForStatusAsync(_client, jobId, "completed").GetAwaiter().GetResult();
    }

    [Fact]
    public void TheJobStillFinishes() => _job.GetProperty("status").GetString().ShouldBe("completed");

    public void Dispose()
    {
        _events.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.Dispose();
        _app.Dispose();
        GC.SuppressFinalize(this);
    }
}
