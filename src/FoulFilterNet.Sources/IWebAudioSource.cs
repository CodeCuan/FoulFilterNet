namespace FoulFilterNet.Sources;

/// <summary>
/// Fetches a Web Video's audio for the analysis stream (ADR-0007): the server
/// acquires the audio itself, by video ID, and never from anything the browser
/// sends except a validated <see cref="VideoRef"/>.
/// </summary>
/// <remarks>
/// <para>
/// The plan sketched two calls, <c>ResolveAsync</c> then
/// <c>DownloadAudioAsync</c>. W01 measured that as two yt-dlp processes, each
/// paying 1.2 s to start and about 4 s to read the watch page, so ADR-0007
/// collapsed them: <see cref="FetchAudioAsync"/> is one yt-dlp call that reports
/// the metadata and downloads, and refuses livestreams in the same call.
/// </para>
/// <para>
/// Failures are a <see cref="WebAudioException"/> whose
/// <see cref="WebAudioException.Kind"/> says what went wrong; cancellation is an
/// ordinary <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
public interface IWebAudioSource
{
    /// <summary>
    /// Download the video's best audio-only stream into
    /// <paramref name="directory"/>, and report what it is.
    /// </summary>
    /// <param name="video">The video. Only its canonical watch URL reaches the downloader.</param>
    /// <param name="directory">A scratch directory the source may write into; created if
    /// missing. The audio lands as <c>audio.&lt;ext&gt;</c>, and any earlier
    /// <c>audio.*</c> there is replaced. Other files are left alone.</param>
    /// <param name="cancellationToken">Cancelling kills the download and deletes
    /// what it had written.</param>
    /// <exception cref="WebAudioException">The audio could not be fetched.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    Task<WebAudio> FetchAudioAsync(
        VideoRef video,
        string directory,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Whether web video can work on this machine at all, for <c>GET /config</c>.
    /// Runs nothing but version checks and never throws for a missing tool.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled by the caller.</exception>
    Task<WebVideoAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default
    );
}

/// <summary>A Web Video's audio, downloaded, with what yt-dlp reported about it.</summary>
/// <param name="Video">The video it is the audio of.</param>
/// <param name="Title">The title, decoded (Unicode survives). Empty if yt-dlp gave none.</param>
/// <param name="DurationSeconds">YouTube's duration in whole seconds, or null when
/// it reported none. Informational: the Watch Session plans its windows from the
/// analysis WAV's own duration, which is exact.</param>
/// <param name="AudioPath">Full path of the downloaded file, inside the directory asked for.</param>
/// <param name="Extension">The container, e.g. <c>webm</c> or <c>m4a</c>.</param>
/// <param name="FormatId">YouTube's format id, e.g. <c>251</c>, if reported.</param>
/// <param name="Codec">The audio codec, e.g. <c>opus</c>, if reported.</param>
/// <param name="JsRuntimeMissing">yt-dlp warned that it found no JavaScript runtime.
/// The download still worked this time, but YouTube may hide formats without
/// one, so it is worth surfacing.</param>
public sealed record WebAudio(
    VideoRef Video,
    string Title,
    double? DurationSeconds,
    string AudioPath,
    string Extension,
    string? FormatId,
    string? Codec,
    bool JsRuntimeMissing
);

/// <summary>
/// Whether the tools web video needs are present: yt-dlp, and Deno, the
/// JavaScript runtime current yt-dlp needs for YouTube. Without Deno yt-dlp only
/// warns (ADR-0007), so it has to be checked for explicitly.
/// </summary>
/// <param name="YtDlpVersion">yt-dlp's version, or null when it could not be run.</param>
/// <param name="DenoVersion">Deno's version, or null when it could not be run.</param>
public sealed record WebVideoAvailability(string? YtDlpVersion, string? DenoVersion)
{
    /// <summary>yt-dlp ran and reported a version.</summary>
    public bool YtDlpFound => YtDlpVersion is not null;

    /// <summary>Deno ran and reported a version.</summary>
    public bool JsRuntimeFound => DenoVersion is not null;

    /// <summary>Both are present, so web video can be offered.</summary>
    public bool IsAvailable => YtDlpFound && JsRuntimeFound;

    /// <summary>What to tell the user when it is not available, or null when it is.</summary>
    public string? Problem =>
        (YtDlpFound, JsRuntimeFound) switch
        {
            (false, _) =>
                "yt-dlp could not be run. Install it and put it on PATH, or set Sources:YtDlpPath.",
            (true, false) =>
                "Deno could not be run. yt-dlp needs it for YouTube: install it and put it on PATH, or set Sources:DenoPath.",
            _ => null,
        };
}
