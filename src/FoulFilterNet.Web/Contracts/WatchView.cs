using FoulFilterNet.Sources;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Web.Contracts;

/// <summary>
/// The body of <c>POST /watch</c>: the extension's heartbeat, once a second
/// while a watch page is open.
/// </summary>
/// <remarks>
/// Every member is nullable so that a missing field reaches the endpoint as a
/// missing field, and is refused with a sentence saying which, rather than
/// arriving as an empty string or a playhead of 0 that looks like a real one.
/// </remarks>
/// <param name="Provider">Only <c>youtube</c> in V1, in any case.</param>
/// <param name="VideoId">The provider's ID, sent bare, never as a URL.</param>
/// <param name="Position">The playhead (<c>video.currentTime</c>) in seconds: finite and not negative.</param>
public sealed record WatchRequest(string? Provider, string? VideoId, double? Position);

/// <summary>
/// What the extension is told about one Watch Session: a
/// <see cref="WatchSnapshot"/> flattened for the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>The Transcript is not sent.</b> The snapshot carries the stitched words
/// and segments so the session can save them; the extension needs only the
/// Hits and the Coverage, and sending every word once a second would make the
/// transcript most of every poll.
/// </para>
/// <para>
/// <b>Unchanged answers.</b> A client that names the <see cref="Session"/> and
/// <see cref="Revision"/> it already holds (<c>?session=…&amp;since=…</c>) and
/// is still current gets <see cref="Unchanged"/> true and <see cref="Hits"/>
/// null: the only large part of the view is left out, and everything else -
/// state, progress, keeping up - is still sent, because those move between
/// revisions. Both must match: revisions start again in a replacement
/// session, so a bare revision number could be a different session's.
/// </para>
/// </remarks>
public sealed record WatchView
{
    /// <summary><c>youtube-&lt;id&gt;</c>, also the Transcript cache key.</summary>
    public required string Key { get; init; }

    /// <summary>The provider, canonical lower case.</summary>
    public required string Provider { get; init; }

    public required string VideoId { get; init; }

    /// <summary>
    /// Identifies the session; changes when a session is dropped and a new one
    /// made for the video. Echo it back with <c>since</c>.
    /// </summary>
    public required string Session { get; init; }

    /// <summary>One of <see cref="WireNames.Of(WatchState)"/>'s spellings.</summary>
    public required string State { get; init; }

    /// <summary>Why it failed, is unsupported or was cancelled, fit to show; null otherwise.</summary>
    public string? Reason { get; init; }

    /// <summary>For a fetch failure, one of <see cref="WireNames.Of(WebAudioFailure)"/>'s spellings; null otherwise.</summary>
    public string? FailureKind { get; init; }

    public string? Title { get; init; }

    /// <summary>Seconds; null until known.</summary>
    public double? Duration { get; init; }

    /// <summary>Rises with every window heard and every change of Bad Words List, within one session.</summary>
    public long Revision { get; init; }

    /// <summary>The client already holds this session's current revision, so <see cref="Hits"/> is left out.</summary>
    public bool Unchanged { get; init; }

    /// <summary>The Coverage as <c>[from, to]</c> pairs in seconds, ascending and disjoint.</summary>
    public required IReadOnlyList<double[]> Coverage { get; init; }

    /// <summary>
    /// Every Hit so far, padded and merged, in start order - including any
    /// outside the Coverage, which may still change. Null only when
    /// <see cref="Unchanged"/>.
    /// </summary>
    public IReadOnlyList<WatchHitView>? Hits { get; init; }

    public int WindowsDone { get; init; }

    public int WindowsTotal { get; init; }

    /// <summary>0 to 1; 1 once complete.</summary>
    public double Progress { get; init; }

    /// <summary>Seconds of video made safe per second of wall time lately; null until measured.</summary>
    public double? RealtimeFactor { get; init; }

    /// <summary>False only while transcribing slower than real time.</summary>
    public bool KeepingUp { get; init; }

