namespace FoulFilterNet.Sources;

/// <summary>
/// Where the web video tools live, bound from the <c>Sources</c> section of
/// appsettings (wired into Web by W10). Both are user-installed prerequisites,
/// like FFmpeg, and default to a <c>PATH</c> lookup.
/// </summary>
public sealed class SourcesOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Sources";

    /// <summary>Path to the yt-dlp executable. Resolved from <c>PATH</c> by default.</summary>
    public string YtDlpPath { get; set; } = "yt-dlp";

    /// <summary>
    /// Path to Deno, the JavaScript runtime yt-dlp needs for YouTube. Null (the
    /// default) leaves it to yt-dlp's own search, which is <c>PATH</c>. When set,
    /// it is handed to yt-dlp with <c>--js-runtimes deno:&lt;path&gt;</c> and the
    /// availability probe runs it from there.
    /// </summary>
    public string? DenoPath { get; set; }
}
