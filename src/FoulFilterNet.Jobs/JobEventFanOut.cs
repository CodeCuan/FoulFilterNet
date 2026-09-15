using System.Threading.Channels;

namespace FoulFilterNet.Jobs;

/// <summary>
/// Broadcasts job records to every connected listener - in practice, every open
/// <c>/events</c> stream.
/// <para>
/// There is one worker and one GPU behind it, so nothing a listener does may
/// ever slow the worker down. Each subscriber gets its own bounded channel that
/// drops its oldest buffered event when it fills, which makes publishing a
/// non-blocking write that always succeeds. Dropping the oldest is the right
/// trade here because every event carries a complete record: a listener that
/// falls behind loses intermediate progress percentages, never the outcome.
/// </para>
/// </summary>
public sealed class JobEventFanOut(int capacity = JobEventFanOut.DefaultCapacity)
{
    /// <summary>
    /// Events one listener may fall behind by. Generous, because the common case
    /// is a browser tab that will catch up within milliseconds.
    /// </summary>
    public const int DefaultCapacity = 256;

    private readonly Lock _gate = new();
    private readonly HashSet<JobSubscription> _subscribers = [];

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>
    /// Opens a listener. <paramref name="snapshot"/> is the state at the moment
    /// of connecting; every subsequent change arrives as an update, so a client
    /// that renders the snapshot and then the updates misses nothing.
    /// </summary>
    public JobSubscription Subscribe(IReadOnlyList<JobRecord> snapshot)
    {
        var subscription = new JobSubscription(this, snapshot, capacity);

        lock (_gate)
        {
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    /// <summary>Closes a listener. Doing this twice, or to a stranger, is a no-op.</summary>
    public void Unsubscribe(JobSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        bool removed;
        lock (_gate)
        {
            removed = _subscribers.Remove(subscription);
        }

        if (removed)
        {
            subscription.Close();
        }
    }

    /// <summary>
    /// Hands <paramref name="record"/> to every listener. Never blocks, and never
    /// throws: a listener whose channel has already been closed is simply
    /// dropped.
    /// </summary>
    public void Publish(JobRecord record)
    {
        JobSubscription[] current;
        lock (_gate)
        {
            current = [.. _subscribers];
        }

        List<JobSubscription>? dead = null;
        foreach (var subscriber in current)
        {
            if (!subscriber.TryPublish(record))
            {
                (dead ??= []).Add(subscriber);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach (var subscriber in dead)
        {
            Unsubscribe(subscriber);
        }
    }
}

/// <summary>
/// One listener's view: the jobs as they stood when it connected, then every
/// change since. Disposing it disconnects.
/// </summary>
public sealed class JobSubscription : IDisposable
{
    private readonly JobEventFanOut _fanOut;
    private readonly Channel<JobRecord> _channel;

    internal JobSubscription(JobEventFanOut fanOut, IReadOnlyList<JobRecord> snapshot, int capacity)
    {
        _fanOut = fanOut;
        Snapshot = snapshot;
        _channel = Channel.CreateBounded<JobRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Every job as it stood at the moment this listener connected.</summary>
    public IReadOnlyList<JobRecord> Snapshot { get; }

    /// <summary>Changes since the snapshot, oldest first.</summary>
    public ChannelReader<JobRecord> Updates => _channel.Reader;

    public void Dispose() => _fanOut.Unsubscribe(this);

    internal bool TryPublish(JobRecord record) => _channel.Writer.TryWrite(record);

    internal void Close() => _channel.Writer.TryComplete();
}
