using System.Net;
using System.Text;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// DNS rebinding: a page on <c>evil.example</c> re-points its own hostname at
/// 127.0.0.1 and then talks to this service "same-origin". The browser still
/// sends <c>Host: evil.example</c>, so answering only to localhost's names
/// closes it - for the Watch endpoints and every existing one.
/// </summary>
public sealed class WhenARequestNamesAForeignHost : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;

    public WhenARequestNamesAForeignHost()
    {
        _client = _app.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:8000")]
    [InlineData("localhost.evil.example")]
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("192.168.1.10:8000")]
    [InlineData("0.0.0.0:8000")]
    public async Task RefusesTheHealthCheck(string host)
    {
        using var response = await SendAsync(HttpMethod.Get, "/health", host);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusesAHeartbeat()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/watch",
            "evil.example",
            Heartbeat()
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartsNothingForARefusedHeartbeat()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/watch",
            "evil.example",
            Heartbeat()
        );

        _app.WebAudio.Fetches.ShouldBe(0);
    }

    [Fact]
    public async Task RefusesASnapshotRead()
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/watch/youtube/{WatchApi.Video}",
            "evil.example"
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusesTheJobList()
    {
        using var response = await SendAsync(HttpMethod.Get, "/jobs", "evil.example");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusesTheConfig()
    {
        using var response = await SendAsync(HttpMethod.Get, "/config", "evil.example");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefusesThePage()
    {
        using var response = await SendAsync(HttpMethod.Get, "/", "evil.example");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string host,
        HttpContent? content = null
    )
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = content,
        };
        request.Headers.Host = host;
        return await _client.SendAsync(request, WatchApi.Token);
    }

    private static StringContent Heartbeat() =>
        new(
            $$"""{"provider":"youtube","video_id":"{{WatchApi.Video}}","position":0}""",
            Encoding.UTF8,
            "application/json"
        );
}

/// <summary>
/// The names the service is reached by on this machine - with or without the
/// port, IPv4 or IPv6 loopback - all still work, including the existing UI's.
/// </summary>
public sealed class WhenARequestNamesLocalhost : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;

    public WhenARequestNamesLocalhost()
    {
        _client = _app.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8000")]
    [InlineData("LOCALHOST:8000")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:8000")]
    [InlineData("[::1]")]
    [InlineData("[::1]:8000")]
    public async Task AnswersTheHealthCheck(string host)
    {
        using var response = await SendAsync(HttpMethod.Get, "/health", host);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/static/app.js")]
    [InlineData("/static/app.css")]
    [InlineData("/config")]
    [InlineData("/jobs")]
    public async Task ServesTheExistingUi(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, "localhost:8000");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AcceptsAHeartbeat()
    {
        _app.WebAudio.Hold = new Gate();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/watch", UriKind.Relative)
        )
        {
            Content = new StringContent(
                $$"""{"provider":"youtube","video_id":"{{WatchApi.Video}}","position":0}""",
                Encoding.UTF8,
                "application/json"
            ),
        };
        request.Headers.Host = "127.0.0.1:8000";

        using var response = await _client.SendAsync(request, WatchApi.Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string host)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Host = host;
        return await _client.SendAsync(request, WatchApi.Token);
    }
}

/// <summary>
/// The plan's exposure rule: no CORS policy. The extension's service worker
/// calls through its <c>host_permissions</c>, which bypass CORS, so a web page
/// gets no <c>Access-Control-Allow-Origin</c> to read an answer with, and its
/// JSON POST fails the preflight.
/// </summary>
public sealed class WhenAWebPageTriesACrossOriginCall : IDisposable
{
    private readonly FoulFilterApplication _app = new();
    private readonly HttpClient _client;

    public WhenAWebPageTriesACrossOriginCall()
    {
        _client = _app.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _app.Dispose();
    }

    [Fact]
    public async Task GrantsNoOriginOnARead()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/config", UriKind.Relative)
        );
        request.Headers.Add("Origin", "https://www.youtube.com");

        using var response = await _client.SendAsync(request, WatchApi.Token);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task AnswersNoPreflight()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            new Uri("/watch", UriKind.Relative)
        );
        request.Headers.Add("Origin", "https://www.youtube.com");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await _client.SendAsync(request, WatchApi.Token);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }
}
