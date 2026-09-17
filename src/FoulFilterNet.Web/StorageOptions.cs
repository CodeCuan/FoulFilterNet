using FoulFilterNet.Pipeline;

namespace FoulFilterNet.Web;

/// <summary>
/// Where the service keeps things, and how much it will accept. Bound from the
/// <c>Storage</c> section of configuration, which the legacy <c>DATA_DIR</c>,
/// <c>TRANSCRIPT_DIR</c>, <c>BAD_WORDS_PATH</c> and <c>MAX_UPLOAD_MB</c> map onto.
/// <para>
/// The data root defaults to <see cref="DataLocations.DefaultDataDirectory"/>
/// rather than the Python's <c>/data</c>, which on native Windows is the root of
/// the current drive. Blank
/// values mean "the default", so <c>appsettings.json</c> can list every key.
/// </para>
/// <para>
/// ADR-0002 draws the line these paths sit either side of: uploads, outputs and
/// scratch belong to a job and do not outlive the process, while transcripts are
/// a cache worth keeping - which is why the transcript directory is separately
/// configurable.
/// </para>
/// </summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Storage";

    private string _dataDirectory = DataLocations.DefaultDataDirectory;
    private string? _badWordsPath;

    /// <summary>Root for everything the service writes.</summary>
    public string DataDirectory
    {
        get => _dataDirectory;
        set => _dataDirectory = DataLocations.DataDirectory(value);
    }

    /// <summary>Overrides the default location of the transcript cache.</summary>
    public string? TranscriptDirectory { get; set; }

    /// <summary>The phrase list every job is matched against; <c>bad_words.txt</c> in the data root by default.</summary>
    public string BadWordsPath
    {
        get => DataLocations.BadWordsPath(DataDirectory, _badWordsPath);
        set => _badWordsPath = value;
    }

    /// <summary>Per-file upload cap, in megabytes.</summary>
    public int MaxUploadMegabytes { get; set; } = 4096;

    /// <summary>Incoming media, one file per job. Wiped at startup.</summary>
    public string UploadDirectory => Path.Combine(DataDirectory, "uploads");

    /// <summary>Censored results, downloadable until someone deletes them.</summary>
    public string OutputDirectory => Path.Combine(DataDirectory, "outputs");

    /// <summary>Per-job working files. Wiped at startup.</summary>
    public string ScratchDirectory => Path.Combine(DataDirectory, "scratch");

    /// <summary>The transcript cache. Survives restarts.</summary>
    public string ResolvedTranscriptDirectory =>
        DataLocations.TranscriptDirectory(DataDirectory, TranscriptDirectory);

    /// <summary><see cref="MaxUploadMegabytes"/> as the byte count the writer enforces.</summary>
    public long MaxUploadBytes => (long)MaxUploadMegabytes * (1L << 20);
}
