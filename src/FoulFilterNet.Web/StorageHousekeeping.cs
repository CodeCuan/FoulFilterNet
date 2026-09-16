using Microsoft.Extensions.Options;

namespace FoulFilterNet.Web;

/// <summary>
/// Makes the data directories exist, then throws away what the last run left in
/// the two that belong to jobs rather than to the operator.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0002: a job is ephemeral. The queue does not survive a restart, so the
/// uploads and scratch files of jobs that were queued or running when the
/// process died are orphans - nothing will ever finish them, and nothing will
/// ever delete them either, because the records that named them are gone.
/// </para>
/// <para>
/// Transcripts and outputs are the deliberate exceptions. A transcript costs a
/// GPU pass to produce and is keyed by content hash, so keeping it is what makes
/// re-running a file cheap; an output is the thing the operator came back for.
/// Wiping either would turn a restart from an inconvenience into data loss.
/// </para>
/// <para>
/// This runs as the first hosted service so that it completes before
/// <c>JobWorker</c> can pick anything up.
/// </para>
/// </remarks>
public sealed class StorageHousekeeping(
    IOptions<StorageOptions> options,
    ILogger<StorageHousekeeping> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Prepare(options.Value, logger);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Creates all four data directories, then empties <c>uploads/</c> and
    /// <c>scratch/</c>, leaving <c>outputs/</c> and the transcript cache alone.
    /// </summary>
    public static void Prepare(StorageOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Nothing downstream creates these: an upload opens a file straight into
        // uploads/, and the pipeline writes into a scratch directory it expects
        // to be under one that exists.
        Directory.CreateDirectory(options.UploadDirectory);
        Directory.CreateDirectory(options.OutputDirectory);
        Directory.CreateDirectory(options.ScratchDirectory);
        Directory.CreateDirectory(options.ResolvedTranscriptDirectory);

        Wipe(options.UploadDirectory, logger);
        Wipe(options.ScratchDirectory, logger);
    }

    /// <summary>
    /// Empties one directory without removing it.
    /// </summary>
    /// <remarks>
    /// Scratch holds a directory per job rather than loose files, so both kinds
    /// of entry have to go. Anything that refuses to be deleted - a file a
    /// scanner or a dying process still holds open - is logged and stepped over:
    /// the service starting matters more than the last run's rubbish going away,
    /// which is the trade the Python made by swallowing these too.
    /// </remarks>
    private static void Wipe(string directory, ILogger? logger)
    {
        // Materialised before deleting: enumerating a directory lazily while
        // emptying it is not something to rely on.
        foreach (var entry in Directory.GetFileSystemEntries(directory))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (IOException exception)
            {
                logger?.LogWarning(exception, "Could not clear {Entry} at startup", entry);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger?.LogWarning(exception, "Could not clear {Entry} at startup", entry);
            }
        }
    }
}
