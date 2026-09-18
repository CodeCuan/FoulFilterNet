using FoulFilterNet.Sources;

namespace FoulFilterNet.Watch;

/// <summary>
/// Everything the extension is told about one Watch Session at one moment,
/// frozen: a later window, a new state or an edited Bad Words List produce a
/// new snapshot and leave this one as it was, so it can be serialised or
/// compared at leisure while the session moves on.
/// </summary>
/// <param name="Video">The Web Video.</param>
/// <param name="State">Where the session is.</param>
/// <param name="Reason">
/// Why it is <see cref="WatchState.Failed"/>, <see cref="WatchState.Unsupported"/>
/// or <see cref="WatchState.Cancelled"/>, in a sentence fit to show the user
/// (for a fetch failure, yt-dlp's own words); null otherwise.
/// </param>
/// <param name="FailureKind">
/// What kind of fetch failure it was, when the audio could not be fetched, so
/// the extension can advise (install Deno, sign-in required); null for any
/// other outcome, including failures after the fetch.
/// </param>
/// <param name="Title">The video's title as yt-dlp reported it; null until fetched, when it gave none, and for a cached Transcript.</param>
/// <param name="DurationSeconds">
/// The video's length: yt-dlp's whole-second figure while preparing, then the
/// analysis WAV's exact one. For a cached Transcript it is where the last thing
/// heard ends, since a Transcript does not record the length (see
/// <see cref="WatchProgress.FromTranscript"/>). Null until known.
/// </param>
/// <param name="Analysis">
/// The Hits, Coverage and revision; null until the WAV is open and the windows
/// are planned. Its revision rises with every window and every change of Bad
/// Words List, but starts again if the session is dropped and a new one made.
/// </param>
/// <param name="WindowsDone">Transcription windows finished.</param>
/// <param name="WindowsTotal">Transcription windows planned; 0 until planned.</param>
/// <param name="RealtimeFactor">
/// Seconds of video made safe per second of wall time, over the last few
/// windows (including any wait for the GPU behind other work). Null until a
/// window has finished.
/// </param>
/// <param name="FromCache">The Hits came from the Transcript cache, with no transcription.</param>
public sealed record WatchSnapshot(
    VideoRef Video,
    WatchState State,
    string? Reason,
    WebAudioFailure? FailureKind,
    string? Title,
    double? DurationSeconds,
    HitSnapshot? Analysis,
    int WindowsDone,
    int WindowsTotal,
    double? RealtimeFactor,
    bool FromCache
)
{
    /// <summary>
    /// The <see cref="WatchSession.Id"/> of the session this came from. Its
    /// revisions only mean something within it: a session that is dropped and
    /// made again (a retry after a failure, a cancel, idle expiry) starts its
    /// revisions again, so "I already have revision 3" has to name whose.
    /// Empty only for a snapshot built by hand.
    /// </summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>The video's key, <c>youtube-&lt;id&gt;</c>, which is also its Transcript cache key.</summary>
    public string Key => Video.Key;

    /// <summary>
    /// How far through the windows the session is, from 0 to 1: 1 once
    /// <see cref="WatchState.Complete"/>, 0 until the windows are planned.
    /// </summary>
    public double Progress =>
        State == WatchState.Complete ? 1.0
        : WindowsTotal == 0 ? 0.0
        : (double)WindowsDone / WindowsTotal;

    /// <summary>
    /// False when windows are finishing slower than real time, so a viewer
    /// watching at normal speed would catch up with the analysis: the extension
    /// can then explain a long hold instead of spinning. True until there is a
    /// measurement, and once there is nothing left to transcribe.
    /// </summary>
    public bool IsKeepingUp =>
        State != WatchState.Transcribing || RealtimeFactor is not { } factor || factor >= 1.0;

    /// <summary>The Analysis revision, or 0 before there is one.</summary>
    public long Revision => Analysis?.Revision ?? 0;

    /// <summary>The Analysis Coverage, or nothing before there is one.</summary>
    public Coverage Coverage => Analysis?.Coverage ?? Coverage.Empty;
}
