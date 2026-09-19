using System.Globalization;
using System.Text.Json;
using FoulFilterNet.Sources;
using FoulFilterNet.Watch;
using FoulFilterNet.Web.Contracts;

namespace FoulFilterNet.Web.Endpoints;

/// <summary>
/// The HTTP surface the browser extension drives (docs/04-web-video-plan.md,
/// "HTTP surface"): a heartbeat that starts and keeps alive a Watch Session,
/// a side-effect-free read, and a cancel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything the extension sends is untrusted.</b> The provider and ID only
/// ever become a <see cref="VideoRef"/> through <see cref="VideoRef.TryCreate"/>
/// (by way of <see cref="WatchVideos"/>), so nothing but a validated
/// 11-character ID reaches the session, and yt-dlp only ever sees the watch URL
/// built from it. The one exception is the development-only <c>file</c>
/// provider (W16), accepted only when the host was configured with it. A refusal is a 400 with the
/// same <c>{ "detail": … }</c> body as the batch endpoints.
/// </para>
/// <para>
/// <b>Exposure.</b> There is no CORS policy (the extension's
/// <c>host_permissions</c> bypass CORS), and the heartbeat demands a JSON
/// content type, which a web page cannot send cross-origin without a preflight
/// that gets no answer. <c>AllowedHosts</c> in appsettings turns away
/// DNS-rebinding hostnames before any of this runs.
/// </para>
/// <para>
/// <b>Cheap polling.</b> Both reads take <c>?session=…&amp;since=…</c>; when
/// they name the session's current revision the Hits are left out (see
/// <see cref="WatchView"/>).
/// </para>
/// </remarks>
public static class WatchEndpoints
{
    private const string InvalidVideo =
        "Not a video this service can watch: provider must be 'youtube', and video_id "
        + "exactly 11 characters from A-Z, a-z, 0-9, '_' and '-'.";

    public static void MapWatchEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/watch", HeartbeatAsync);
        app.MapGet("/watch/{provider}/{id}", Find);
        app.MapDelete("/watch/{provider}/{id}", Cancel);
    }

    /// <summary>
    /// Start the video's session if there is none, record the playhead, and
    /// answer with the session as it stands - at once, never waiting for work.
    /// </summary>
    /// <remarks>
    /// The body is read by hand rather than bound, so that each way of getting
    /// it wrong gets its own status and a sentence in <c>detail</c>: minimal
    /// API binding answers a malformed body with a bare 400 and no reason.
    /// </remarks>
    private static async Task<IResult> HeartbeatAsync(
        HttpRequest request,
        WatchSessionManager sessions,
        WatchVideos videos,
        string? since,
        string? session,
        CancellationToken cancellationToken
    )
    {
        if (!request.HasJsonContentType())
        {
            return Problem(
                StatusCodes.Status415UnsupportedMediaType,
                "Send the heartbeat as application/json."
            );
        }

        if (!TryParseSince(since, out var revision))
        {
            return BadSince();
        }

        WatchRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<WatchRequest>(cancellationToken);
        }
        catch (JsonException exception)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                $"The heartbeat is not valid JSON for {{provider, video_id, position}}: {exception.Message}"
            );
        }

        if (body is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "The heartbeat must be a JSON object with provider, video_id and position."
            );
        }

        if (!videos.TryRead(body.Provider, body.VideoId, out var video))
        {
            return Problem(StatusCodes.Status400BadRequest, InvalidVideo);
        }

        // JSON cannot carry NaN or infinity unless asked to, but an overflowing
        // literal such as 1e400 reads as infinity, so finiteness is checked here.
        if (body.Position is not { } position || !double.IsFinite(position) || position < 0.0)
        {
            return Problem(
                StatusCodes.Status400BadRequest,
                "position is required: the playhead in seconds, a finite number not below 0."
            );
        }

        WatchSnapshot snapshot;
        try
        {
            snapshot = await sessions.HeartbeatAsync(video, position, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The host is shutting down and the manager has cancelled everything.
            return Problem(StatusCodes.Status503ServiceUnavailable, "The service is stopping.");
        }

        return Results.Ok(WatchView.From(snapshot, revision, session));
    }

    /// <summary>
    /// The session as it stands, or 404. Starts nothing and is not a
    /// heartbeat: reading a session does not keep it alive.
    /// </summary>
    private static IResult Find(
        string provider,
        string id,
        string? since,
        string? session,
        WatchSessionManager sessions,
        WatchVideos videos
    )
    {
        if (!videos.TryRead(provider, id, out var video))
        {
            return Problem(StatusCodes.Status400BadRequest, InvalidVideo);
        }

        if (!TryParseSince(since, out var revision))
        {
            return BadSince();
        }

        return sessions.Find(video) is { } snapshot
            ? Results.Ok(WatchView.From(snapshot, revision, session))
            : NoSession();
    }

    /// <summary>
    /// Cancel the session and drop it: 204, or 404 when there was none. The
    /// work stops in the background; the next heartbeat starts afresh.
    /// </summary>
    private static IResult Cancel(
        string provider,
        string id,
        WatchSessionManager sessions,
        WatchVideos videos
    )
    {
        if (!videos.TryRead(provider, id, out var video))
        {
            return Problem(StatusCodes.Status400BadRequest, InvalidVideo);
        }

        return sessions.Cancel(video) ? Results.NoContent() : NoSession();
    }

    /// <summary>
    /// <c>since</c> is a revision: absent, or plain decimal digits that fit a
    /// <see cref="long"/>. Signs, spaces and fractions are refused rather than
    /// guessed at, since a misread revision would drop Hits the client needs.
    /// </summary>
    private static bool TryParseSince(string? since, out long? revision)
    {
        revision = null;
        if (since is null)
        {
            return true;
        }

        if (long.TryParse(since, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            revision = value;
            return true;
        }

        return false;
    }

    private static IResult BadSince() =>
        Problem(
            StatusCodes.Status400BadRequest,
            "since must be a revision this service sent: a whole number, 0 or more."
        );

    private static IResult NoSession() =>
        Problem(StatusCodes.Status404NotFound, "No Watch Session for this video.");

    private static IResult Problem(int statusCode, string detail) =>
        Results.Json(new ErrorResponse(detail), statusCode: statusCode);
}
