using FoulFilterNet.Sources;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

public sealed class WhenTheFirstHeartbeatArrives : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot _snapshot;
    private readonly WatchSnapshot _whileFetching;

    public WhenTheFirstHeartbeatArrives()
    {
        var fetch = _harness.Source.Hold = new Gate();
        _manager = _harness.Manager();

        _snapshot = _manager.Heartbeat(Harness.Video, 0.0);
        fetch.WaitUntilEntered();
        _whileFetching = _manager.Find(Harness.Video)!;
        fetch.Release();
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void AnswersForTheVideo() => _snapshot.Key.ShouldBe("youtube-dQw4w9WgXcQ");

    [Fact]
    public void AnswersBeforeTheWorkIsDone() => _snapshot.State.IsTerminal().ShouldBeFalse();

    [Fact]
    public void HoldsOneSession() => _manager.Count.ShouldBe(1);

    [Fact]
    public void StartsTheSession() => _whileFetching.State.ShouldBe(WatchState.Fetching);
}

public sealed class WhenHeartbeatsRaceForOneVideo : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot[] _snapshots;

    public WhenHeartbeatsRaceForOneVideo()
    {
        _manager = _harness.Manager();
        // Dedicated threads, released together, so the race does not wait on
        // the thread pool growing.
        const int Racers = 16;
        using var start = new Barrier(Racers);
        _snapshots = new WatchSnapshot[Racers];
        var threads = Enumerable
            .Range(0, Racers)
            .Select(i => new Thread(() =>
            {
                start.SignalAndWait(Waits.Timeout);
                _snapshots[i] = _manager.Heartbeat(Harness.Video, i);
            }))
            .ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(Waits.Timeout).ShouldBeTrue();
        }

        Waits.Until(() => _manager.Find(Harness.Video)!.State == WatchState.Complete);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void HoldsOneSession() => _manager.Count.ShouldBe(1);

    [Fact]
    public void FetchesOnce() => _harness.Source.Calls.ShouldBe(1);

    [Fact]
    public void OpensTheAudioOnce() => _harness.Engine.Opened.Count.ShouldBe(1);

    [Fact]
    public void SavesOnce() => _harness.Store.Saves.Count.ShouldBe(1);

    [Fact]
    public void AnswersEveryHeartbeatForTheVideo() =>
        _snapshots.ShouldAllBe(s => s.Key == "youtube-dQw4w9WgXcQ");
}

public sealed class WhenTwoVideosAreWatched : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenTwoVideosAreWatched()
    {
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        _manager.Heartbeat(Harness.OtherVideo, 0.0);

        Waits.Until(() =>
            _manager.Find(Harness.Video)!.State == WatchState.Complete
            && _manager.Find(Harness.OtherVideo)!.State == WatchState.Complete
        );
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void HoldsASessionForEach() => _manager.Count.ShouldBe(2);

    [Fact]
    public void FetchesEach() => _harness.Source.Calls.ShouldBe(2);

    [Fact]
    public void SavesEachUnderItsOwnKey() =>
        _harness
            .Store.Saves.Select(s => s.Transcript.FileHash)
            .Order(StringComparer.Ordinal)
            .ShouldBe(["youtube-dQw4w9WgXcQ", "youtube-jNQXAC9IVRw"]);

    [Fact]
    public void GivesEachItsOwnScratchDirectory() =>
        _harness.Source.Directories.Distinct().Count().ShouldBe(2);
}

public sealed class WhenAHeartbeatMovesThePlayhead : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenAHeartbeatMovesThePlayhead()
    {
        var first = _harness.Engine.Hold(0);
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);

        first.WaitUntilEntered();
        _manager.Heartbeat(Harness.Video, 70.0);
        first.Release();

        Waits.Until(() => _manager.Find(Harness.Video)!.State == WatchState.Complete);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void ReordersTheWindowsAfterTheOneInFlight() =>
        _harness.Engine.Requested.ShouldBe([0, 3, 4, 2, 1]);

    [Fact]
    public void KeepsOneSession() => _harness.Source.Calls.ShouldBe(1);
}

