using FoulFilterNet.Cli;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Cli.Tests;

/// <summary>
/// One run of the terminal wrapper: a real parse, a stub prober and a stub
/// pipeline, with both console streams captured.
/// </summary>
internal sealed class RunnerHarness : IDisposable
{
    private readonly TempDirectory _directory = new();

    internal RunnerHarness(StubProber prober, StubPipeline pipeline, params string[] flags)
    {
        Prober = prober;
        Pipeline = pipeline;

        InputPath = _directory.File("clip.mp3");
        System.IO.File.WriteAllText(InputPath, "not really audio");

        var commandLine = new FoulFilterCommandLine();
        var parsed = commandLine.Parse([InputPath, _directory.File("bad_words.txt"), .. flags]);
        parsed.Errors.ShouldBeEmpty();

        Request = commandLine.ToRequest(parsed, _ => null);

        var runner = new JobRunner(prober, pipeline, Output, Error);
        ExitCode = runner.RunAsync(Request).GetAwaiter().GetResult();
    }

    internal StubProber Prober { get; }

    internal StubPipeline Pipeline { get; }

    internal string InputPath { get; }

    internal JobRequest Request { get; }

    internal StringWriter Output { get; } = new();

    internal StringWriter Error { get; } = new();

    internal int ExitCode { get; }

    internal string Printed => Output.ToString();

    internal string Complained => Error.ToString();

    public void Dispose()
    {
        Output.Dispose();
        Error.Dispose();
        _directory.Dispose();
    }
}

/// <summary>
/// The Python printed one line and exited 1 rather than handing an unknown file
/// to the pipeline. FFprobe answers the question libmagic used to.
/// </summary>
public sealed class WhenTheFileIsNotRecognisedMedia : IDisposable
{
    private readonly RunnerHarness _run = new(
        new StubProber(MediaKind.Unknown),
        new StubPipeline()
    );

    public void Dispose() => _run.Dispose();

    [Fact]
    public void Fails() => _run.ExitCode.ShouldBe(1);

    [Fact]
    public void SaysWhichFileItCouldNotRead() =>
        _run.Complained.ShouldContain($"Unrecognized file type: {_run.InputPath}");

    [Fact]
    public void NeverStartsTheJob() => _run.Pipeline.Requests.ShouldBeEmpty();
}

/// <summary>The reporting format is the Python's, column widths included.</summary>
public sealed class WhenTheJobFindsHits : IDisposable
{
    private readonly RunnerHarness _run = new(
        new StubProber(MediaKind.Audio),
        new StubPipeline([new Hit("hell", 1.0, 1.5), new Hit("damn", 2.25, 2.5)])
    );

    public void Dispose() => _run.Dispose();

    [Fact]
    public void Succeeds() => _run.ExitCode.ShouldBe(0);

    [Fact]
    public void CountsThem() => _run.Printed.ShouldContain("2 hit(s):");

    [Fact]
    public void PrintsEachOneInTheLegacyColumns() =>
        _run.Printed.ShouldContain("      1.00 -     1.50  hell");

    [Fact]
    public void ReportsWhereTheOutputWent() =>
        _run.Printed.ShouldContain($"Done. Output: {_run.Request.OutputPath}");

    [Fact]
    public void WritesTheOutput() => File.Exists(_run.Request.OutputPath).ShouldBeTrue();
}

/// <summary>A clean file still reports, rather than printing nothing at all.</summary>
public sealed class WhenTheJobFindsNothing : IDisposable
{
    private readonly RunnerHarness _run = new(new StubProber(MediaKind.Video), new StubPipeline());

    public void Dispose() => _run.Dispose();

    [Fact]
    public void Succeeds() => _run.ExitCode.ShouldBe(0);

    [Fact]
    public void SaysSo() => _run.Printed.ShouldContain("No inappropriate words found.");
}

