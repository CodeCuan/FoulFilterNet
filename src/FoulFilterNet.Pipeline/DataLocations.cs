namespace FoulFilterNet.Pipeline;

/// <summary>
/// Where the service and the CLI keep things when configuration does not say,
/// decided once so the two hosts share one transcript cache.
/// </summary>
/// <remarks>
/// The Python defaulted to <c>/data</c>, which only makes sense inside its
/// container: on native Windows it is the root of the current drive. The default
/// here is a per-user application data directory
/// (<c>%LOCALAPPDATA%\FoulFilterNet</c> on Windows,
/// <c>~/.local/share/FoulFilterNet</c> on Linux), which is absolute, writable
/// without elevation, and the same whichever directory a terminal is in.
/// <c>DATA_DIR</c> or <c>Storage__DataDirectory</c> moves it.
/// </remarks>
public static class DataLocations
{
    /// <summary>The folder under the per-user application data directory.</summary>
    public const string ApplicationFolderName = "FoulFilterNet";

    /// <summary>The data root used when none is configured.</summary>
    public static string DefaultDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        ApplicationFolderName);

    /// <summary>A blank data directory is the default one, not the working directory.</summary>
    public static string DataDirectory(string? dataDirectory) =>
        string.IsNullOrWhiteSpace(dataDirectory) ? DefaultDataDirectory : dataDirectory.Trim();

    /// <summary>The transcript cache: an explicit directory, else <c>transcripts</c> under the data root.</summary>
    public static string TranscriptDirectory(string? dataDirectory, string? transcriptDirectory) =>
        string.IsNullOrWhiteSpace(transcriptDirectory)
            ? Path.Combine(DataDirectory(dataDirectory), "transcripts")
            : transcriptDirectory.Trim();

    /// <summary>The Bad Words List: an explicit path, else <c>bad_words.txt</c> in the data root.</summary>
    public static string BadWordsPath(string? dataDirectory, string? badWordsPath) =>
        string.IsNullOrWhiteSpace(badWordsPath)
            ? Path.Combine(DataDirectory(dataDirectory), "bad_words.txt")
            : badWordsPath.Trim();
}
