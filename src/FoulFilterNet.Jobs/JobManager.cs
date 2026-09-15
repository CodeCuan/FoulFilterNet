using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Jobs;

/// <summary>
/// The in-memory job store and the hand-off to the worker.
/// <para>
/// Two things the Python got wrong are fixed here. Finding 6: the queue is a
/// <see cref="Channel{T}"/> the worker blocks on, not a deque it polls every
/// half second. Finding 5: cancellation is a per-job
/// <see cref="CancellationTokenSource"/> created at enqueue time, so it works
/// identically whether the job is queued or running, instead of a set mutated
/// from two threads outside the lock.
/// </para>
/// </summary>
public sealed class JobManager(JobEventFanOut events)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly List<Entry> _order = [];

    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public JobManager()
        : this(new JobEventFanOut())
    {
    }

    /// <summary>What the worker blocks on. One reader only - jobs are sequential.</summary>
    public ChannelReader<string> Reader => _channel.Reader;

    /// <summary>Where every change to a record is broadcast.</summary>
    public JobEventFanOut Events => events;

    /// <summary>
    /// Opens a listener on the jobs. The snapshot is taken under the same lock
    /// that publishes updates, so a client cannot miss a change that lands
    /// between connecting and reading - a race the Python's
    /// snapshot-then-subscribe left open.
    /// </summary>
    public JobSubscription Subscribe()
    {
        lock (_gate)
        {
            return events.Subscribe(_order.ConvertAll(entry => entry.Record));
        }
    }

    /// <summary>
    /// Accepts a job. The caller supplies the id because the upload paths in
    /// <paramref name="request"/> are built from it.
    /// </summary>
    public JobRecord Enqueue(string id, string fileName, JobRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(request);

        var entry = new Entry(new JobRecord { Id = id, FileName = fileName, Request = request });

        lock (_gate)
        {
            if (!_entries.TryAdd(id, entry))
            {
                throw new ArgumentException($"Job '{id}' is already queued.", nameof(id));
            }

            _order.Add(entry);
            events.Publish(entry.Record);
        }

        _channel.Writer.TryWrite(id);
        return entry.Record;
    }

    /// <summary>Every job, oldest first. A point-in-time copy.</summary>
    public IReadOnlyList<JobRecord> Snapshot()
    {
        lock (_gate)
        {
            return _order.ConvertAll(entry => entry.Record);
        }
    }

    public JobRecord? Find(string id)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(id, out var entry) ? entry.Record : null;
        }
    }

    /// <summary>
    /// Cancels a job whether it is queued or running. A queued job is retired
    /// here and skipped when the worker reaches it; a running job observes the
    /// token inside the pipeline and the worker records the outcome.
    /// </summary>
    public JobCancelOutcome Cancel(string id)
    {
        Entry entry;
        bool wasQueued;

        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var found))
            {
                return JobCancelOutcome.NotFound;
            }

            entry = found;
            var status = entry.Record.Status;
            if (status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
            {
                return JobCancelOutcome.AlreadyFinished;
            }

            wasQueued = status == JobStatus.Queued;
            if (wasQueued)
            {
                entry.Record = entry.Record with
                {
                    Status = JobStatus.Cancelled,
                    Detail = CancelledDetail,
                };

                events.Publish(entry.Record);
            }
        }

        // Outside the lock: cancelling runs registered callbacks synchronously.
        entry.Cancellation.Cancel();

        if (wasQueued)
        {
            JobFiles.Cleanup(entry.Record.Request, keepOutput: true);
        }

        return JobCancelOutcome.Cancelled;
    }

    /// <summary>
    /// Claims a job for the worker. False when it was cancelled while queued, or
    /// has otherwise already left the queued state.
    /// </summary>
    public bool TryBeginProcessing(
        string id,
        [NotNullWhen(true)] out JobRecord? record,
        out CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry)
                || entry.Record.Status != JobStatus.Queued
                || entry.Cancellation.IsCancellationRequested)
            {
                record = null;
                cancellationToken = CancellationToken.None;
                return false;
            }

            entry.Record = entry.Record with
            {
                Status = JobStatus.Processing,
                Stage = "queued",
                Progress = 0,
                Detail = "Starting",
            };

            events.Publish(entry.Record);
            record = entry.Record;
            cancellationToken = entry.Cancellation.Token;
            return true;
        }
    }

    /// <summary>Records a progress checkpoint reported by the pipeline.</summary>
    public void Report(string id, JobProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        Update(id, record => record with
        {
            Stage = progress.Stage,
            Progress = progress.Percent,
            Detail = progress.Detail,
        });
    }

    public void MarkCompleted(string id, JobSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        Update(id, record => record with
        {
            Status = JobStatus.Completed,
            Stage = "completed",
            Progress = 100,
            Detail = $"{summary.Hits.Count} hit(s)" + (summary.Rescanned ? " (rescanned)" : string.Empty),
            Hits = summary.Hits,
            DownloadUrl = $"/download/{id}",
        });
    }

    public void MarkCancelled(string id) =>
        Update(id, record => record with { Status = JobStatus.Cancelled, Detail = CancelledDetail });

    public void MarkFailed(string id, string detail) =>
        Update(id, record => record with { Status = JobStatus.Failed, Detail = detail });

    /// <summary>
    /// No further jobs will be accepted, which lets the worker's blocking read
    /// finish rather than be torn down mid-await.
    /// </summary>
    public void CompleteAdding() => _channel.Writer.TryComplete();

    internal const string CancelledDetail = "Cancelled by user";

    /// <summary>
    /// Every mutation goes through here, and publishes while still holding the
    /// lock. Publishing is a non-blocking write into each listener's own bounded
    /// channel, so holding the lock costs nothing and buys subscribers an event
    /// order that matches the order the records actually changed in.
    /// </summary>
    private void Update(string id, Func<JobRecord, JobRecord> change)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.Record = change(entry.Record);
                events.Publish(entry.Record);
            }
        }
    }

    private sealed class Entry(JobRecord record)
    {
        public JobRecord Record { get; set; } = record;

        /// <summary>Live from the moment the job is queued until the process ends.</summary>
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
