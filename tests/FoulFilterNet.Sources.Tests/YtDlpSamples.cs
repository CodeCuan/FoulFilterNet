using System.Text.Json;

namespace FoulFilterNet.Sources.Tests;

/// <summary>
/// What yt-dlp really wrote, captured on 2026-09-18 from yt-dlp 2026.08.19
/// (the winget Windows executable) with Deno 2.9.6, running exactly the
/// argument list <see cref="YtDlpArguments.ForFetch"/> builds. Standard output
/// and standard error were captured separately; both end lines with a bare
/// <c>\n</c>. Only the scratch directory in <c>filepath</c> was replaced, by
/// <see cref="AudioPathPlaceholder"/>, so a fake runner can point it at a
/// directory that exists. Samples that could not be reproduced on demand are
/// marked as written by hand, in yt-dlp's format.
/// </summary>
internal static class YtDlpSamples
{
    /// <summary>Stands in for the downloaded file's full path, JSON-escaped on substitution.</summary>
    public const string AudioPathPlaceholder = "{AUDIO_PATH}";

    /// <summary>
    /// <c>jNQXAC9IVRw</c> ("Me at the zoo", 19 s). With <c>--print</c> yt-dlp is
    /// quiet, so a clean run writes nothing at all to standard error.
    /// </summary>
    public const string SuccessOutput = """
        FFN-INFO {"id": "jNQXAC9IVRw", "title": "Me at the zoo", "duration": 19, "live_status": "not_live"}
        FFN-FILE {"filepath": "{AUDIO_PATH}", "ext": "webm", "format_id": "251", "acodec": "opus"}

        """;

    /// <summary>
    /// <c>9bZkp7q19f0</c>. The <c>j</c> conversion escapes everything outside
    /// ASCII, which is why the title survives the Windows console code page:
    /// the same title printed without <c>j</c> came back as mojibake.
    /// </summary>
    public const string UnicodeTitleOutput = """
        FFN-INFO {"id": "9bZkp7q19f0", "title": "PSY - GANGNAM STYLE(강남스타일) M/V", "duration": 252, "live_status": "not_live"}
        FFN-FILE {"filepath": "{AUDIO_PATH}", "ext": "webm", "format_id": "251", "acodec": "opus"}

        """;

    /// <summary>The Gangnam Style title as it should read once decoded.</summary>
    public const string UnicodeTitle = "PSY - GANGNAM STYLE(강남스타일) M/V";

    /// <summary>
    /// <c>3PFJ9SETS4M</c>, a 24/7 livestream. The match filter skipped it: exit
    /// code 0, nothing on standard error, and no <c>FFN-FILE</c> line. The info
    /// line has no <c>duration</c> key at all (a live stream has none), and the
    /// title carries an emoji as a UTF-16 surrogate pair.
    /// </summary>
    public const string LiveSkippedOutput = """
        FFN-INFO {"id": "3PFJ9SETS4M", "title": "lofi house radio 🌅  lounge music to vibe/chill to 2026-09-18 19:15", "live_status": "is_live"}

        """;

    /// <summary>The live title decoded.</summary>
    public const string LiveTitle =
        "lofi house radio 🌅  lounge music to vibe/chill to 2026-09-18 19:15";

    /// <summary>
    /// The success run again with Deno removed from <c>PATH</c>: the same
    /// standard output and exit code 0, plus this warning.
    /// </summary>
    public const string MissingJsRuntimeWarning = """
        WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default; to use another runtime add  --js-runtimes RUNTIME[:PATH]  to your command/config. YouTube extraction without a JS runtime has been deprecated, and some formats may be missing. See  https://github.com/yt-dlp/yt-dlp/wiki/EJS  for details on installing one

        """;

    /// <summary><c>aaaaaaaaaaa</c>, an ID nobody has. Exit code 1.</summary>
    public const string NonexistentVideoError = """
        ERROR: [youtube] aaaaaaaaaaa: This video is unavailable

        """;

    /// <summary>
    /// <c>jfKfPfyJRdk</c>, a former Lofi Girl stream whose recording is gone.
    /// Exit code 1.
    /// </summary>
    public const string LiveRecordingGoneError = """
        ERROR: [youtube] jfKfPfyJRdk: This live stream recording is not available.

        """;

