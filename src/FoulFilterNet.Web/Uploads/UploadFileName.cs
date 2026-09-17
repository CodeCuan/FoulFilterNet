using System.Text.RegularExpressions;

namespace FoulFilterNet.Web.Uploads;

/// <summary>
/// Turns whatever a browser sent into a name that is safe to put on disk and,
/// more importantly, safe to interpolate into an FFmpeg filtergraph. Colons,
/// brackets and quotes all mean something to FFmpeg's filter parser, so a file
/// called <c>book: "chapter [1]".mp3</c> would make the render fail with a
/// syntax error a long way from the cause. Sanitizing at the upload edge is why
/// nothing downstream has to quote anything.
/// </summary>
public static partial class UploadFileName
{
    /// <summary>What the pipeline can actually process. Lower case, dot included.</summary>
    public static IReadOnlySet<string> AllowedExtensions { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ".mp3",
            ".wav",
            ".m4a",
            ".m4b",
            ".flac",
            ".ogg",
            ".opus",
            ".wma",
            ".mp4",
            ".mkv",
            ".mov",
            ".avi",
            ".webm",
        };

    /// <summary>The name used when nothing usable survives sanitizing.</summary>
    public const string Fallback = "file";

    /// <summary>
    /// Base name only, extension lower-cased, <c>:</c> rewritten as <c> -</c>,
    /// brackets and quotes removed, surrounding whitespace and dots trimmed.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        var (stem, extension) = Split(BaseName(fileName));

        stem = stem.Replace(":", " -", StringComparison.Ordinal);
        stem = Unsafe().Replace(stem, string.Empty).Trim().Trim('.');

        return (stem.Length == 0 ? Fallback : stem) + extension.ToLowerInvariant();
    }

    /// <summary>Whether this name's extension is one the pipeline can open.</summary>
    public static bool IsAllowedExtension(string? fileName) =>
        AllowedExtensions.Contains(Split(BaseName(fileName)).Extension.ToLowerInvariant());

    /// <summary>
    /// The last path segment, splitting on both separators regardless of the
    /// host OS. The Python used <c>os.path.basename</c>, which on Linux leaves a
    /// Windows path intact - and Windows clients are the ones sending them.
    /// </summary>
    private static string BaseName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        var cut = fileName.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? fileName : fileName[(cut + 1)..];
    }

    /// <summary>
    /// Python's <c>os.path.splitext</c>, which differs from
    /// <see cref="Path.GetExtension(string)"/> on names that are all dots or
    /// that start with one: <c>.mp3</c> is a hidden file with no extension, and
    /// <c>..</c> splits to itself. Reproduced so the allow-list decides the same
    /// way the Python did.
    /// </summary>
    private static (string Stem, string Extension) Split(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return (name, string.Empty);
        }

        var firstNonDot = 0;
        while (firstNonDot < dot && name[firstNonDot] == '.')
        {
            firstNonDot++;
        }

        return firstNonDot < dot ? (name[..dot], name[dot..]) : (name, string.Empty);
    }

    [GeneratedRegex("[\\[\\]\"']")]
    private static partial Regex Unsafe();
}
