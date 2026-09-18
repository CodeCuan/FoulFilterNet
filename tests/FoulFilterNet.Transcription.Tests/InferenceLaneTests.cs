namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// An idle lane is not a queue: the first request walks straight in, without
/// waiting on anything, and leaving hands the lane back empty.
/// </summary>
public sealed class WhenOneRequestUsesAnIdleLane
{
    private readonly InferenceLane _sut = new();
    private readonly Task<InferenceLane.Lease> _entered;

    public WhenOneRequestUsesAnIdleLane()
    {
        _entered = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);

        _entered.ShouldNotBeNull();
    }

    [Fact]
    public void IsAdmittedWithoutWaiting() => _entered.IsCompletedSuccessfully.ShouldBeTrue();

    [Fact]
    public void HoldsTheLane() => _sut.IsHeld.ShouldBeTrue();

    [Fact]
    public void LeavesNobodyWaiting() => _sut.Waiting.ShouldBe(0);

    [Fact]
    public async Task FreesTheLaneOnLeaving()
    {
        (await _entered).Dispose();

        _sut.IsHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task AdmitsTheNextRequestAtOnceAfterwards()
    {
        (await _entered).Dispose();

        var next = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);

        next.IsCompletedSuccessfully.ShouldBeTrue();
        (await next).Dispose();
    }

    [Fact]
    public async Task ToleratesLeavingTwice()
    {
        var lease = await _entered;
        lease.Dispose();
        lease.Dispose();

        _sut.IsHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task AdmitsAHighPriorityRequestJustTheSameWay()
    {
        (await _entered).Dispose();

        using var lease = await _sut.EnterAsync(InferencePriority.High, CancellationToken.None);

        _sut.IsHeld.ShouldBeTrue();
    }
}

/// <summary>
/// The point of the lane: a Watch Session's window that arrives while a batch
/// Job's windows are already waiting goes in first, so a viewer waits for at
/// most the window already on the GPU, not for the rest of an audiobook.
/// </summary>
public sealed class WhenAWatchWindowArrivesBehindWaitingJobWindows
{
    private readonly InferenceLane _sut = new();
    private readonly Task<InferenceLane.Lease> _job1;
    private readonly Task<InferenceLane.Lease> _job2;
    private readonly Task<InferenceLane.Lease> _watch;

    public WhenAWatchWindowArrivesBehindWaitingJobWindows()
    {
        var holder = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;

        _job1 = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);
        _job2 = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);
        _watch = _sut.EnterAsync(InferencePriority.High, CancellationToken.None);

        _sut.Waiting.ShouldBe(3);
        holder.Dispose();
    }

    [Fact]
    public void AdmitsTheWatchWindowNext() => _watch.IsCompletedSuccessfully.ShouldBeTrue();

    [Fact]
    public void KeepsTheEarlierJobWindowWaiting() => _job1.IsCompleted.ShouldBeFalse();

    [Fact]
    public void KeepsTheLaterJobWindowWaitingToo() => _job2.IsCompleted.ShouldBeFalse();

    [Fact]
    public void LeavesBothJobWindowsQueued() => _sut.Waiting.ShouldBe(2);

    [Fact]
    public async Task AdmitsTheEarlierJobWindowOnceTheWatchWindowLeaves()
    {
        (await _watch).Dispose();

        _job1.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task StillKeepsTheLaterJobWindowBehindIt()
    {
        (await _watch).Dispose();

        _job2.IsCompleted.ShouldBeFalse();
    }
}

/// <summary>
/// Every request waits behind a holder and then leaves as soon as it is let in;
/// the order they were let in is the lane's whole policy. Within a priority it
/// is first come, first served, and every waiting High goes before any waiting
/// Normal, however long the Normal has waited.
/// </summary>
public sealed class WhenManyRequestsQueueBehindTheHolder
{
    private readonly List<string> _admitted = [];

    public WhenManyRequestsQueueBehindTheHolder()
    {
        var sut = new InferenceLane();
        var holder = sut.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;

        var queued = new[]
        {
            Queue(sut, "normal-1", InferencePriority.Normal),
            Queue(sut, "high-1", InferencePriority.High),
            Queue(sut, "normal-2", InferencePriority.Normal),
            Queue(sut, "high-2", InferencePriority.High),
            Queue(sut, "normal-3", InferencePriority.Normal),
            Queue(sut, "high-3", InferencePriority.High),
        };

        holder.Dispose();
        Task.WhenAll(queued).Wait(TestContext.Current.CancellationToken);

        _admitted.Count.ShouldBe(6);
    }

