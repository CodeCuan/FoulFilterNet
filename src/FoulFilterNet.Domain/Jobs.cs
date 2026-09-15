namespace FoulFilterNet.Domain;

/// <summary>
/// A progress checkpoint. The percentages are part of the UI contract - the
/// front end renders them directly - so keep them aligned with the stage table
/// in docs/01-python-analysis.md.
/// </summary>
public sealed record JobProgress(string Stage, int Percent, string Detail = "");

/// <summary>Everything the pipeline needs to process one file.</summary>
public sealed record JobRequest
{
    public required string InputPath { get; init; }

    public required string OutputPath { get; init; }

    public required string BadWordsPath { get; init; }

    public required string TranscriptDirectory { get; init; }

    public required string ScratchDirectory { get; init; }

    public CensorMethod CensorMethod { get; init; } = CensorMethod.Silence;

    /// <summary>Write the transcript out as text alongside the output.</summary>
    public bool Debug { get; init; }

    /// <summary>Run a second detection pass with chunk boundaries shifted.</summary>
    public bool Rescan { get; init; }

    /// <summary>When false, report what would be cut without writing an edit.</summary>
    public bool Render { get; init; } = true;
}

/// <summary>What one completed job produced.</summary>
public sealed record JobSummary(
    IReadOnlyList<Hit> Hits,
    int TranscriptWordCount,
    bool UsedCachedTranscript,
    bool Rescanned);

/// <summary>Raised when a job observes that it has been cancelled.</summary>
public sealed class JobCancelledException : Exception
{
    public JobCancelledException()
        : base("Job cancelled.")
    {
    }

    public JobCancelledException(string message)
        : base(message)
    {
    }

    public JobCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
