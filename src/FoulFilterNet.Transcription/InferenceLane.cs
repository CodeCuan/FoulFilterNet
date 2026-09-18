namespace FoulFilterNet.Transcription;

/// <summary>
/// Who is waiting for the GPU, which decides who gets it next.
/// </summary>
/// <remarks>
/// Two levels, because there are two kinds of work and one of them has someone
/// watching: a Watch Session's window may be the only thing between a viewer
/// and the video, while a Job's window is one of hundreds in an audiobook
/// nobody is waiting on second by second.
/// </remarks>
public enum InferencePriority
{
    /// <summary>A batch Job's window. Waits behind every waiting High one.</summary>
    Normal = 0,

    /// <summary>A Watch Session's window: someone is holding playback for it.</summary>
    High = 1,
}

/// <summary>
/// The GPU as a one-lane road: window inference goes through one at a time,
/// waiting High requests are let in before waiting Normal ones, and requests of
/// the same priority go first come, first served.
/// </summary>
/// <remarks>
/// <para>
/// Batch Jobs and Watch Sessions share one engine and one card. Without an
/// order, a viewer who opens a video while an audiobook is transcribing would
/// wait behind whatever the Job had queued. With it, priority is decided at
/// every window boundary - about 0.7 s apart on the RTX 3080 Ti (ADR-0007) -
/// so a running Job delays a viewer by at most the one window already on the
/// GPU. Nothing is pre-empted mid-window: whisper.cpp cannot be paused, and a
/// window is short enough that it does not need to be.
/// </para>
/// <para>
/// It also makes inference strictly one at a time, whatever whisper.cpp would
/// tolerate. Each processor has its own whisper state, so two processors on
/// one factory can run at once, but on one card they would only split it, with
/// twice the working memory, and neither would finish sooner.
/// </para>
/// <para>
/// A request cancelled while it waits leaves the queue at once and is never
/// given the lane - nobody would be there to hand it back. Cancelling a request
/// that already holds the lane is the holder's business: its lease stays good
/// until it is disposed, because the work it guards may still be on the GPU.
/// </para>
/// <para>
/// A plain <see cref="SemaphoreSlim"/> would serialise but not order; this is
/// the smallest thing that does both. Everything happens under one lock, and
/// waiters are completed outside it, asynchronously, so no caller's
/// continuation ever runs while the lane's state is half updated.
/// </para>
/// </remarks>
public sealed class InferenceLane
{
    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _high = new();
    private readonly LinkedList<Waiter> _normal = new();
    private bool _held;

    /// <summary>Whether a request currently holds the lane.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_lock)
            {
                return _held;
            }
        }
    }

    /// <summary>How many requests are queued for the lane, of either priority.</summary>
    public int Waiting
    {
        get
        {
            lock (_lock)
            {
                return _high.Count + _normal.Count;
            }
        }
    }

    /// <summary>
    /// Wait for the lane. Completes at once when it is free and nobody is
    /// queued; otherwise when every request ahead of this one - every waiting
    /// High request, then earlier ones of the same priority - has left.
    /// Dispose the lease to leave.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the lane was
    /// handed over; the request has left the queue.
    /// </exception>
    public Task<Lease> EnterAsync(InferencePriority priority, CancellationToken cancellationToken)
    {
        var queue = QueueFor(priority);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<Lease>(cancellationToken);
        }

        Waiter waiter;
        lock (_lock)
        {
            if (!_held)
            {
                _held = true;
                return Task.FromResult(new Lease(this));
            }

            waiter = new Waiter();
            waiter.Node = queue.AddLast(waiter);
        }

        return cancellationToken.CanBeCanceled
            ? WaitAsync(waiter, cancellationToken)
            : waiter.Admitted.Task;
    }

    /// <summary>
    /// Wait with the token watched. The registration belongs to this call and
    /// ends with it, whichever way the wait ends, so the lane never has to
    /// touch it.
    /// </summary>
    private async Task<Lease> WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state =>
            {
                var (lane, waiter, token) = ((InferenceLane, Waiter, CancellationToken))state!;
                lane.Abandon(waiter, token);
            },
            (this, waiter, cancellationToken)
        );

        return await waiter.Admitted.Task.ConfigureAwait(false);
    }

    private LinkedList<Waiter> QueueFor(InferencePriority priority) =>
        priority switch
        {
            InferencePriority.High => _high,
            InferencePriority.Normal => _normal,
            _ => throw new ArgumentOutOfRangeException(
                nameof(priority),
                priority,
                "Only Normal and High exist."
            ),
        };

    /// <summary>
    /// A waiter whose token was cancelled leaves the queue - unless the lane was
    /// already handed to it, in which case it is too late and the lease stands.
    /// </summary>
    private void Abandon(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (waiter.Node?.List is not { } queue)
            {
                return;
            }

            queue.Remove(waiter.Node);
            waiter.Node = null;
        }

        waiter.Admitted.TrySetCanceled(cancellationToken);
    }

    /// <summary>Hand the lane to the next waiter, or free it.</summary>
    private void Leave()
    {
        Waiter? next;
        lock (_lock)
        {
            var queue =
                _high.Count > 0 ? _high
                : _normal.Count > 0 ? _normal
                : null;
            if (queue is null)
            {
                _held = false;
                return;
            }

            next = queue.First!.Value;
            queue.RemoveFirst();
            next.Node = null;
        }

        // Outside the lock, and asynchronously (see Waiter): the admitted
        // caller's code never runs inside the lane's bookkeeping.
        next.Admitted.TrySetResult(new Lease(this));
    }

    /// <summary>The right to use the GPU for one window. Dispose it to leave.</summary>
    public sealed class Lease : IDisposable
    {
        private InferenceLane? _lane;

        internal Lease(InferenceLane lane) => _lane = lane;

        /// <summary>Leave the lane. Only the first call does anything.</summary>
        public void Dispose() => Interlocked.Exchange(ref _lane, null)?.Leave();
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<Lease> Admitted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Its place in a queue; null once admitted or abandoned.</summary>
        public LinkedListNode<Waiter>? Node { get; set; }
    }
}
