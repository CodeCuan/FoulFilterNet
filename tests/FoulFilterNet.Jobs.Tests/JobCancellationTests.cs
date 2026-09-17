using FoulFilterNet.Domain;

namespace FoulFilterNet.Jobs.Tests;

/// <summary>
/// Finding 5. The Python signalled a running job through a set it mutated from
/// two threads without the lock; a per-job token has to reach the pipeline for
/// real, and the worker has to survive it.
/// </summary>
public class WhenARunningJobIsCancelled : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly Gate _started = new();
    private readonly JobRecord _record;
    private readonly bool _pipelineSawTheCancellation;
    private readonly JobCancelOutcome _outcome;

    public WhenARunningJobIsCancelled()
    {
        var observed = false;
        _harness.Pipeline.Behaviour = async (_, _, token) =>
        {
            _started.Reached();
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                observed = true;
                throw new JobCancelledException();
            }

            return StubMediaPipeline.Completed();
        };
        _harness.StartAsync().GetAwaiter().GetResult();

        var queued = _harness.Enqueue("book.mp3");
        _harness.WaitForStatusAsync(queued.Id, JobStatus.Processing).GetAwaiter().GetResult();
        _started.Entered.GetAwaiter().GetResult();

        _outcome = _harness.Jobs.Cancel(queued.Id);
        _record = _harness
            .WaitForStatusAsync(queued.Id, JobStatus.Cancelled)
            .GetAwaiter()
            .GetResult();
        _pipelineSawTheCancellation = observed;

        _record.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsThatItWasCancelled() => _outcome.ShouldBe(JobCancelOutcome.Cancelled);

    [Fact]
    public void ReachesThePipelineThroughItsOwnToken() =>
        _pipelineSawTheCancellation.ShouldBeTrue();

    [Fact]
    public void SaysWhoCancelledIt() => _record.Detail.ShouldBe("Cancelled by user");

    [Fact]
    public void RemovesTheUploadedInput() => File.Exists(_record.Request.InputPath).ShouldBeFalse();

    [Fact]
    public void RemovesTheJobScratchDirectory() =>
        Directory.Exists(_record.Request.ScratchDirectory).ShouldBeFalse();

    [Fact]
    public async Task LeavesTheWorkerAbleToRunTheNextJob()
    {
        _harness.Pipeline.Behaviour = (_, _, _) => Task.FromResult(StubMediaPipeline.Completed());

        var next = _harness.Enqueue("next.mp3");

        (await _harness.WaitForStatusAsync(next.Id, JobStatus.Completed)).Status.ShouldBe(
            JobStatus.Completed
        );
    }

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A pipeline that lets the raw <see cref="OperationCanceledException"/> escape
/// is cancelled, not failed - the distinction the UI renders.
/// </summary>
public class WhenARunningJobSurfacesARawCancellation : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly Gate _started = new();
    private readonly JobRecord _record;

    public WhenARunningJobSurfacesARawCancellation()
    {
        _harness.Pipeline.Behaviour = async (_, _, token) =>
        {
            _started.Reached();
            await Task.Delay(Timeout.Infinite, token);
            return StubMediaPipeline.Completed();
        };
        _harness.StartAsync().GetAwaiter().GetResult();

        var queued = _harness.Enqueue("book.mp3");
        _started.Entered.GetAwaiter().GetResult();
        _harness.Jobs.Cancel(queued.Id);

        _record = _harness
            .WaitForStatusAsync(queued.Id, JobStatus.Cancelled)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void IsNotReportedAsAFailure() => _record.Status.ShouldBe(JobStatus.Cancelled);

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The worker is busy, so the cancelled job is still sitting in the channel. It
/// must be skipped rather than started when the worker eventually reaches it.
/// </summary>
public class WhenAQueuedJobIsCancelledWhileAnotherIsRunning : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly Gate _blocked = new();
    private readonly JobRecord _cancelled;
    private readonly JobRecord _running;

    public WhenAQueuedJobIsCancelledWhileAnotherIsRunning()
    {
        _harness.Pipeline.Behaviour = async (_, _, _) =>
        {
            await _blocked.PassAsync();
            return StubMediaPipeline.Completed();
        };
        _harness.StartAsync().GetAwaiter().GetResult();

        var first = _harness.Enqueue("first.mp3");
        _blocked.Entered.GetAwaiter().GetResult();

        var second = _harness.Enqueue("second.mp3");
        _harness.Jobs.Cancel(second.Id).ShouldBe(JobCancelOutcome.Cancelled);

        _blocked.Open();
        _running = _harness
            .WaitForStatusAsync(first.Id, JobStatus.Completed)
            .GetAwaiter()
            .GetResult();
        _cancelled = _harness.Get(second.Id);
    }

    [Fact]
    public void NeverHandsTheCancelledJobToThePipeline() =>
        _harness.Pipeline.Requests.ShouldHaveSingleItem();

    [Fact]
    public void LeavesTheCancelledJobCancelled() => _cancelled.Status.ShouldBe(JobStatus.Cancelled);

    [Fact]
    public void LetsTheRunningJobFinish() => _running.Status.ShouldBe(JobStatus.Completed);

    [Fact]
    public void RemovesTheCancelledJobsUpload() =>
        File.Exists(_cancelled.Request.InputPath).ShouldBeFalse();

    public void Dispose()
    {
        _blocked.Open();
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
