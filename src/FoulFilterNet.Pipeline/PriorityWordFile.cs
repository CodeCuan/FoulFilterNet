using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Pipeline;

/// <summary>
/// Reads the Priority Word List the way both hosts should: from its file when
/// there is one, the built-in list when there is not.
/// </summary>
/// <remarks>
/// A missing file is not an error, even at a configured path: the pass is meant
/// to work out of the box, and most users never write the file. An empty file is
/// read as an empty list, which turns the pass off. Read once, at startup: unlike
/// the Bad Words List it is not re-read on change, because it is baked into the
/// prompted processor's prompt.
/// </remarks>
public static class PriorityWordFile
{
    /// <summary>The list in <paramref name="path"/>, or <see cref="PriorityWordList.Default"/> when there is no such file.</summary>
    public static PriorityWordList Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return File.Exists(path)
            ? PriorityWordList.FromLines(File.ReadAllLines(path))
            : PriorityWordList.Default;
    }

    /// <summary>
    /// The list <paramref name="options"/> asks for: empty when
    /// <see cref="TranscriptionOptions.PriorityPass"/> is off, else read from
    /// <see cref="TranscriptionOptions.PriorityWordsPath"/> or
    /// <c>priority_words.txt</c> under <paramref name="dataDirectory"/>.
    /// </summary>
    public static PriorityWordList ForOptions(TranscriptionOptions options, string? dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.PriorityPass
            ? Read(DataLocations.PriorityWordsPath(dataDirectory, options.PriorityWordsPath))
            : PriorityWordList.FromLines([]);
    }
}