    [Fact]
    public void AdmitsEveryHighRequestBeforeAnyNormalOne() =>
        _admitted.Take(3).ShouldAllBe(name => name.StartsWith("high", StringComparison.Ordinal));

    [Fact]
    public void AdmitsHighRequestsInTheOrderTheyArrived() =>
        _admitted.Take(3).ShouldBe(["high-1", "high-2", "high-3"]);

    [Fact]
    public void AdmitsNormalRequestsInTheOrderTheyArrived() =>
        _admitted.Skip(3).ShouldBe(["normal-1", "normal-2", "normal-3"]);

    private async Task Queue(InferenceLane lane, string name, InferencePriority priority)
    {
        // ConfigureAwait(false): the constructor blocks on these, so their
        // continuations must not need the test's synchronization context.
        using var lease = await lane.EnterAsync(priority, CancellationToken.None)
            .ConfigureAwait(false);
        _admitted.Add(name);
    }
}

/// <summary>
/// A High request that arrives while Normal ones are waiting, but only after
/// one of them was already let in, cannot pre-empt it: priority only applies at
/// the boundary between windows.
/// </summary>
public sealed class WhenAWatchWindowArrivesWhileAJobWindowRuns
{
    private readonly InferenceLane _sut = new();
    private readonly Task<InferenceLane.Lease> _watch;

    public WhenAWatchWindowArrivesWhileAJobWindowRuns()
    {
        _ = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;

        _watch = _sut.EnterAsync(InferencePriority.High, CancellationToken.None);
    }

    [Fact]
    public void WaitsForTheRunningJobWindow() => _watch.IsCompleted.ShouldBeFalse();

    [Fact]
    public void IsCountedAsWaiting() => _sut.Waiting.ShouldBe(1);
}

/// <summary>
/// A viewer who closes the tab cancels a window that is still waiting. It must
/// never be given the lane - nobody would ever leave it again - and the requests
/// behind it must not be held up by it.
/// </summary>
public sealed class WhenAWaitingRequestIsCancelled
{
    private readonly InferenceLane _sut = new();
    private readonly InferenceLane.Lease _holder;
    private readonly Task<InferenceLane.Lease> _cancelled;
    private readonly Task<InferenceLane.Lease> _behind;
    private readonly CancellationToken _token;

    public WhenAWaitingRequestIsCancelled()
    {
        using var abandon = new CancellationTokenSource();
        _token = abandon.Token;
        _holder = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;

        _cancelled = _sut.EnterAsync(InferencePriority.High, abandon.Token);
        _behind = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);

        abandon.Cancel();
    }

    [Fact]
    public async Task ThrowsOperationCanceled() =>
        await Should.ThrowAsync<OperationCanceledException>(() => _cancelled);

    [Fact]
    public async Task ReportsTheCallersOwnToken() =>
        (
            await Should.ThrowAsync<OperationCanceledException>(() => _cancelled)
        ).CancellationToken.ShouldBe(_token);

    [Fact]
    public void GivesUpItsPlaceInTheQueueAtOnce() => _sut.Waiting.ShouldBe(1);

    [Fact]
    public void LeavesTheHolderHoldingTheLane() => _sut.IsHeld.ShouldBeTrue();

    [Fact]
    public void PassesTheLaneToTheRequestBehindItWhenTheHolderLeaves()
    {
        _holder.Dispose();

        _behind.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task LeavesTheLaneFreeOnceTheRequestBehindItLeaves()
    {
        _holder.Dispose();
        (await _behind).Dispose();

        _sut.IsHeld.ShouldBeFalse();
    }
}

/// <summary>
/// A request whose token was already cancelled does not queue or take a free
/// lane: it fails before it asks.
/// </summary>
public sealed class WhenARequestArrivesAlreadyCancelled
{
    private readonly InferenceLane _sut = new();
    private readonly Task<InferenceLane.Lease> _entered;

    public WhenARequestArrivesAlreadyCancelled() =>
        _entered = _sut.EnterAsync(InferencePriority.High, new CancellationToken(canceled: true));

    [Fact]
    public async Task ThrowsOperationCanceled() =>
        await Should.ThrowAsync<OperationCanceledException>(() => _entered);

    [Fact]
    public void DoesNotTakeAFreeLane() => _sut.IsHeld.ShouldBeFalse();

