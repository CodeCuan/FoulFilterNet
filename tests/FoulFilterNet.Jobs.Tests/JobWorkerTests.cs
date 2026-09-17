using FoulFilterNet.Domain;

namespace FoulFilterNet.Jobs.Tests;

public class WhenTheWorkerRunsAJobToCompletion : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly JobRecord _record;

    public WhenTheWorkerRunsAJobToCompletion()
    {
        _harness.Pipeline.Behaviour = (_, _, _) =>
            Task.FromResult(StubMediaPipeline.Completed(hits: 2));
        _harness.StartAsync().GetAwaiter().GetResult();

        var queued = _harness.Enqueue("book.mp3");
        _record = _harness
            .WaitForStatusAsync(queued.Id, JobStatus.Completed)
            .GetAwaiter()
            .GetResult();

        _record.Status.ShouldBe(JobStatus.Completed);
    }

    [Fact]
    public void ReportsFullProgress() => _record.Progress.ShouldBe(100);

    [Fact]
    public void EndsOnTheCompletedStage() => _record.Stage.ShouldBe("completed");

    [Fact]
    public void CountsTheHitsInTheDetailLine() => _record.Detail.ShouldBe("2 hit(s)");

    [Fact]
    public void OffersTheDownloadLinkTheUiUses() =>
        _record.DownloadUrl.ShouldBe($"/download/{_record.Id}");

    [Fact]
    public void KeepsTheHitsForTheUi() => _record.Hits.Count.ShouldBe(2);

    [Fact]
    public void HandsThePipelineTheRequestItWasEnqueuedWith() =>
        _harness.Pipeline.Requests.Single().InputPath.ShouldEndWith("book.mp3");

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

public class WhenACompletedJobWasRescanned : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly JobRecord _record;

    public WhenACompletedJobWasRescanned()
    {
        _harness.Pipeline.Behaviour = (_, _, _) =>
            Task.FromResult(StubMediaPipeline.Completed(hits: 1, rescanned: true));
        _harness.StartAsync().GetAwaiter().GetResult();

        var queued = _harness.Enqueue("book.mp3");
        _record = _harness
            .WaitForStatusAsync(queued.Id, JobStatus.Completed)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void SaysSoInTheDetailLine() => _record.Detail.ShouldBe("1 hit(s) (rescanned)");

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

public class WhileAJobIsRunning : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly Gate _gate = new();
    private readonly JobRecord _record;

    public WhileAJobIsRunning()
    {
        _harness.Pipeline.Behaviour = async (_, progress, _) =>
        {
            progress!.Report(new JobProgress("transcribing", 40, "Transcribing audio"));
            await _gate.PassAsync();
            return StubMediaPipeline.Completed();
        };
        _harness.StartAsync().GetAwaiter().GetResult();

        var queued = _harness.Enqueue("book.mp3");
        _record = _harness
            .WaitForAsync(queued.Id, job => job.Progress == 40)
            .GetAwaiter()
            .GetResult();

        _record.ShouldNotBeNull();
    }

    [Fact]
    public void ShowsTheStageThePipelineReported() => _record.Stage.ShouldBe("transcribing");

    [Fact]
    public void ShowsTheDetailThePipelineReported() =>
        _record.Detail.ShouldBe("Transcribing audio");

    [Fact]
    public void StaysInTheProcessingStatus() => _record.Status.ShouldBe(JobStatus.Processing);

    public void Dispose()
    {
        _gate.Open();
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// One GPU, so jobs are strictly sequential no matter how many arrive at once.
/// </summary>
public class WhenJobsAreEnqueuedConcurrently : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly List<JobRecord> _finished = [];

    public WhenJobsAreEnqueuedConcurrently()
    {
        _harness.Pipeline.Behaviour = async (_, _, token) =>
        {
            await Task.Delay(20, token);
            return StubMediaPipeline.Completed();
        };
        _harness.StartAsync().GetAwaiter().GetResult();

        var ids = Task.WhenAll(
                Enumerable
                    .Range(0, 6)
                    .Select(i => Task.Run(() => _harness.Enqueue($"book{i}.mp3").Id))
            )
            .GetAwaiter()
            .GetResult();

        foreach (var id in ids)
        {
            _finished.Add(
                _harness.WaitForStatusAsync(id, JobStatus.Completed).GetAwaiter().GetResult()
            );
        }

        _finished.Count.ShouldBe(6);
    }

    [Fact]
    public void NeverRunsTwoAtOnce() => _harness.Pipeline.PeakConcurrency.ShouldBe(1);

    [Fact]
    public void RunsEveryOneOfThem() => _harness.Pipeline.Requests.Count.ShouldBe(6);

    [Fact]
    public void GivesEachOneItsOwnScratchDirectory() =>
        _harness.Pipeline.Requests.Select(r => r.ScratchDirectory).Distinct().Count().ShouldBe(6);

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

public class WhenAJobThrows : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly JobRecord _failed;
    private readonly JobRecord _next;

    public WhenAJobThrows()
    {
        var first = true;
        _harness.Pipeline.Behaviour = (_, _, _) =>
        {
            if (!first)
            {
                return Task.FromResult(StubMediaPipeline.Completed());
            }

            first = false;
            throw new InvalidOperationException("ffmpeg exited with code 1");
        };
        _harness.StartAsync().GetAwaiter().GetResult();

        var doomed = _harness.Enqueue("bad.mp3");
        var healthy = _harness.Enqueue("good.mp3");

        _failed = _harness.WaitForStatusAsync(doomed.Id, JobStatus.Failed).GetAwaiter().GetResult();
        _next = _harness
            .WaitForStatusAsync(healthy.Id, JobStatus.Completed)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void ReportsTheFailureToTheUi() => _failed.Detail.ShouldBe("ffmpeg exited with code 1");

    [Fact]
    public void DoesNotOfferADownload() => _failed.DownloadUrl.ShouldBeNull();

    [Fact]
    public void RemovesTheUploadedInput() => File.Exists(_failed.Request.InputPath).ShouldBeFalse();

    [Fact]
    public void RemovesTheJobScratchDirectory() =>
        Directory.Exists(_failed.Request.ScratchDirectory).ShouldBeFalse();

    [Fact]
    public void IsolatesTheFailureToThatOneJob() => _next.Status.ShouldBe(JobStatus.Completed);

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

public class WhenAJobThrowsAVeryLongMessage : IDisposable
{
    private readonly JobHarness _harness = new();
    private readonly JobRecord _failed;

    public WhenAJobThrowsAVeryLongMessage()
    {
        _harness.Pipeline.Behaviour = (_, _, _) =>
            throw new InvalidOperationException(new string('x', 900));
        _harness.StartAsync().GetAwaiter().GetResult();

        var doomed = _harness.Enqueue("bad.mp3");
        _failed = _harness.WaitForStatusAsync(doomed.Id, JobStatus.Failed).GetAwaiter().GetResult();
    }

    [Fact]
    public void TrimsItSoItCannotFloodTheUi() => _failed.Detail.Length.ShouldBe(500);

    public void Dispose()
    {
        _harness.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
