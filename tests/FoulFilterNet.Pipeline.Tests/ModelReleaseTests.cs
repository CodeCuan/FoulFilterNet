using FoulFilterNet.Domain;
using NSubstitute;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>
/// UNLOAD_MODELS_AFTER_JOB, the pipeline's half: every job releases the engines
/// it may have loaded, and it does so last, so the VRAM is handed back after the
/// render rather than while FFmpeg still has work to do.
/// </summary>
/// <remarks>
/// There is no flag in here, exactly as there is none for Smart Cut: the
/// pipeline always releases and the flag decides what releasing means. See
/// <c>ReleasePolicyTranscriber</c>.
/// </remarks>
public sealed class WhenAJobSucceeds : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenAJobSucceeds()
    {
        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
        _harness.Stages[^1].ShouldBe(("completed", 100));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task ReleasesTheTranscriber() =>
        await _harness.Transcriber.Received(1).ReleaseAsync();

    [Fact]
    public async Task ReleasesTheAlignerToo() =>
        await _harness.Aligner.Received(1).ReleaseAsync();

    [Fact]
    public void ReleasesOnlyAfterTheRenderHasFinishedWithTheFile() => _harness.EngineCalls.ShouldBe(
    [
        PipelineHarness.TranscribeCall,
        PipelineHarness.RenderCall,
        PipelineHarness.ReleaseTranscriberCall,
        PipelineHarness.ReleaseAlignerCall,
    ]);
}

/// <summary>
/// The case the flag exists for: a job that died still has to hand the card
/// back, or one bad file keeps 12 GB of VRAM until the process is restarted.
/// </summary>
public sealed class WhenAJobFails : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenAJobFails()
    {
        _harness.EditorRenders = false;
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldBeOfType<InvalidOperationException>();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task StillReleasesTheTranscriber() =>
        await _harness.Transcriber.Received(1).ReleaseAsync();

    [Fact]
    public async Task StillReleasesTheAligner() =>
        await _harness.Aligner.Received(1).ReleaseAsync();

    [Fact]
    public void ReleasesAfterTheRenderItAttempted() =>
        _harness.EngineCalls.ShouldBe(
        [
            PipelineHarness.TranscribeCall,
            PipelineHarness.RenderCall,
            PipelineHarness.ReleaseTranscriberCall,
            PipelineHarness.ReleaseAlignerCall,
        ]);

    [Fact]
    public void ReportsTheFailureThatActuallyHappened() =>
        _failure.Message.ShouldContain(_harness.OutputPath);
}

/// <summary>A cancelled job is the other abandoned path, and it leaks the same VRAM.</summary>
public sealed class WhenAJobIsCancelledPartWayThrough : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenAJobIsCancelledPartWayThrough()
    {
        _harness.CancelAt(50);
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldBeOfType<JobCancelledException>();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task StillReleasesTheTranscriber() =>
        await _harness.Transcriber.Received(1).ReleaseAsync();

    [Fact]
    public async Task StillReleasesTheAligner() =>
        await _harness.Aligner.Received(1).ReleaseAsync();

    [Fact]
    public void NeverRendered() => _harness.EngineCalls.ShouldNotContain(PipelineHarness.RenderCall);
}

/// <summary>
/// A job cancelled before it did anything at all: nothing was ever loaded, and
/// releasing has to be safe anyway rather than a way of failing on the way out.
/// </summary>
public sealed class WhenAJobIsCancelledBeforeAnythingIsLoaded : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenAJobIsCancelledBeforeAnythingIsLoaded()
    {
        _harness.Cancel();
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldBeOfType<JobCancelledException>();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task ReleasesAnyway() =>
        await _harness.Transcriber.Received(1).ReleaseAsync();

    [Fact]
    public void NeverTranscribed() =>
        _harness.EngineCalls.ShouldNotContain(PipelineHarness.TranscribeCall);
}

/// <summary>
/// Resume (ADR-0002) never loads a model, so releasing is being asked to drop
/// something that was never there. It has to be idempotent for that reason.
/// </summary>
public sealed class WhenAJobResumedACachedTranscript : IDisposable
{
    private readonly PipelineHarness _harness = new();

    public WhenAJobResumedACachedTranscript()
    {
        _harness.SeedTranscript();
        _harness.Run();

        _harness.Stages.ShouldContain(("transcribing", 45));
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task StillReleasesOnTheWayOut() =>
        await _harness.Transcriber.Received(1).ReleaseAsync();

    [Fact]
    public void NeverTranscribedInTheFirstPlace() =>
        _harness.EngineCalls.ShouldNotContain(PipelineHarness.TranscribeCall);
}

/// <summary>
/// Releasing is housekeeping, not part of the job: a driver that refuses to
/// unload must not turn a finished job into a failed one. The Python logged and
/// moved on, and so does this.
/// </summary>
public sealed class WhenReleasingTheModelFailsAfterASuccessfulJob : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly JobSummary _summary;

    public WhenReleasingTheModelFailsAfterASuccessfulJob()
    {
        _harness.ReleaseThrows = true;
        _summary = _harness.Run();

        _summary.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StillReportsTheJobFinished() => _harness.Stages[^1].ShouldBe(("completed", 100));

    [Fact]
    public void StillReportsWhatItCensored() => _summary.Hits.Count.ShouldBe(1);

    [Fact]
    public void StillLeftTheCensoredFileBehind() => File.Exists(_harness.OutputPath).ShouldBeTrue();

    [Fact]
    public async Task StillReleasesTheAlignerDespiteTheTranscriberThrowing() =>
        await _harness.Aligner.Received(1).ReleaseAsync();
}

/// <summary>
/// And from the other side: a release that fails while the job is already
/// failing must not replace the failure the caller needs to see.
/// </summary>
public sealed class WhenReleasingTheModelFailsOnTopOfAFailedJob : IDisposable
{
    private readonly PipelineHarness _harness = new();
    private readonly Exception _failure;

    public WhenReleasingTheModelFailsOnTopOfAFailedJob()
    {
        _harness.EditorRenders = false;
        _harness.ReleaseThrows = true;
        _failure = _harness.RunExpectingFailure();

        _failure.ShouldNotBeNull();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void KeepsTheFailureTheJobActuallyHad() =>
        _failure.ShouldBeOfType<InvalidOperationException>();

    [Fact]
    public void NamesTheOutputThatNeverAppeared() =>
        _failure.Message.ShouldContain(_harness.OutputPath);
}
