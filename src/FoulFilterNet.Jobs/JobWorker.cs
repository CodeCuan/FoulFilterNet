using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FoulFilterNet.Jobs;

/// <summary>
/// Drains the queue one job at a time. There is a single GPU behind the
/// pipeline, so "one at a time" is a hard requirement rather than a throttle:
/// the loop awaits each job before reading the next id.
/// </summary>
public sealed class JobWorker(JobManager jobs, IMediaPipeline pipeline, ILogger<JobWorker> logger)
    : BackgroundService
{
    /// <summary>A failure message longer than this would flood the UI's status cell.</summary>
    private const int MaxDetailLength = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Finding 6: block on the reader instead of polling every 0.5 s.
            await foreach (var id in jobs.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(id, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down.
        }
    }

    private async Task RunAsync(string id, CancellationToken stoppingToken)
    {
        if (!jobs.TryBeginProcessing(id, out var record, out var jobToken))
        {
            // Cancelled while it sat in the queue.
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(jobToken, stoppingToken);
        var progress = new JobProgressReporter(jobs, id);

        try
        {
            var summary = await pipeline.RunAsync(record.Request, progress, linked.Token);
            jobs.MarkCompleted(id, summary);
        }
        catch (Exception exception)
            when (exception is JobCancelledException or OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Job {JobId} cancelled", id);
            }

            jobs.MarkCancelled(id);
            JobFiles.Cleanup(record.Request, keepOutput: true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Job {JobId} failed", id);
            jobs.MarkFailed(id, Truncate(exception.Message));
            JobFiles.Cleanup(record.Request, keepOutput: false);
        }
    }

    private static string Truncate(string detail) =>
        detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength];

    /// <summary>
    /// Reports straight through to the queue. <see cref="Progress{T}"/> would
    /// hand each checkpoint to the thread pool, which loses the ordering the UI
    /// depends on.
    /// </summary>
    private sealed class JobProgressReporter(JobManager jobs, string id) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => jobs.Report(id, value);
    }
}