    [Fact]
    public void DoesNotQueue() => _sut.Waiting.ShouldBe(0);
}

/// <summary>
/// Cancelling the token of a request that has already been let in is the
/// holder's business, not the lane's: the lease stays good until disposed, so
/// the lane cannot be handed on while a window is still on the GPU.
/// </summary>
public sealed class WhenTheHoldersTokenIsCancelled
{
    private readonly InferenceLane _sut = new();
    private readonly InferenceLane.Lease _holder;
    private readonly Task<InferenceLane.Lease> _next;

    public WhenTheHoldersTokenIsCancelled()
    {
        using var abandon = new CancellationTokenSource();
        _holder = _sut.EnterAsync(InferencePriority.Normal, abandon.Token).Result;
        _next = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);

        abandon.Cancel();
    }

    [Fact]
    public void StillHoldsTheLane() => _sut.IsHeld.ShouldBeTrue();

    [Fact]
    public void KeepsTheNextRequestWaiting() => _next.IsCompleted.ShouldBeFalse();

    [Fact]
    public void HandsTheLaneOnOnceTheHolderLeaves()
    {
        _holder.Dispose();

        _next.IsCompletedSuccessfully.ShouldBeTrue();
    }
}

/// <summary>
/// A window whose inference is cancelled half way - the viewer left, the Job
/// was cancelled - still leaves the lane by its <c>using</c>, and the lane goes
/// on to the next request instead of wedging.
/// </summary>
public sealed class WhenTheHoldersWorkIsCancelled
{
    private readonly InferenceLane _sut = new();
    private readonly Exception? _thrown;
    private readonly Task<InferenceLane.Lease> _next;

    public WhenTheHoldersWorkIsCancelled()
    {
        using var abandon = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = HoldAsync(started, abandon.Token);
        started.Task.Wait(TestContext.Current.CancellationToken);

        _next = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);
        abandon.Cancel();

        _thrown = Record.Exception(() => work.GetAwaiter().GetResult());
    }

    [Fact]
    public void LetsTheHoldersCancellationThrough() =>
        _thrown.ShouldBeAssignableTo<OperationCanceledException>();

    [Fact]
    public void HandsTheLaneToTheNextRequest() => _next.IsCompletedSuccessfully.ShouldBeTrue();

    private async Task HoldAsync(TaskCompletionSource started, CancellationToken cancellationToken)
    {
        using var lease = await _sut.EnterAsync(InferencePriority.High, cancellationToken)
            .ConfigureAwait(false);
        started.SetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// An inference that fails (CUDA out of memory, a corrupt WAV) must not keep
/// the GPU from everybody else.
/// </summary>
public sealed class WhenTheHoldersWorkThrows
{
    private readonly InferenceLane _sut = new();
    private readonly Task<InferenceLane.Lease> _next;
    private readonly Exception? _thrown;

    public WhenTheHoldersWorkThrows()
    {
        var holder = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;
        _next = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);

        _thrown = Record.Exception(Fail);

        void Fail()
        {
            using (holder)
            {
                throw new InvalidOperationException("cuda oom");
            }
        }
    }

    [Fact]
    public void LetsTheFailureThrough() => _thrown.ShouldBeOfType<InvalidOperationException>();

    [Fact]
    public void HandsTheLaneOn() => _next.IsCompletedSuccessfully.ShouldBeTrue();
}

/// <summary>
/// Leaving twice must not let two requests in: a double dispose is a bug in
/// the caller, but it cannot be allowed to put two windows on the GPU at once.
/// </summary>
public sealed class WhenAHolderLeavesTwice
{
    private readonly InferenceLane _sut = new();
    private readonly Task<InferenceLane.Lease> _first;
    private readonly Task<InferenceLane.Lease> _second;

    public WhenAHolderLeavesTwice()
    {
        var holder = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;
        _first = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);
        _second = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None);

        holder.Dispose();
        holder.Dispose();
    }

    [Fact]
    public void AdmitsTheFirstWaiter() => _first.IsCompletedSuccessfully.ShouldBeTrue();

    [Fact]
    public void KeepsTheSecondWaiting() => _second.IsCompleted.ShouldBeFalse();
}

/// <summary>
/// Cancelling a request after it was admitted cannot take it back out of the
/// lane, and cancelling one that has already failed changes nothing.
/// </summary>
public sealed class WhenAnAdmittedWaitersTokenIsCancelledLate
{
    private readonly InferenceLane _sut = new();
    private readonly Task<InferenceLane.Lease> _admitted;

