using FoulFilterNet.Domain;

namespace FoulFilterNet.Jobs.Tests;

public class WhenASubscriberConnects : IDisposable
{
    private readonly JobManager _jobs = new();
    private readonly JobSubscription _subscription;

    public WhenASubscriberConnects()
    {
        _jobs.Enqueue("one", "a.mp3", Requests.For("a.mp3"));
        _jobs.Enqueue("two", "b.mp3", Requests.For("b.mp3"));

        _subscription = _jobs.Subscribe();

        _subscription.ShouldNotBeNull();
    }

    [Fact]
    public void GetsTheJobsThatAlreadyExist() =>
        _subscription.Snapshot.Select(job => job.Id).ShouldBe(["one", "two"]);

    [Fact]
    public void DoesNotReplayThemAsUpdates() =>
        _subscription.Updates.TryRead(out _).ShouldBeFalse();

    [Fact]
    public void IsCountedByTheFanOut() => _jobs.Events.SubscriberCount.ShouldBe(1);

    [Fact]
    public void MissesNothingThatHappensAfterTheSnapshot()
    {
        _jobs.Enqueue("three", "c.mp3", Requests.For("c.mp3"));

        _subscription.Updates.TryRead(out var update).ShouldBeTrue();
        update!.Id.ShouldBe("three");
    }

    public void Dispose()
    {
        _subscription.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The UI redraws a row from whatever the latest event says, so the order the
/// events arrive in is the order the row transitions through.
/// </summary>
public class WhenAJobMovesThroughItsLifecycle : IDisposable
{
    private readonly JobManager _jobs = new();
    private readonly JobSubscription _subscription;
    private readonly List<JobRecord> _received = [];

    public WhenAJobMovesThroughItsLifecycle()
    {
        _subscription = _jobs.Subscribe();

        _jobs.Enqueue("abc123", "book.mp3", Requests.For("book.mp3"));
        _jobs.TryBeginProcessing("abc123", out _, out _);
        _jobs.Report("abc123", new JobProgress("transcribing", 40, "Transcribing audio"));
        _jobs.MarkCompleted("abc123", new JobSummary([], 3, false, false));

        while (_subscription.Updates.TryRead(out var update))
        {
            _received.Add(update);
        }

        _received.ShouldNotBeEmpty();
    }

    [Fact]
    public void ReportsEveryTransition() => _received.Count.ShouldBe(4);

    [Fact]
    public void ReportsThemInOrder() =>
        _received
            .Select(job => job.Status)
            .ShouldBe([
                JobStatus.Queued,
                JobStatus.Processing,
                JobStatus.Processing,
                JobStatus.Completed,
            ]);

    [Fact]
    public void CarriesTheProgressCheckpoint() => _received[2].Progress.ShouldBe(40);

    [Fact]
    public void EndsWithTheDownloadLink() => _received[^1].DownloadUrl.ShouldBe("/download/abc123");

    public void Dispose()
    {
        _subscription.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenAQueuedJobIsCancelled : IDisposable
{
    private readonly JobManager _jobs = new();
    private readonly JobSubscription _subscription;

    public WhenAQueuedJobIsCancelled()
    {
        _subscription = _jobs.Subscribe();
        _jobs.Enqueue("abc123", "book.mp3", Requests.For("book.mp3"));
        _subscription.Updates.TryRead(out _).ShouldBeTrue();

        _jobs.Cancel("abc123");
    }

    [Fact]
    public void TellsTheSubscribersAboutIt()
    {
        _subscription.Updates.TryRead(out var update).ShouldBeTrue();
        update!.Status.ShouldBe(JobStatus.Cancelled);
    }

    public void Dispose()
    {
        _subscription.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The whole point of the fan-out: an SSE client that has stopped reading - a
/// closed laptop lid, a paused tab - must never hold up the one worker.
/// </summary>
public class WhenASubscriberStopsReading : IDisposable
{
    private readonly JobEventFanOut _fanOut = new(capacity: 4);
    private readonly JobSubscription _stalled;
    private readonly JobSubscription _attentive;
    private readonly JobManager _jobs;

    public WhenASubscriberStopsReading()
    {
        _jobs = new JobManager(_fanOut);
        _stalled = _jobs.Subscribe();
        _attentive = _jobs.Subscribe();

        for (var i = 0; i < 20; i++)
        {
            _jobs.Enqueue($"job{i}", $"book{i}.mp3", Requests.For($"book{i}.mp3"));
            _attentive.Updates.TryRead(out _);
        }

        _jobs.Snapshot().Count.ShouldBe(20);
    }

    [Fact]
    public void KeepsTheStalledSubscriberConnected() => _jobs.Events.SubscriberCount.ShouldBe(2);

    [Fact]
    public void BoundsWhatTheStalledSubscriberHolds()
    {
        var buffered = 0;
        while (_stalled.Updates.TryRead(out _))
        {
            buffered++;
        }

        buffered.ShouldBe(4);
    }

    [Fact]
    public void LeavesTheStalledSubscriberTheNewestEventsRatherThanTheOldest()
    {
        _stalled.Updates.TryRead(out var oldest).ShouldBeTrue();

        oldest!.Id.ShouldBe("job16");
    }

    [Fact]
    public void DoesNotCostTheAttentiveSubscriberAnything() =>
        _attentive.Updates.TryRead(out _).ShouldBeFalse();

    public void Dispose()
    {
        _stalled.Dispose();
        _attentive.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The same property, observed through the worker rather than the fan-out: a
/// subscriber that never reads must not keep a job from finishing.
/// </summary>
public class WhenTheWorkerRunsWhileASubscriberIsStalled : IDisposable
{
    private readonly JobHarness _harness = new(new JobEventFanOut(capacity: 2));
    private readonly JobRecord _record;

    public WhenTheWorkerRunsWhileASubscriberIsStalled()
    {
        _harness.Jobs.Subscribe();
        _harness.StartAsync().GetAwaiter().GetResult();

        var queued = _harness.Enqueue("book.mp3");
        _record = _harness
            .WaitForStatusAsync(queued.Id, JobStatus.Completed)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void FinishesTheJobAnyway() => _record.Status.ShouldBe(JobStatus.Completed);

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

public class WhenASubscriberDisconnects
{
    private readonly JobManager _jobs = new();
    private readonly JobSubscription _gone;
    private readonly JobSubscription _stays;

    public WhenASubscriberDisconnects()
    {
        _gone = _jobs.Subscribe();
        _stays = _jobs.Subscribe();

        _gone.Dispose();

        _jobs.Enqueue("abc123", "book.mp3", Requests.For("book.mp3"));
    }

    [Fact]
    public void IsNoLongerCounted() => _jobs.Events.SubscriberCount.ShouldBe(1);

    [Fact]
    public void StopsReceivingUpdates() => _gone.Updates.TryRead(out _).ShouldBeFalse();

    [Fact]
    public void LeavesTheOtherSubscriberReceiving() => _stays.Updates.TryRead(out _).ShouldBeTrue();

    [Fact]
    public void CanBeDisconnectedAgainWithoutComplaint() => Should.NotThrow(() => _gone.Dispose());

    [Fact]
    public void CanBeUnsubscribedDirectlyWithoutComplaint() =>
        Should.NotThrow(() => _jobs.Events.Unsubscribe(_gone));
}
