namespace FoulFilterNet.Web;

/// <summary>
/// Where the service keeps things, and how much it will accept. Bound from the
/// <c>Storage</c> section of configuration; the defaults are the Python's
/// (<c>DATA_DIR=/data</c>, <c>MAX_UPLOAD_MB=4096</c>).
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

    /// <summary>Root for everything the service writes.</summary>
    public string DataDirectory { get; set; } = "/data";

    /// <summary>Overrides the default location of the transcript cache.</summary>
    public string? TranscriptDirectory { get; set; }

    /// <summary>The phrase list every job is matched against.</summary>
    public string BadWordsPath { get; set; } = "/data/bad_words.txt";

    /// <summary>Per-file upload cap, in megabytes.</summary>
    public int MaxUploadMegabytes { get; set; } = 4096;

    /// <summary>Incoming media, one file per job. Wiped at startup.</summary>
    public string UploadDirectory => Path.Combine(DataDirectory, "uploads");

    /// <summary>Censored results, downloadable until the container is replaced.</summary>
    public string OutputDirectory => Path.Combine(DataDirectory, "outputs");

    /// <summary>Per-job working files. Wiped at startup.</summary>
    public string ScratchDirectory => Path.Combine(DataDirectory, "scratch");

    /// <summary>The transcript cache. Survives restarts.</summary>
    public string ResolvedTranscriptDirectory => string.IsNullOrWhiteSpace(TranscriptDirectory)
        ? Path.Combine(DataDirectory, "transcripts")
        : TranscriptDirectory;

    /// <summary><see cref="MaxUploadMegabytes"/> as the byte count the writer enforces.</summary>
    public long MaxUploadBytes => (long)MaxUploadMegabytes * (1L << 20);
}