public sealed class WhenTheFirstHeartbeatCarriesAPlayhead : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenTheFirstHeartbeatCarriesAPlayhead()
    {
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 90.0);

        Waits.Until(() => _manager.Find(Harness.Video)!.State == WatchState.Complete);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void StartsWhereTheViewerIs() => _harness.Engine.Requested.First().ShouldBe(4);
}

public sealed class WhenAHeartbeatIsNotATime : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenAHeartbeatIsNotATime()
    {
        _manager = _harness.Manager();
    }

    public void Dispose()
    {
        _manager.Dispose();
        _harness.Dispose();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void IsRejected(double position) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            _manager.Heartbeat(Harness.Video, position)
        );

    [Fact]
    public void StartsNoSession()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            _manager.Heartbeat(Harness.Video, double.NaN)
        );

        _manager.Count.ShouldBe(0);
    }
}

public sealed class WhenFindingAVideo : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenFindingAVideo()
    {
        _manager = _harness.Manager();
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void FindsNothingForAVideoNotWatched() => _manager.Find(Harness.Video).ShouldBeNull();

    [Fact]
    public void StartsNothing()
    {
        _manager.Find(Harness.Video);

        _manager.Count.ShouldBe(0);
    }

    [Fact]
    public void FindsAWatchedVideo()
    {
        _manager.Heartbeat(Harness.Video, 0.0);

        _manager.Find(Harness.Video)!.Key.ShouldBe("youtube-dQw4w9WgXcQ");
    }

    [Fact]
    public void DoesNotFindAnotherVideo()
    {
        _manager.Heartbeat(Harness.Video, 0.0);

        _manager.Find(Harness.OtherVideo).ShouldBeNull();
    }

    [Fact]
    public void DoesNotCountAsAHeartbeat()
    {
        var hold = _harness.Engine.Hold(1);
        _manager.Heartbeat(Harness.Video, 0.0);
        hold.WaitUntilEntered();

        _harness.Time.Advance(TimeSpan.FromMinutes(1));
        _manager.Find(Harness.Video);
        _harness.Time.Advance(TimeSpan.FromMinutes(1));

        _manager.Find(Harness.Video).ShouldBeNull();
    }
}

public sealed class WhenAVideoIsCancelledMidWindow : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly bool _cancelled;

    public WhenAVideoIsCancelledMidWindow()
    {
        var second = _harness.Engine.Hold(1);
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);

        second.WaitUntilEntered();
        _cancelled = _manager.Cancel(Harness.Video);
        Waits.Until(() => _harness.EveryAudioIsDisposed && _harness.ScratchIsEmpty);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void SaysItWasThere() => _cancelled.ShouldBeTrue();

    [Fact]
    public void DropsTheSession() => _manager.Count.ShouldBe(0);

    [Fact]
    public void FindsNothingAfterwards() => _manager.Find(Harness.Video).ShouldBeNull();

    [Fact]
    public void AbandonsTheWindowInFlight() => _harness.Engine.Abandoned.ShouldBe([1]);

    [Fact]
    public void CleansUpTheWav() =>
        Waits.Until(() => _harness.EveryPreparedWavIsDeleted).ShouldBe(true);

    [Fact]
    public void SavesNothing() => _harness.Store.Saves.ShouldBeEmpty();

    [Fact]
    public void StartsAgainOnTheNextHeartbeat()
    {
        _manager.Heartbeat(Harness.Video, 0.0);

        Waits.Until(() => _harness.Source.Calls == 2).ShouldBe(true);
    }

    [Fact]
    public void SaysNothingWasThereTheSecondTime() =>
        _manager.Cancel(Harness.Video).ShouldBeFalse();
}

public sealed class WhenCancellingAVideoNotWatched : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenCancellingAVideoNotWatched()
    {
        _manager = _harness.Manager();
    }

    public void Dispose()
    {
        _manager.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void SaysItWasNotThere() => _manager.Cancel(Harness.Video).ShouldBeFalse();
}