    public WhenAnAdmittedWaitersTokenIsCancelledLate()
    {
        using var late = new CancellationTokenSource();
        var holder = _sut.EnterAsync(InferencePriority.Normal, CancellationToken.None).Result;
        _admitted = _sut.EnterAsync(InferencePriority.Normal, late.Token);

        holder.Dispose();
        late.Cancel();

        // A waiter with a token is completed asynchronously, off this thread.
        _admitted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void StaysAdmitted() => _admitted.IsCompletedSuccessfully.ShouldBeTrue();

    [Fact]
    public void StillHoldsTheLane() => _sut.IsHeld.ShouldBeTrue();

    [Fact]
    public async Task FreesTheLaneWhenItLeaves()
    {
        (await _admitted).Dispose();

        _sut.IsHeld.ShouldBeFalse();
    }
}

/// <summary>
/// Many requests from many threads at once, a seeded mix of priorities and a
/// sprinkling of cancellations: nobody is ever on the GPU at the same time as
/// anybody else, every request that was not cancelled is served, and the lane
/// ends empty.
/// </summary>
public sealed class WhenManyRequestsContendForTheLane
{
    private const int Requests = 400;

    private readonly InferenceLane _sut = new();
    private readonly List<Outcome> _outcomes;
    private int _inside;
    private int _mostInsideAtOnce;

    public WhenManyRequestsContendForTheLane()
    {
        var random = new Random(20260918);
        var plans = Enumerable
            .Range(0, Requests)
            .Select(i => new Plan(
                i,
                random.Next(2) == 0 ? InferencePriority.Normal : InferencePriority.High,
                CancelAfterMs: random.Next(8) == 0 ? random.Next(0, 5) : null,
                Holds: random.Next(3) == 0
            ))
            .ToList();

        _outcomes = [.. Task.WhenAll(plans.Select(p => Task.Run(() => RunAsync(p)))).Result];

        _outcomes.Count.ShouldBe(Requests);
    }

    [Fact]
    public void NeverAdmitsTwoRequestsAtOnce() => _mostInsideAtOnce.ShouldBe(1);

    [Fact]
    public void ServesEveryRequestThatWasNotCancelled() =>
        _outcomes.Where(o => !o.CouldBeCancelled).ShouldAllBe(o => o.Served);

    [Fact]
    public void EndsEveryRequestOneWayOrTheOther() =>
        _outcomes.ShouldAllBe(o => o.Served || o.Cancelled);

    [Fact]
    public void ServesMostOfThem() => _outcomes.Count(o => o.Served).ShouldBeGreaterThan(300);

    [Fact]
    public void EndsWithTheLaneFree() => _sut.IsHeld.ShouldBeFalse();

    [Fact]
    public void EndsWithNobodyWaiting() => _sut.Waiting.ShouldBe(0);

    private async Task<Outcome> RunAsync(Plan plan)
    {
        using var abandon = new CancellationTokenSource();
        if (plan.CancelAfterMs is { } after)
        {
            abandon.CancelAfter(after);
        }

        try
        {
            using var lease = await _sut.EnterAsync(plan.Priority, abandon.Token);
            var inside = Interlocked.Increment(ref _inside);
            InterlockedMax(ref _mostInsideAtOnce, inside);
            if (plan.Holds)
            {
                await Task.Yield();
            }

            Interlocked.Decrement(ref _inside);
            return new Outcome(plan.CancelAfterMs is not null, Served: true, Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(plan.CancelAfterMs is not null, Served: false, Cancelled: true);
        }
    }

    private static void InterlockedMax(ref int location, int value)
    {
        var seen = Volatile.Read(ref location);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref location, value, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }

    private sealed record Plan(
        int Index,
        InferencePriority Priority,
        int? CancelAfterMs,
        bool Holds
    );

    private sealed record Outcome(bool CouldBeCancelled, bool Served, bool Cancelled);
}

/// <summary>
/// Only the two priorities the lane knows about exist; anything else is a
/// programming error, not a third queue.
/// </summary>
public sealed class WhenARequestNamesAnUnknownPriority
{
    private readonly InferenceLane _sut = new();

    [Fact]
    public async Task RefusesIt() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.EnterAsync((InferencePriority)7, CancellationToken.None)
        );

    [Fact]
    public async Task LeavesTheLaneUntouched()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.EnterAsync((InferencePriority)7, CancellationToken.None)
        );

        _sut.IsHeld.ShouldBeFalse();
    }
}