    /// <summary>The Hits came from the Transcript cache, with no transcription.</summary>
    public bool FromCache { get; init; }

    /// <summary>
    /// The view of <paramref name="snapshot"/>, with the Hits left out when
    /// <paramref name="session"/> and <paramref name="since"/> name exactly its
    /// session and revision.
    /// </summary>
    public static WatchView From(WatchSnapshot snapshot, long? since, string? session)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var unchanged =
            since is { } revision
            && revision == snapshot.Revision
            && !string.IsNullOrEmpty(session)
            && string.Equals(session, snapshot.SessionId, StringComparison.Ordinal);

        return new WatchView
        {
            Key = snapshot.Key,
            Provider = snapshot.Video.Provider,
            VideoId = snapshot.Video.Id,
            Session = snapshot.SessionId,
            State = WireNames.Of(snapshot.State),
            Reason = snapshot.Reason,
            FailureKind = snapshot.FailureKind is { } kind ? WireNames.Of(kind) : null,
            Title = snapshot.Title,
            Duration = snapshot.DurationSeconds,
            Revision = snapshot.Revision,
            Unchanged = unchanged,
            Coverage = [.. snapshot.Coverage.Intervals.Select(i => new[] { i.From, i.To })],
            Hits = unchanged
                ? null
                :
                [
                    .. (snapshot.Analysis?.Hits ?? []).Select(hit => new WatchHitView(
                        hit.Start,
                        hit.End,
                        hit.Phrase
                    )),
                ],
            WindowsDone = snapshot.WindowsDone,
            WindowsTotal = snapshot.WindowsTotal,
            Progress = snapshot.Progress,
            RealtimeFactor = snapshot.RealtimeFactor,
            KeepingUp = snapshot.IsKeepingUp,
            FromCache = snapshot.FromCache,
        };
    }
}

/// <summary>
/// One stretch to censor, in seconds on the video's timeline. The phrase is
/// for a debug overlay; the extension censors by time alone.
/// </summary>
public sealed record WatchHitView(double Start, double End, string Phrase);

/// <summary>Whether web video can work here, as <c>/config</c> reports it.</summary>
/// <param name="Available">Both yt-dlp and Deno ran.</param>
/// <param name="YtDlpVersion">yt-dlp's version, or null when it could not be run.</param>
/// <param name="DenoVersion">Deno's version, or null when it could not be run.</param>
/// <param name="Problem">What to tell the user when it is not available; null when it is.</param>
public sealed record WebVideoView(
    bool Available,
    string? YtDlpVersion,
    string? DenoVersion,
    string? Problem
)
{
    public static WebVideoView From(WebVideoAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        return new WebVideoView(
            availability.IsAvailable,
            availability.YtDlpVersion,
            availability.DenoVersion,
            availability.Problem
        );
    }
}

public static partial class WireNames
{
    /// <summary>
    /// A Watch Session's state as the extension switches on it. Written out,
    /// like the Job statuses, so renaming a member cannot change the wire.
    /// </summary>
    public static string Of(WatchState state) =>
        state switch
        {
            WatchState.Queued => "queued",
            WatchState.Fetching => "fetching",
            WatchState.Preparing => "preparing",
            WatchState.Transcribing => "transcribing",
            WatchState.Complete => "complete",
            WatchState.Failed => "failed",
            WatchState.Unsupported => "unsupported",
            WatchState.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

    /// <summary>
    /// Why the audio could not be fetched, in snake case, so the extension can
    /// advise (install yt-dlp or Deno, sign-in required) rather than just fail.
    /// </summary>
    public static string Of(WebAudioFailure kind) =>
        kind switch
        {
            WebAudioFailure.Failed => "failed",
            WebAudioFailure.NotInstalled => "not_installed",
            WebAudioFailure.JsRuntimeMissing => "js_runtime_missing",
            WebAudioFailure.Unavailable => "unavailable",
            WebAudioFailure.SignInRequired => "sign_in_required",
            WebAudioFailure.Unsupported => "unsupported",
            WebAudioFailure.Network => "network",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