public sealed class WhenNoHeartbeatArrivesForTheIdleTimeout : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly int _sweptEarly;
    private readonly WatchSnapshot? _foundEarly;
    private readonly int _swept;

    public WhenNoHeartbeatArrivesForTheIdleTimeout()
    {
        var second = _harness.Engine.Hold(1);
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        second.WaitUntilEntered();

        // Window 0 moved the clock on by a second; time from the heartbeat.
        var heartbeat = Harness.Epoch;
        _harness.Time.SetUtcNow(heartbeat + TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        _sweptEarly = _manager.SweepExpired();
        _foundEarly = _manager.Find(Harness.Video);

        _harness.Time.SetUtcNow(heartbeat + TimeSpan.FromMinutes(2));
        _swept = _manager.SweepExpired();

        Waits.Until(() => _harness.EveryAudioIsDisposed && _harness.ScratchIsEmpty);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void KeepsItJustShortOfTheTimeout() => _sweptEarly.ShouldBe(0);

    [Fact]
    public void StillServesItJustShortOfTheTimeout() => _foundEarly.ShouldNotBeNull();

    [Fact]
    public void DropsItAtTheTimeout() => _swept.ShouldBe(1);

    [Fact]
    public void HoldsNothingAfterwards() => _manager.Count.ShouldBe(0);

    [Fact]
    public void CancelsTheWindowInFlight() => _harness.Engine.Abandoned.ShouldBe([1]);

    [Fact]
    public void SavesNothing() => _harness.Store.Saves.ShouldBeEmpty();

    [Fact]
    public void DeletesItsScratchDirectory() => _harness.ScratchIsEmpty.ShouldBeTrue();
}

public sealed class WhenHeartbeatsKeepAWorkingSessionAlive : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly int _swept;

    public WhenHeartbeatsKeepAWorkingSessionAlive()
    {
        var second = _harness.Engine.Hold(1);
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        second.WaitUntilEntered();

        _harness.Time.SetUtcNow(Harness.Epoch + TimeSpan.FromMinutes(1.5));
        _manager.Heartbeat(Harness.Video, 5.0);
        _harness.Time.SetUtcNow(Harness.Epoch + TimeSpan.FromMinutes(3));
        _swept = _manager.SweepExpired();

        second.Release();
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void DropsNothing() => _swept.ShouldBe(0);

    [Fact]
    public void KeepsTheSession() => _manager.Count.ShouldBe(1);

    [Fact]
    public void KeepsItsWork() => _harness.Engine.Abandoned.ShouldBeEmpty();
}

public sealed class WhenAHeartbeatFindsAnIdleSessionNotYetSwept : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenAHeartbeatFindsAnIdleSessionNotYetSwept()
    {
        var second = _harness.Engine.Hold(1);
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        second.WaitUntilEntered();

        _harness.Time.SetUtcNow(Harness.Epoch + TimeSpan.FromMinutes(5));
        _manager.Heartbeat(Harness.Video, 0.0);

        Waits.Until(() => _harness.Source.Calls == 2);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void CancelsTheExpiredOne() =>
        Waits.Until(() => _harness.Engine.Abandoned.Contains(1)).ShouldBe(true);

    [Fact]
    public void HoldsOnlyTheNewOne() => _manager.Count.ShouldBe(1);
}

public sealed class WhenACompleteSessionIsRetained : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly DateTimeOffset _completedAt;

    public WhenACompleteSessionIsRetained()
    {
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete);

        // Five windows at a second each moved the fake clock on.
        _completedAt = _harness.Time.GetUtcNow();
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void IsKeptJustShortOfTheRetention()
    {
        _harness.Time.SetUtcNow(_completedAt + TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1));

        _manager.SweepExpired().ShouldBe(0);
    }

    [Fact]
    public void IsDroppedAtTheRetention()
    {
        _harness.Time.SetUtcNow(_completedAt + TimeSpan.FromMinutes(30));

        _manager.SweepExpired().ShouldBe(1);
    }

    [Fact]
    public void OutlivesTheIdleTimeout()
    {
        _harness.Time.SetUtcNow(_completedAt + TimeSpan.FromMinutes(10));

        _manager.Find(Harness.Video)!.State.ShouldBe(WatchState.Complete);
    }

    [Fact]
    public void IsKeptLongerByHeartbeats()
    {
        _harness.Time.SetUtcNow(_completedAt + TimeSpan.FromMinutes(20));
        _manager.Heartbeat(Harness.Video, 50.0);
        _harness.Time.SetUtcNow(_completedAt + TimeSpan.FromMinutes(45));

        _manager.SweepExpired().ShouldBe(0);
    }

    [Fact]
    public void IsServedByHeartbeatsWithoutWorkingAgain()
    {
        _manager.Heartbeat(Harness.Video, 10.0);
        _manager.Heartbeat(Harness.Video, 20.0);

        _harness.Engine.Requested.Count.ShouldBe(5);
    }

    [Fact]
    public void IsSavedOnceHoweverOftenItIsPolled()
    {
        _manager.Heartbeat(Harness.Video, 10.0);
        _manager.Find(Harness.Video);

        _harness.Store.Saves.Count.ShouldBe(1);
    }
}

