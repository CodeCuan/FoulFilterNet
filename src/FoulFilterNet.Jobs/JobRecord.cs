using FoulFilterNet.Domain;

namespace FoulFilterNet.Jobs;

/// <summary>Where a job is in its life. The names are the UI's status chips.</summary>
public enum JobStatus
{
    /// <summary>Accepted and waiting for the worker.</summary>
    Queued,

    /// <summary>The worker is running it. Only ever one job at a time.</summary>
    Processing,

    /// <summary>Finished; the output is downloadable.</summary>
    Completed,

    /// <summary>The pipeline threw. <see cref="JobRecord.Detail"/> carries why.</summary>
    Failed,

    /// <summary>Cancelled by the user, whether it had started or not.</summary>
    Cancelled,
}

/// <summary>What <see cref="JobManager.Cancel"/> was able to do.</summary>
public enum JobCancelOutcome
{
    /// <summary>No such job - a 404 at the HTTP edge.</summary>
    NotFound,

    /// <summary>The job is now cancelled, or is being torn down.</summary>
    Cancelled,

    /// <summary>
    /// The job had already finished. Not an error: the front end reuses
    /// <c>DELETE /jobs/{id}</c> as "remove this row".
    /// </summary>
    AlreadyFinished,
}

/// <summary>
/// Everything known about one job. Immutable: the queue swaps a whole record
/// under its lock rather than mutating fields, so a snapshot handed to a reader
/// can never be observed half-updated.
/// </summary>
public sealed record JobRecord
{
    public required string Id { get; init; }

    /// <summary>The sanitized upload name, which is what the UI shows.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// What the worker hands the pipeline. Holds filesystem paths, so it must
    /// never be serialized to a client.
    /// </summary>
    public required JobRequest Request { get; init; }

    public JobStatus Status { get; init; } = JobStatus.Queued;

    /// <summary>The pipeline's current stage name, as the progress callback reports it.</summary>
    public string Stage { get; init; } = "queued";

    public int Progress { get; init; }

    public string Detail { get; init; } = string.Empty;

    public IReadOnlyList<Hit> Hits { get; init; } = [];

    /// <summary>Set once the output exists; null at every other point.</summary>
    public string? DownloadUrl { get; init; }
}

/// <summary>The one spelling of a job id, so paths built from it line up.</summary>
public static class JobId
{
    /// <summary>A fresh twelve-character id, matching the Python's <c>uuid4().hex[:12]</c>.</summary>
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}