    /// <summary><c>v03RjDNwG1o</c>, a scheduled NASA stream. Exit code 1.</summary>
    public const string UpcomingError = """
        ERROR: [youtube] v03RjDNwG1o: This live event will begin in 27 hours.

        """;

    /// <summary>
    /// <c>6kLq3WMV1nU</c>, age-restricted, signed out. Exit code 1. (Another
    /// age-limited video, <c>HtVdAasjOgU</c>, downloaded fine: the gate is
    /// YouTube's call per video, not ours.)
    /// </summary>
    public const string AgeRestrictedError = """
        ERROR: [youtube] 6kLq3WMV1nU: Sign in to confirm your age. Use --cookies-from-browser or --cookies for the authentication. See  https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp  for how to manually pass cookies. Also see  https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies  for tips on effectively exporting YouTube cookies

        """;

    /// <summary>
    /// A connection refused, provoked with <c>--proxy http://127.0.0.1:9</c>.
    /// The warning comes first and the error last. Exit code 1.
    /// </summary>
    public const string ConnectionRefusedError = """
        WARNING: [youtube] jNQXAC9IVRw: Unable to download webpage: ('Unable to connect to proxy', NewConnectionError("HTTPSConnection(host='127.0.0.1', port=9): Failed to establish a new connection: [WinError 10061] No connection could be made because the target machine actively refused it")) (caused by ProxyError('(\'Unable to connect to proxy\', NewConnectionError("HTTPSConnection(host=\'127.0.0.1\', port=9): Failed to establish a new connection: [WinError 10061] No connection could be made because the target machine actively refused it"))')); please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. Confirm you are on the latest version using  yt-dlp -U
        ERROR: [youtube] jNQXAC9IVRw: Unable to download API page: ('Unable to connect to proxy', NewConnectionError("HTTPSConnection(host='127.0.0.1', port=9): Failed to establish a new connection: [WinError 10061] No connection could be made because the target machine actively refused it")) (caused by ProxyError('(\'Unable to connect to proxy\', NewConnectionError("HTTPSConnection(host=\'127.0.0.1\', port=9): Failed to establish a new connection: [WinError 10061] No connection could be made because the target machine actively refused it"))')); please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. Confirm you are on the latest version using  yt-dlp -U

        """;

    /// <summary>
    /// A name that does not resolve (<c>--proxy http://no-such-host.invalid:8080</c>),
    /// retried three times before the error; the second round of retries is
    /// trimmed. Exit code 1.
    /// </summary>
    public const string NameResolutionError = """
        WARNING: [youtube] ('Unable to connect to proxy', NameResolutionError("HTTPSConnection(host='no-such-host.invalid', port=8080): Failed to resolve 'no-such-host.invalid' ([Errno 11001] getaddrinfo failed)")). Retrying (1/3)...
        WARNING: [youtube] ('Unable to connect to proxy', NameResolutionError("HTTPSConnection(host='no-such-host.invalid', port=8080): Failed to resolve 'no-such-host.invalid' ([Errno 11001] getaddrinfo failed)")). Retrying (2/3)...
        WARNING: [youtube] ('Unable to connect to proxy', NameResolutionError("HTTPSConnection(host='no-such-host.invalid', port=8080): Failed to resolve 'no-such-host.invalid' ([Errno 11001] getaddrinfo failed)")). Retrying (3/3)...
        WARNING: [youtube] jNQXAC9IVRw: Unable to download webpage: ('Unable to connect to proxy', NameResolutionError("HTTPSConnection(host='no-such-host.invalid', port=8080): Failed to resolve 'no-such-host.invalid' ([Errno 11001] getaddrinfo failed)")) (caused by ProxyError('(\'Unable to connect to proxy\', NameResolutionError("HTTPSConnection(host=\'no-such-host.invalid\', port=8080): Failed to resolve \'no-such-host.invalid\' ([Errno 11001] getaddrinfo failed)"))')); please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. Confirm you are on the latest version using  yt-dlp -U. Giving up after 3 retries
        ERROR: [youtube] jNQXAC9IVRw: Unable to download API page: ('Unable to connect to proxy', NameResolutionError("HTTPSConnection(host='no-such-host.invalid', port=8080): Failed to resolve 'no-such-host.invalid' ([Errno 11001] getaddrinfo failed)")) (caused by ProxyError('(\'Unable to connect to proxy\', NameResolutionError("HTTPSConnection(host=\'no-such-host.invalid\', port=8080): Failed to resolve \'no-such-host.invalid\' ([Errno 11001] getaddrinfo failed)"))')); please report this issue on  https://github.com/yt-dlp/yt-dlp/issues?q= , filling out the appropriate issue template. Confirm you are on the latest version using  yt-dlp -U

        """;