public sealed class WhenACompleteVideoIsWatchedAgainAfterItsRetention : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot _again;

    public WhenACompleteVideoIsWatchedAgainAfterItsRetention()
    {
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete);

        _harness.Time.Advance(TimeSpan.FromHours(1));
        _manager.Heartbeat(Harness.Video, 0.0);
        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete);
        _again = _manager.Find(Harness.Video)!;
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void IsServedFromTheCache() => _again.FromCache.ShouldBeTrue();

    [Fact]
    public void FetchesNothingMore() => _harness.Source.Calls.ShouldBe(1);

    [Fact]
    public void TranscribesNothingMore() => _harness.Engine.Requested.Count.ShouldBe(5);

    [Fact]
    public void StartsItsRevisionsAgain() => _again.Revision.ShouldBe(1);
}

public sealed class WhenAFailedSessionIsRetained : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly DateTimeOffset _failedAt;

    public WhenAFailedSessionIsRetained()
    {
        _harness.Source.Failure = FakeAudioSource.Failing(
            WebAudioFailure.Network,
            "Unable to download webpage"
        );
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Failed);
        _failedAt = _harness.Time.GetUtcNow();
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void ServesTheReasonToTheNextHeartbeat() =>
        _manager.Heartbeat(Harness.Video, 0.0).Reason.ShouldBe("Unable to download webpage");

    [Fact]
    public void DoesNotRetryWithinTheRetention()
    {
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromSeconds(59));
        _manager.Heartbeat(Harness.Video, 0.0);

        _harness.Source.Calls.ShouldBe(1);
    }

    [Fact]
    public void RetriesOnTheFirstHeartbeatAfterIt()
    {
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromMinutes(1));
        _manager.Heartbeat(Harness.Video, 0.0);

        Waits.Until(() => _harness.Source.Calls == 2).ShouldBe(true);
    }

    [Fact]
    public void IsNotKeptLongerByHeartbeats()
    {
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromSeconds(30));
        _manager.Heartbeat(Harness.Video, 0.0);
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromSeconds(59));
        _manager.Heartbeat(Harness.Video, 0.0);
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromMinutes(1));
        _manager.Heartbeat(Harness.Video, 0.0);

        Waits.Until(() => _harness.Source.Calls == 2).ShouldBe(true);
    }

    [Fact]
    public void RetriesWithANewSessionThatCanSucceed()
    {
        _harness.Source.Failure = null;
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromMinutes(1));
        _manager.Heartbeat(Harness.Video, 0.0);

        Waits
            .Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete)
            .ShouldBe(true);
    }

    [Fact]
    public void IsSweptAfterTheRetention()
    {
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromMinutes(1));

        _manager.SweepExpired().ShouldBe(1);
    }

    [Fact]
    public void IsNotSweptWithinTheRetention()
    {
        _harness.Time.SetUtcNow(_failedAt + TimeSpan.FromSeconds(59));

        _manager.SweepExpired().ShouldBe(0);
    }
}

