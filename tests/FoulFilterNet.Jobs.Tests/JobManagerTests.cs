using FoulFilterNet.Domain;

namespace FoulFilterNet.Jobs.Tests;

/// <summary>
/// The queue's own bookkeeping, with no worker draining it, so the records are
/// observed in the state an enqueue leaves them in.
/// </summary>
public class WhenAJobIsEnqueued
{
    private readonly JobManager _jobs = new();
    private readonly JobRecord _record;

    public WhenAJobIsEnqueued()
    {
        _record = _jobs.Enqueue("abc123", "book.mp3", Requests.For("book.mp3"));

        _record.ShouldNotBeNull();
        _jobs.Snapshot().Count.ShouldBe(1);
    }

    [Fact]
    public void StartsOutQueued() => _record.Status.ShouldBe(JobStatus.Queued);

    [Fact]
    public void ReportsTheQueuedStage() => _record.Stage.ShouldBe("queued");

    [Fact]
    public void StartsAtZeroProgress() => _record.Progress.ShouldBe(0);

    [Fact]
    public void KeepsTheSanitizedFileNameForTheUi() => _record.FileName.ShouldBe("book.mp3");

    [Fact]
    public void HasNoDownloadUrlYet() => _record.DownloadUrl.ShouldBeNull();

    [Fact]
    public void IsFoundById() => _jobs.Find("abc123").ShouldBe(_record);

    [Fact]
    public void CarriesTheRequestTheWorkerWillRun() =>
        _jobs.Find("abc123")!.Request.InputPath.ShouldEndWith("book.mp3");
}

public class WhenSeveralJobsAreEnqueued
{
    private readonly JobManager _jobs = new();
    private readonly IReadOnlyList<JobRecord> _snapshot;

    public WhenSeveralJobsAreEnqueued()
    {
        _jobs.Enqueue("one", "a.mp3", Requests.For("a.mp3"));
        _jobs.Enqueue("two", "b.mp3", Requests.For("b.mp3"));
        _jobs.Enqueue("three", "c.mp3", Requests.For("c.mp3"));

        _snapshot = _jobs.Snapshot();

        _snapshot.Count.ShouldBe(3);
    }

    [Fact]
    public void KeepsThemInEnqueueOrder() =>
        _snapshot.Select(job => job.Id).ShouldBe(["one", "two", "three"]);

    [Fact]
    public void RejectsADuplicateId() =>
        Should.Throw<ArgumentException>(() => _jobs.Enqueue("one", "a.mp3", Requests.For("a.mp3")));

    [Fact]
    public void ReturnsNullForAnUnknownId() => _jobs.Find("nope").ShouldBeNull();

    [Fact]
    public void TakesASnapshotThatLaterChangesDoNotAlter()
    {
        _jobs.Enqueue("four", "d.mp3", Requests.For("d.mp3"));

        _snapshot.Count.ShouldBe(3);
    }
}

public class WhenCancellingAJobThatWasNeverQueued
{
    private readonly JobManager _jobs = new();
    private readonly JobCancelOutcome _outcome;

    public WhenCancellingAJobThatWasNeverQueued()
    {
        _outcome = _jobs.Cancel("missing");

        _jobs.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public void ReportsThatThereWasNothingToCancel() =>
        _outcome.ShouldBe(JobCancelOutcome.NotFound);
}

/// <summary>
/// Finding 5: the Python tracked cancellation in a set mutated from two threads.
/// A per-job token has to be live from the moment the job is queued.
/// </summary>
public class WhenCancellingAJobThatIsStillQueued
{
    private readonly JobManager _jobs = new();
    private readonly JobCancelOutcome _outcome;
    private readonly JobRecord _record;

    public WhenCancellingAJobThatIsStillQueued()
    {
        _jobs.Enqueue("abc123", "book.mp3", Requests.For("book.mp3"));

        _outcome = _jobs.Cancel("abc123");
        _record = _jobs.Find("abc123")!;

        _record.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsItAsCancelled() => _outcome.ShouldBe(JobCancelOutcome.Cancelled);

    [Fact]
    public void MovesTheRecordToCancelled() => _record.Status.ShouldBe(JobStatus.Cancelled);

    [Fact]
    public void SaysWhoCancelledIt() => _record.Detail.ShouldBe("Cancelled by user");

    [Fact]
    public void RefusesToStartItAfterwards() =>
        _jobs.TryBeginProcessing("abc123", out _, out _).ShouldBeFalse();

    [Fact]
    public void IsIdempotent() => _jobs.Cancel("abc123").ShouldBe(JobCancelOutcome.AlreadyFinished);
}

/// <summary>
/// The front end reuses <c>DELETE /jobs/{id}</c> as "remove this row", so a job
/// that has already finished must answer without erroring.
/// </summary>
public class WhenCancellingAJobThatAlreadyFinished
{
    private readonly JobManager _jobs = new();
    private readonly JobCancelOutcome _outcome;

    public WhenCancellingAJobThatAlreadyFinished()
    {
        _jobs.Enqueue("abc123", "book.mp3", Requests.For("book.mp3"));
        _jobs.TryBeginProcessing("abc123", out _, out _).ShouldBeTrue();
        _jobs.MarkCompleted("abc123", new JobSummary([], 4, false, false));

        _outcome = _jobs.Cancel("abc123");
    }

    [Fact]
    public void ReportsThatThereWasNothingLeftToCancel() =>
        _outcome.ShouldBe(JobCancelOutcome.AlreadyFinished);

    [Fact]
    public void LeavesItCompleted() => _jobs.Find("abc123")!.Status.ShouldBe(JobStatus.Completed);
}

internal static class Requests
{
    public static JobRequest For(string fileName) =>
        new()
        {
            InputPath = Path.Combine(Path.GetTempPath(), "ffn-unused", "uploads", fileName),
            OutputPath = Path.Combine(
                Path.GetTempPath(),
                "ffn-unused",
                "outputs",
                $"censored_{fileName}"
            ),
            BadWordsPath = Path.Combine(Path.GetTempPath(), "ffn-unused", "bad_words.txt"),
            TranscriptDirectory = Path.Combine(Path.GetTempPath(), "ffn-unused", "transcripts"),
            ScratchDirectory = Path.Combine(Path.GetTempPath(), "ffn-unused", "scratch"),
        };
}
