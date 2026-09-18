namespace FoulFilterNet.Sources;

/// <summary>
/// The yt-dlp command lines, built as argument lists and never as a shell
/// string. Pure, so every argument can be asserted without yt-dlp installed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing from the browser reaches yt-dlp except inside a URL we built.</b>
/// The video goes in as <see cref="VideoRef.WatchUrl"/>, last, after <c>--</c>.
/// yt-dlp parses its options optparse-style and honours <c>--</c> as the end of
/// options (checked against 2026.08.19: <c>yt-dlp -- --version</c> answers
/// "'--version' is not a valid URL"). The URL starts with <c>https://</c>
/// anyway, but an ID may start with <c>-</c> (W03), so the separator is kept as
/// a second, independent guard.
/// </para>
/// <para>
/// <c>--ignore-config</c> comes first so that no system, user or portable
/// configuration file can add options - an <c>--exec</c> there would run on
/// every fetch.
/// </para>
/// </remarks>
public static class YtDlpArguments
{
    /// <summary>
    /// The downloaded file's name without its extension. Fixed rather than
    /// built from the title, so no title can shape a path.
    /// </summary>
    public const string AudioFileStem = "audio";

    /// <summary>Starts the standard output line that carries the video's metadata.</summary>
    public const string InfoMarker = "FFN-INFO";

    /// <summary>Starts the standard output line that carries the downloaded file.</summary>
    public const string FileMarker = "FFN-FILE";

    /// <summary>
    /// The one call that reports the metadata and downloads the audio (ADR-0007).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>--format bestaudio</c>: audio only, about 1 MB a minute.</item>
    /// <item><c>--match-filters !is_live</c>: a livestream is skipped inside the
    /// same call. yt-dlp then exits 0 having printed the info line and no file
    /// line, which is how a skip is recognised.</item>
    /// <item><c>--print pre_process:…</c> runs <em>before</em> the match
    /// filter, so a skipped livestream still reports its <c>live_status</c>;
    /// <c>--print after_move:…</c> only once the file is in its final place.
    /// <c>--print</c> implies quiet, so standard error carries only warnings and
    /// errors, and <c>--no-simulate</c> keeps it downloading.</item>
    /// <item>Both lines use the <c>j</c> conversion, which escapes everything
    /// outside ASCII. Printed plainly, a title in Korean arrived as mojibake
    /// through the Windows console code page; as JSON it decodes exactly.</item>
    /// <item><c>--paths</c> carries the directory and <c>--output</c> only the
    /// file name, because the output template is expanded: a <c>%</c> in a
    /// user's temp path would be read as a field.</item>
    /// <item><c>--color never</c> keeps escape codes out of the diagnostics we
    /// parse. Warnings are kept: the missing-runtime warning is one.</item>
    /// </list>
    /// </remarks>
    /// <param name="video">The video.</param>
    /// <param name="directory">Where the audio goes.</param>
    /// <param name="denoPath">Deno's location, when not left to yt-dlp's own search.</param>
    public static IReadOnlyList<string> ForFetch(
        VideoRef video,
        string directory,
        string? denoPath = null
    )
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var arguments = new List<string> { "--ignore-config", "--no-playlist" };

        if (!string.IsNullOrWhiteSpace(denoPath))
        {
            arguments.Add("--js-runtimes");
            arguments.Add($"deno:{denoPath}");
        }

        arguments.AddRange([
            "--format",
            "bestaudio",
            "--match-filters",
            "!is_live",
            "--no-progress",
            "--no-simulate",
            "--color",
            "never",
            "--paths",
            directory,
            "--output",
            $"{AudioFileStem}.%(ext)s",
            "--print",
            $"pre_process:{InfoMarker} %(.{{id,title,duration,live_status}})j",
            "--print",
            $"after_move:{FileMarker} %(.{{filepath,ext,format_id,acodec}})j",
            "--",
            video.WatchUrl,
        ]);

        return arguments;
    }

    /// <summary>The availability probe for yt-dlp: print the version and exit.</summary>
    public static IReadOnlyList<string> ForVersion() => ["--ignore-config", "--version"];

    /// <summary>The availability probe for Deno.</summary>
    public static IReadOnlyList<string> ForDenoVersion() => ["--version"];
}
