namespace FoulFilterNet.Sources;

/// <summary>
/// Reading yt-dlp's standard error. yt-dlp exits 1 for a private video, a
/// dropped connection and a livestream that has not started alike, so its
/// message is the only thing that tells them apart.
/// </summary>
/// <remarks>
/// <para>
/// The rules are phrases from real output, captured from yt-dlp 2026.08.19 (the
/// samples live in the tests as <c>YtDlpSamples</c>), matched case-insensitively
/// against the <em>error</em> lines. Warnings are only read when there is no
/// error line, because a warning that retried its way past a network blip must
/// not turn a missing video into a network failure. Anything unrecognised is
/// <see cref="WebAudioFailure.Failed"/> with yt-dlp's sentence as the reason,
/// which is still accurate, just not specific.
/// </para>
/// <para>
/// Order matters, and each rule is checked before the ones it overlaps:
/// "Private video. Sign in if you've been granted access" mentions signing in
/// but is unavailable to this viewer whatever they do; "This live stream
/// recording is not available" mentions live but is gone, not in progress; and
/// "Requested format is not available" is not an unavailable <em>video</em>,
/// so the unavailable phrases are specific rather than a bare "not available".
/// </para>
/// </remarks>
public static class YtDlpDiagnostics
{
    private static readonly string[] UnavailablePhrases =
    [
        "private video",
        "video unavailable",
        "this video is unavailable",
        "this video is not available",
        "live stream recording is not available",
        "has been removed",
        "no longer available",
        "has been terminated",
        "not made this video available in your country",
        "not available in your country",
        "incomplete youtube id",
    ];

    private static readonly string[] SignInPhrases =
    [
        "sign in to confirm your age",
        "age-restricted",
        "inappropriate for some users",
        "not a bot",
        "members-only",
        "join this channel",
    ];

    private static readonly string[] UpcomingPhrases =
    [
        "live event will begin",
        "premieres in",
        "premiere will begin",
        "is_upcoming",
    ];

    private static readonly string[] NetworkPhrases =
    [
        "unable to download webpage",
        "unable to download api page",
        "unable to download video data",
        "unable to connect",
        "failed to establish a new connection",
        "failed to resolve",
        "getaddrinfo failed",
        "name or service not known",
        "timed out",
        "connection refused",
        "connection reset",
        "connection aborted",
        "network is unreachable",
        "remote end closed connection",
    ];

    private const string MissingJsRuntimePhrase = "no supported javascript runtime";

    /// <summary>What kind of failure a run that did not deliver the audio was.</summary>
    public static WebAudioFailure Classify(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        var lines = Lines(standardError);
        var errors = lines.Where(IsErrorLine).ToList();
        var evidence = string.Join('\n', errors.Count > 0 ? errors : lines);

        if (Mentions(evidence, UnavailablePhrases))
        {
            return WebAudioFailure.Unavailable;
        }

        if (Mentions(evidence, UpcomingPhrases))
        {
            return WebAudioFailure.Unsupported;
        }

        if (Mentions(evidence, SignInPhrases))
        {
            return WebAudioFailure.SignInRequired;
        }

        if (Mentions(evidence, NetworkPhrases))
        {
            return WebAudioFailure.Network;
        }

        return WebAudioFailure.Failed;
    }

    /// <summary>
    /// yt-dlp warned that it found no JavaScript runtime. It carries on without
    /// one, so this is the only sign (ADR-0007).
    /// </summary>
    public static bool ReportsMissingJsRuntime(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        return standardError.Contains(MissingJsRuntimePhrase, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// yt-dlp's own sentence for the failure: the last <c>ERROR:</c> line with
    /// its <c>ERROR: [extractor] id:</c> prefix removed; else the last line
    /// containing <c>error:</c> (optparse's usage error); else the last line.
    /// Null when standard error is blank.
    /// </summary>
    public static string? Reason(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        var lines = Lines(standardError);

        if (lines.LastOrDefault(IsErrorLine) is { } error)
        {
            return StripPrefix(error);
        }

        return lines.LastOrDefault(l => l.Contains("error:", StringComparison.OrdinalIgnoreCase))
            ?? lines.LastOrDefault();
    }

    /// <summary>The version <c>yt-dlp --version</c> printed, or null if it printed nothing.</summary>
    public static string? YtDlpVersion(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        return Lines(standardOutput).FirstOrDefault();
    }

    /// <summary>
    /// The version from <c>deno --version</c>, whose first line reads
    /// <c>deno 2.9.6 (stable, release, …)</c>; null if it does not look like Deno.
    /// </summary>
    public static string? DenoVersion(string standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);

        var words = (Lines(standardOutput).FirstOrDefault() ?? string.Empty).Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries
        );

        return words.Length >= 2 && words[0] == "deno" ? words[1] : null;
    }

    private static List<string> Lines(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private static bool IsErrorLine(string line) =>
        line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase);

    private static bool Mentions(string text, string[] phrases) =>
        phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>ERROR: [youtube] id: message</c> → <c>message</c>. The extractor name
    /// can itself contain a colon (<c>[youtube:truncated_id]</c>), so the prefix
    /// is found by its closing bracket, not by the first colon.
    /// </summary>
    private static string StripPrefix(string line)
    {
        var rest = line["ERROR:".Length..].TrimStart();

        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close > 0)
            {
                rest = rest[(close + 1)..].TrimStart();
                var colon = rest.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0 && !rest[..colon].Contains(' ', StringComparison.Ordinal))
                {
                    rest = rest[(colon + 2)..];
                }
            }
        }

        return rest.Trim();
    }
}