    /// <summary>
    /// No format matched (provoked with <c>-f "bestaudio[abr&gt;100000]"</c>).
    /// This is what a missing JavaScript runtime turns into when YouTube hides
    /// the formats. Exit code 1.
    /// </summary>
    public const string NoFormatError = """
        ERROR: [youtube] jNQXAC9IVRw: Requested format is not available. Use --list-formats for a list of available formats

        """;

    /// <summary>An unknown option. Exit code 2: yt-dlp is too old for an argument we pass.</summary>
    public const string UsageError = """
        Usage: yt-dlp.exe [OPTIONS] URL [URL...]

        yt-dlp.exe: error: no such option: --bogus-option

        """;

    /// <summary>A truncated ID given as a URL (VideoRef never builds one). Exit code 1.</summary>
    public const string TruncatedIdError = """
        ERROR: [youtube:truncated_id] jNQXAC9IVR: Incomplete YouTube ID jNQXAC9IVR. URL https://www.youtube.com/watch?v=jNQXAC9IVR looks truncated.

        """;

    /// <summary>Written by hand in yt-dlp's format: no private video could be found to capture.</summary>
    public const string PrivateVideoError = """
        ERROR: [youtube] abcdefghijk: Private video. Sign in if you've been granted access to this video. Use --cookies-from-browser or --cookies for the authentication.

        """;

    /// <summary>Written by hand in yt-dlp's format: YouTube's bot check (a curly apostrophe).</summary>
    public const string BotCheckError = """
        ERROR: [youtube] abcdefghijk: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.

        """;

    /// <summary>Written by hand in yt-dlp's format.</summary>
    public const string TerminatedAccountError = """
        ERROR: [youtube] abcdefghijk: Video unavailable. This video is no longer available because the YouTube account associated with this video has been terminated.

        """;

    /// <summary>Written by hand in yt-dlp's format.</summary>
    public const string RemovedByUploaderError = """
        ERROR: [youtube] abcdefghijk: Video unavailable. This video has been removed by the uploader

        """;

    /// <summary>Written by hand in yt-dlp's format.</summary>
    public const string GeoBlockedError = """
        ERROR: [youtube] abcdefghijk: Video unavailable. The uploader has not made this video available in your country

        """;

    /// <summary>Written by hand in yt-dlp's format.</summary>
    public const string MembersOnlyError = """
        ERROR: [youtube] abcdefghijk: Join this channel to get access to members-only content like this video, and other exclusive perks.

        """;

    /// <summary>Written by hand in yt-dlp's format: a premiere that has not started.</summary>
    public const string PremiereError = """
        ERROR: [youtube] abcdefghijk: Premieres in 3 hours

        """;

    /// <summary>Written by hand in yt-dlp's format: the connection dropped mid-download.</summary>
    public const string DownloadTimedOutError = """
        ERROR: unable to download video data: The read operation timed out

        """;

    /// <summary>
    /// <c>yt-dlp --ignore-config --version</c>: the version and a newline,
    /// nothing else.
    /// </summary>
    public const string YtDlpVersionOutput = "2026.08.19\n";

    /// <summary><c>deno --version</c>.</summary>
    public const string DenoVersionOutput = """
        deno 2.9.6 (stable, release, x86_64-pc-windows-msvc)
        v8 15.0.245.2-rusty
        typescript 6.0.3

        """;

    /// <summary>A stdout sample with its placeholder pointed at a real path.</summary>
    public static string WithAudioPath(string output, string audioPath)
    {
        // JsonSerializer escapes the backslashes of a Windows path exactly as
        // yt-dlp's JSON does; the surrounding quotes come from the sample.
        var escaped = JsonSerializer.Serialize(audioPath);
        return output.Replace(AudioPathPlaceholder, escaped[1..^1], StringComparison.Ordinal);
    }
}
