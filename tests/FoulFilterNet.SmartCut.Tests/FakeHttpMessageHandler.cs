using System.Net;
using System.Text;

namespace FoulFilterNet.SmartCut.Tests;

/// <summary>One request a transport made, captured before its message was disposed.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// The only thing standing in for an LLM in this suite. CI has neither a GPU nor
/// a network, so every transport test drives a scripted handler and asserts on
/// what the transport asked for.
/// </summary>
/// <remarks>
/// Responses are built fresh per call: the transports dispose what they read, so
/// a handler that handed out the same instance twice would fail on the retry.
/// </remarks>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _respond;

    private FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) =>
        _respond = respond;

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Answer each request with the next scripted reply, repeating the last.</summary>
    public static FakeHttpMessageHandler Returning(params (HttpStatusCode Status, string Body)[] replies) =>
        new((_, index) =>
        {
            var (status, body) = replies[Math.Min(index, replies.Length - 1)];
            return Json(status, body);
        });

    /// <summary>Answer by request, so a POST and a <c>/models</c> GET can differ.</summary>
    public static FakeHttpMessageHandler Responding(
        Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) =>
        new((request, _) =>
        {
            var (status, body) = respond(request);
            return Json(status, body);
        });

    /// <summary>The server is not there at all.</summary>
    public static FakeHttpMessageHandler Throwing(Exception failure) => new((_, _) => throw failure);

    public HttpClient Client() => new(this, disposeHandler: false);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            body,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value))));

        return _respond(request, Requests.Count - 1);
    }
}