/// <summary>
/// <c>--no_edit</c> is <see cref="JobRequest.Render"/> turned off, and nothing
/// else: the analysis runs, the report prints, no file is written.
/// </summary>
public sealed class WhenAnalysisOnlyIsRequested : IDisposable
{
    private readonly RunnerHarness _run = new(
        new StubProber(MediaKind.Audio),
        new StubPipeline([new Hit("hell", 1.0, 1.5)]),
        "--no_edit"
    );

    public void Dispose() => _run.Dispose();

    [Fact]
    public void Succeeds() => _run.ExitCode.ShouldBe(0);

    [Fact]
    public void AsksThePipelineNotToRender() => _run.Pipeline.Requests[0].Render.ShouldBeFalse();

    [Fact]
    public void StillReportsTheHit() => _run.Printed.ShouldContain("1 hit(s):");

    [Fact]
    public void SaysNothingWasWritten() =>
        _run.Printed.ShouldContain("Done. Output: (not written)");

    [Fact]
    public void WritesNoOutputFile() => File.Exists(_run.Request.OutputPath).ShouldBeFalse();
}

/// <summary>
/// Stage and percent reach the terminal. The web UI has a progress bar; a
/// terminal gets the same checkpoints as text.
/// </summary>
public sealed class WhenThePipelineReportsProgress : IDisposable
{
    private readonly RunnerHarness _run;

    public WhenThePipelineReportsProgress()
    {
        var pipeline = new StubPipeline();
        pipeline.Checkpoints.Add(new JobProgress("Transcribing", 40));
        pipeline.Checkpoints.Add(new JobProgress("Rendering", 90, "censored_clip.mp3"));

        _run = new RunnerHarness(new StubProber(MediaKind.Audio), pipeline);
    }

    public void Dispose() => _run.Dispose();

    [Fact]
    public void NamesEachStage() => _run.Printed.ShouldContain("Transcribing");

    [Fact]
    public void ShowsThePercentage() => _run.Printed.ShouldContain("40%");

    [Fact]
    public void CarriesTheDetail() => _run.Printed.ShouldContain("censored_clip.mp3");
}

/// <summary>
/// A failed job reports its reason and exits non-zero instead of printing a
/// stack trace, which is what the Python did.
/// </summary>
public sealed class WhenTheJobFails : IDisposable
{
    private readonly RunnerHarness _run = new(
        new StubProber(MediaKind.Audio),
        new StubPipeline(failure: new InvalidOperationException("ffmpeg exited with 1"))
    );

    public void Dispose() => _run.Dispose();

    [Fact]
    public void Fails() => _run.ExitCode.ShouldBe(1);

    [Fact]
    public void SaysWhy() => _run.Complained.ShouldContain("ffmpeg exited with 1");
}

/// <summary>
/// A path that does not exist, or that FFprobe cannot open, fails before the
/// pipeline is involved - and still as one line rather than a stack trace.
/// </summary>
public sealed class WhenTheFileCannotBeProbed : IDisposable
{
    private readonly RunnerHarness _run = new(
        new StubProber(MediaKind.Audio, new FileNotFoundException("clip.mp3 does not exist")),
        new StubPipeline()
    );

    public void Dispose() => _run.Dispose();

    [Fact]
    public void Fails() => _run.ExitCode.ShouldBe(1);

    [Fact]
    public void SaysWhy() => _run.Complained.ShouldContain("clip.mp3 does not exist");

    [Fact]
    public void NeverStartsTheJob() => _run.Pipeline.Requests.ShouldBeEmpty();
}

/// <summary>Ctrl+C is not a crash; the shell convention for it is 130.</summary>
public sealed class WhenTheJobIsCancelled : IDisposable
{
    private readonly RunnerHarness _run = new(
        new StubProber(MediaKind.Audio),
        new StubPipeline(failure: new JobCancelledException())
    );

    public void Dispose() => _run.Dispose();

    [Fact]
    public void ReportsTheInterruptedExitCode() => _run.ExitCode.ShouldBe(130);

    [Fact]
    public void SaysItWasCancelled() => _run.Complained.ShouldContain("Cancelled");
}
