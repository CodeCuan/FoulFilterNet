namespace FoulFilterNet.Sources;

/// <summary>
/// Why a Web Video's audio could not be fetched. The Watch Session (W09) maps
/// <see cref="Unsupported"/> to its Unsupported state and every other kind to
/// Failed, showing <see cref="WebAudioException.Reason"/>; the kinds exist so
/// the extension can say something more useful than "failed".
/// </summary>
public enum WebAudioFailure
{
    /// <summary>Anything not recognised below. The reason is yt-dlp's own.</summary>
    Failed,

    /// <summary>yt-dlp could not be started: not installed, or not where configured.</summary>
    NotInstalled,

    /// <summary>
    /// yt-dlp failed in a way it gives no specific reason for, and it had warned
    /// that it found no JavaScript runtime. Without one YouTube can hide
    /// formats, so installing Deno is the advice.
    /// </summary>
    JsRuntimeMissing,

    /// <summary>The video does not exist, is private, removed, or blocked in this country.</summary>
    Unavailable,

    /// <summary>
    /// YouTube wants a signed-in viewer: age-restricted, members-only, or its bot
    /// check. This tool never passes cookies, so it cannot fetch these.
    /// </summary>
    SignInRequired,

    /// <summary>A livestream or an upcoming premiere. Out of scope for V1.</summary>
    Unsupported,

    /// <summary>YouTube could not be reached, or the connection failed mid-download.</summary>
    Network,
}

/// <summary>
/// A Web Video's audio could not be fetched. One exception with a
/// <see cref="Kind"/> rather than a hierarchy: every caller switches on the kind,
/// and the kinds are data the snapshot will carry, not control flow.
/// </summary>
public sealed class WebAudioException : Exception
{
    public WebAudioException() { }

    public WebAudioException(string message)
        : base(message) { }

    public WebAudioException(string message, Exception innerException)
        : base(message, innerException) { }

    public WebAudioException(
        VideoRef video,
        WebAudioFailure kind,
        string reason,
        string message,
        int exitCode = -1,
        string standardError = "",
        Exception? innerException = null
    )
        : base(message, innerException)
    {
        Video = video;
        Kind = kind;
        Reason = reason;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    /// <summary>The video that could not be fetched.</summary>
    public VideoRef? Video { get; }

    /// <summary>What kind of failure it was.</summary>
    public WebAudioFailure Kind { get; }

    /// <summary>
    /// The reason in a sentence, fit to show a user: yt-dlp's own error text
    /// with its <c>ERROR: [youtube] &lt;id&gt;:</c> prefix removed, or ours when
    /// yt-dlp gave none.
    /// </summary>
    public string Reason { get; } = string.Empty;

    /// <summary>yt-dlp's exit code, or -1 when it never ran.</summary>
    public int ExitCode { get; } = -1;

    /// <summary>Everything yt-dlp wrote to standard error, for the log.</summary>
    public string StandardError { get; } = string.Empty;

    /// <summary>The video is out of scope rather than broken (a livestream).</summary>
    public bool IsUnsupported => Kind == WebAudioFailure.Unsupported;
}