public sealed class WhenAnUnsupportedSessionIsRetained : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenAnUnsupportedSessionIsRetained()
    {
        _harness.Source.Failure = FakeAudioSource.Failing(
            WebAudioFailure.Unsupported,
            "This live event will begin in 3 hours."
        );
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Unsupported);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void ServesTheReason() =>
        _manager.Find(Harness.Video)!.Reason.ShouldBe("This live event will begin in 3 hours.");

    [Fact]
    public void RetriesAfterTheFailedRetention()
    {
        _harness.Time.Advance(TimeSpan.FromMinutes(1));
        _manager.Heartbeat(Harness.Video, 0.0);

        Waits.Until(() => _harness.Source.Calls == 2).ShouldBe(true);
    }
}

public sealed class WhenTheRetentionsAreConfigured : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenTheRetentionsAreConfigured()
    {
        var options = _harness.Options();
        options.IdleTimeout = TimeSpan.FromSeconds(10);
        _manager = _harness.Manager(options);

        _harness.Engine.Hold(0);
        _manager.Heartbeat(Harness.Video, 0.0);
        _harness.Time.Advance(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void UsesTheConfiguredIdleTimeout() => _manager.SweepExpired().ShouldBe(1);
}

public sealed class WhenTheManagerIsDisposed : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;

    public WhenTheManagerIsDisposed()
    {
        var second = _harness.Engine.Hold(1);
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        second.WaitUntilEntered();

        Waits.On(_manager.DisposeAsync().AsTask());
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void CancelsTheWorkInFlight() => _harness.Engine.Abandoned.ShouldBe([1]);

    [Fact]
    public void WaitsForTheCleanup() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void DisposesTheAudioFirst() => _harness.EveryAudioIsDisposed.ShouldBeTrue();

    [Fact]
    public void HoldsNothing() => _manager.Count.ShouldBe(0);

    [Fact]
    public void RefusesFurtherHeartbeats() =>
        Should.Throw<ObjectDisposedException>(() => _manager.Heartbeat(Harness.Video, 0.0));

    [Fact]
    public void CanBeDisposedAgain() => Should.NotThrow(_manager.Dispose);
}

public sealed class WhenTheOptionsCouldNotWork : IDisposable
{
    private readonly Harness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void RefusesAZeroIdleTimeout()
    {
        var options = _harness.Options();
        options.IdleTimeout = TimeSpan.Zero;

        Should.Throw<ArgumentOutOfRangeException>(() => _harness.Manager(options));
    }

    [Fact]
    public void RefusesABlankScratchDirectory()
    {
        var options = _harness.Options();
        options.ScratchDirectory = " ";

        Should.Throw<ArgumentException>(() => _harness.Manager(options));
    }
}

public sealed class WhenTheHeadLengthCouldNotWork : IDisposable
{
    private readonly Harness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RefusesIt(double seconds)
    {
        var options = _harness.Options();
        options.HeadSeconds = seconds;

        Should
            .Throw<ArgumentOutOfRangeException>(() => _harness.Manager(options))
            .ParamName.ShouldBe(nameof(WatchOptions.HeadSeconds));
    }

    [Fact]
    public void TakesZeroAsOff()
    {
        var options = _harness.Options();
        options.HeadSeconds = 0.0;

        Should.NotThrow(() => _harness.Manager(options).Dispose());
    }

    [Fact]
    public void DefaultsToTwoMinutes() => new WatchOptions().HeadSeconds.ShouldBe(120.0);
}

public sealed class WhenTheManagersSessionsHearAHead : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSessionManager _manager;

    public WhenTheManagersSessionsHearAHead()
    {
        var options = _harness.Options();
        options.HeadSeconds = 100.0;
        _harness.Engine.HeadScript = HeadStartVideo.Script.Head(100.0);
        _manager = _harness.Manager(options);

        _manager.Heartbeat(Harness.Video, 0.0);
        Waits.Until(() => _manager.Find(Harness.Video)?.State == WatchState.Complete);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void ConvertTheConfiguredLength() =>
        _harness.Preparer.HeadRequests.Single().Seconds.ShouldBe(100.0);

    [Fact]
    public void FindTheBatchPipelinesHits() =>
        _manager.Find(Harness.Video)!.Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);
}
