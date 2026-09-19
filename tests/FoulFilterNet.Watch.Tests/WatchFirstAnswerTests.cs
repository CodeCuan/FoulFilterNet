using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// W17: the heartbeat that starts a session waits briefly for its Transcript
/// cache lookup, so a cached video's first answer is already complete.
/// </summary>
public sealed class WhenACachedVideoIsFirstHeard : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot _first;

    public WhenACachedVideoIsFirstHeard()
    {
        _harness.Store.Cached = Harness.CachedTranscript(Harness.Video.Key);
        _manager = _harness.Manager();
        _first = _manager
            .HeartbeatAsync(Harness.Video, 0.0, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void AnswersCompleteAtOnce() => _first.State.ShouldBe(WatchState.Complete);

    [Fact]
    public void SaysItCameFromTheCache() => _first.FromCache.ShouldBeTrue();

    [Fact]
    public void CarriesTheHits() =>
        _first.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(HundredSecondVideo.BadWords));

    [Fact]
    public void CoversEverything() => _first.Coverage.IsComplete.ShouldBeTrue();
}

public sealed class WhenAMissIsFirstHeard : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot _first;

    public WhenAMissIsFirstHeard()
    {
        _harness.Source.Hold = new Gate();
        _manager = _harness.Manager();
        _first = _manager
            .HeartbeatAsync(Harness.Video, 0.0, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void AnswersFetchingRatherThanQueued() => _first.State.ShouldBe(WatchState.Fetching);

    [Fact]
    public void IsNotFromTheCache() => _first.FromCache.ShouldBeFalse();
}

public sealed class WhenTheCacheLookupIsSlow : IDisposable
{
    private readonly Harness _harness = new();
    private readonly Gate _lookup = new();
    private readonly WatchSessionManager _manager;
    private readonly Task<WatchSnapshot> _first;
    private readonly bool _answeredBeforeTheWait;
    private readonly bool _answeredJustBeforeTheWaitRanOut;

    public WhenTheCacheLookupIsSlow()
    {
        _harness.Store.Cached = Harness.CachedTranscript(Harness.Video.Key);
        _harness.Store.FindHold = _lookup;
        _manager = _harness.Manager();

        _first = _manager.HeartbeatAsync(Harness.Video, 0.0, TestContext.Current.CancellationToken);
        _lookup.WaitUntilEntered();
        Thread.Sleep(20);
        _answeredBeforeTheWait = _first.IsCompleted;

        _harness.Time.Advance(TimeSpan.FromMilliseconds(499));
        Thread.Sleep(20);
        _answeredJustBeforeTheWaitRanOut = _first.IsCompleted;

        _harness.Time.Advance(TimeSpan.FromMilliseconds(1));
        Waits.On(_first);
    }

    public void Dispose()
    {
        _lookup.Release();
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void WaitsForTheLookup() => _answeredBeforeTheWait.ShouldBeFalse();

    [Fact]
    public void WaitsTheWholeFirstAnswerWait() => _answeredJustBeforeTheWaitRanOut.ShouldBeFalse();

    [Fact]
    public async Task ThenAnswersQueued() => (await _first).State.ShouldBe(WatchState.Queued);

    [Fact]
    public void NeverMakesALaterHeartbeatWait()
    {
        var later = _manager.HeartbeatAsync(
            Harness.Video,
            1.0,
            TestContext.Current.CancellationToken
        );

        later.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public void AnswersCompleteOnceTheLookupEnds()
    {
        _lookup.Release();
        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete);

        _manager.Heartbeat(Harness.Video, 1.0).FromCache.ShouldBeTrue();
    }
}

public sealed class WhenTheFirstAnswerWaitIsZero : IDisposable
{
    private readonly Harness _harness = new();
    private readonly Gate _lookup = new();
    private readonly WatchSessionManager _manager;
    private readonly Task<WatchSnapshot> _first;

    public WhenTheFirstAnswerWaitIsZero()
    {
        _harness.Store.FindHold = _lookup;
        var options = _harness.Options();
        options.FirstAnswerWait = TimeSpan.Zero;
        _manager = _harness.Manager(options);

        _first = _manager.HeartbeatAsync(Harness.Video, 0.0, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        _lookup.Release();
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void AnswersAtOnce() => _first.IsCompletedSuccessfully.ShouldBeTrue();

    [Fact]
    public async Task AnswersQueued() => (await _first).State.ShouldBe(WatchState.Queued);
}

public sealed class WhenTheCallerGivesUpWhileTheLookupRuns : IDisposable
{
    private readonly Harness _harness = new();
    private readonly Gate _lookup = new();
    private readonly WatchSessionManager _manager;

    public WhenTheCallerGivesUpWhileTheLookupRuns()
    {
        _harness.Store.FindHold = _lookup;
        _manager = _harness.Manager();
    }

    public void Dispose()
    {
        _lookup.Release();
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public async Task ThrowsTheCancellation()
    {
        using var gaveUp = new CancellationTokenSource();
        var first = _manager.HeartbeatAsync(Harness.Video, 0.0, gaveUp.Token);
        _lookup.WaitUntilEntered();

        await gaveUp.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task LeavesTheSessionRunning()
    {
        using var gaveUp = new CancellationTokenSource();
        var first = _manager.HeartbeatAsync(Harness.Video, 0.0, gaveUp.Token);
        _lookup.WaitUntilEntered();
        await gaveUp.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);

        _lookup.Release();

        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete);
    }
}

public sealed class WhenAHeartbeatIsAskedOfAStoppedManager : IDisposable
{
    private readonly Harness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task RefusesIt()
    {
        var manager = _harness.Manager();
        await manager.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() =>
            manager.HeartbeatAsync(Harness.Video, 0.0, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task RefusesANegativePosition()
    {
        using var manager = _harness.Manager();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            manager.HeartbeatAsync(Harness.Video, -1.0, TestContext.Current.CancellationToken)
        );
    }
}

public sealed class WhenTheFirstAnswerWaitCouldNotWork : IDisposable
{
    private readonly Harness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void RefusesANegativeWait()
    {
        var options = _harness.Options();
        options.FirstAnswerWait = TimeSpan.FromMilliseconds(-1);

        Should.Throw<ArgumentOutOfRangeException>(() => _harness.Manager(options));
    }

    [Fact]
    public void DefaultsToHalfASecond() =>
        new WatchOptions().FirstAnswerWait.ShouldBe(TimeSpan.FromMilliseconds(500));
}

public sealed class WhenASessionIsNoLongerQueued : IDisposable
{
    private readonly Harness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsStillQueuedBeforeItStarts() =>
        _harness.Session().LeftQueue.IsCompleted.ShouldBeFalse();

    [Fact]
    public void LeavesItWhenCancelledBeforeStarting()
    {
        var session = _harness.Session();
        session.Cancel();

        session.LeftQueue.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public void LeavesItCompleteOnACacheHit()
    {
        _harness.Store.Cached = Harness.CachedTranscript(Harness.Video.Key);
        var session = _harness.Session();
        session.Start();

        Waits.On(session.LeftQueue);

        session.State.ShouldBe(WatchState.Complete);
    }

    [Fact]
    public void LeavesItFetchingOnAMiss()
    {
        var fetch = _harness.Source.Hold = new Gate();
        var session = _harness.Session();
        session.Start();

        Waits.On(session.LeftQueue);

        session.State.ShouldBe(WatchState.Fetching);
        fetch.Release();
        Waits.On(session.Completion);
    }

    [Fact]
    public void LeavesItFailedWhenTheListCannotBeRead()
    {
        _harness.BadWords.Failure = new IOException("Locked.");
        var session = _harness.Session();
        session.Start();

        Waits.On(session.LeftQueue);

        session.State.ShouldBe(WatchState.Failed);
    }
}
