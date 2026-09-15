namespace FoulFilterNet.Domain.Abstractions;

/// <summary>
/// Persists transcripts by content hash so re-processing a file can skip the
/// most expensive stage entirely (Resume, ADR-0002).
/// </summary>
public interface ITranscriptStore
{
    /// <summary>Content hash of a media file, used as the cache key.</summary>
    Task<string> ComputeHashAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The cached transcript for this digest, or null. A transcript whose schema
    /// version does not match <see cref="Transcript.CurrentVersion"/>, or which
    /// cannot be read, is a miss rather than an error.
    /// </summary>
    Task<Transcript?> FindAsync(string digest, CancellationToken cancellationToken = default);

    Task SaveAsync(Transcript transcript, string baseName, CancellationToken cancellationToken = default);
}

/// <summary>Runs one file end to end.</summary>
public interface IMediaPipeline
{
    /// <summary>
    /// Process one file. <paramref name="progress"/> is reported at each stage
    /// checkpoint; cancellation is observed at the same points.
    /// </summary>
    /// <exception cref="JobCancelledException">The job was cancelled.</exception>
    Task<JobSummary> RunAsync(
        JobRequest request,
        IProgress<JobProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
