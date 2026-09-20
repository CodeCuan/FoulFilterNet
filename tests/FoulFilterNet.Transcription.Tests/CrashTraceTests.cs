using FoulFilterNet.Transcription;
using Shouldly;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// The trace is process-wide static state, so these run one at a time and put
/// it back the way they found it.
/// </summary>
[Collection(nameof(CrashTraceTests))]
public abstract class CrashTraceFacts : IDisposable
{
    private readonly TempDirectory _directory = new();

    protected CrashTraceFacts() => Path = System.IO.Path.Combine(Folder, "trace.log");

    /// <summary>The directory this test's trace file lives in.</summary>
    protected string Folder => _directory.Path;

    /// <summary>Where this test traces to.</summary>
    protected string Path { get; }

    /// <summary>
    /// Read while the trace is still open, which is what a crash hunt does:
    /// the writer holds the file, so a reader has to allow for that.
    /// </summary>
    protected string Contents()
    {
        using var file = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        using var reader = new StreamReader(file);

        return reader.ReadToEnd();
    }

    protected string[] Lines() => Contents().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        CrashTrace.Stop();
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

[CollectionDefinition(nameof(CrashTraceTests), DisableParallelization = true)]
public sealed class CrashTraceTests;

public sealed class WhenTracingIsOff : CrashTraceFacts
{
    public WhenTracingIsOff() => CrashTrace.Stop();

    [Fact]
    public void ItIsNotEnabled() => CrashTrace.IsEnabled.ShouldBeFalse();

    [Fact]
    public void ItHasNoPath() => CrashTrace.Path.ShouldBeNull();

    [Fact]
    public void WritingDoesNothing()
    {
        CrashTrace.Write("step", "detail");

        File.Exists(Path).ShouldBeFalse();
    }

    [Fact]
    public void StoppingAgainIsHarmless() => Should.NotThrow(CrashTrace.Stop);
}

public sealed class WhenTracingIsOn : CrashTraceFacts
{
    public WhenTracingIsOn() => CrashTrace.Start(Path).ShouldBeTrue();

    [Fact]
    public void ItIsEnabled() => CrashTrace.IsEnabled.ShouldBeTrue();

    [Fact]
    public void ItReportsTheFullPath() =>
        CrashTrace.Path.ShouldBe(System.IO.Path.GetFullPath(Path));

    [Fact]
    public void ItRecordsTheProcessItOpenedIn() => Contents().ShouldContain("trace.open");

    [Fact]
    public void ItNamesTheStep()
    {
        CrashTrace.Write("window.native.begin", "samples=448000");

        Contents().ShouldContain("window.native.begin samples=448000");
    }

    [Fact]
    public void EachStepIsOnItsOwnLine()
    {
        CrashTrace.Write("first");
        CrashTrace.Write("second");

        Lines().Length.ShouldBe(3);
    }

    [Fact]
    public void AStepIsReadableBeforeTheNextOne()
    {
        // The whole point: a process that dies here still leaves the line.
        CrashTrace.Write("window.native.begin");

        Contents().ShouldContain("window.native.begin");
    }

    [Fact]
    public void TheFileCanBeReadWhileItIsOpen() =>
        Should.NotThrow(() =>
            new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite).Dispose()
        );

    [Fact]
    public void ADetailIsOptional()
    {
        CrashTrace.Write("session.head.done");

        Contents().ShouldContain("session.head.done");
    }

    [Fact]
    public void EveryLineCarriesTheTimeSinceStartUp() => Contents().ShouldContain("+");

    [Fact]
    public void WritingFromManyThreadsKeepsEveryLine()
    {
        Parallel.For(0, 200, i => CrashTrace.Write("concurrent", $"i={i}"));

        Lines().Count(line => line.Contains("concurrent", StringComparison.Ordinal)).ShouldBe(200);
    }

    [Fact]
    public void WritingAfterStoppingDoesNothing()
    {
        CrashTrace.Stop();
        CrashTrace.Write("after");

        Contents().ShouldNotContain("after");
    }

    [Fact]
    public void RestartingAppendsRatherThanTruncates()
    {
        CrashTrace.Write("before-restart");
        CrashTrace.Stop();
        CrashTrace.Start(Path);

        Contents().ShouldContain("before-restart");
    }
}

public sealed class WhenThePathCannotBeUsed : CrashTraceFacts
{
    [Fact]
    public void AnUnusableDirectoryLeavesTracingOff()
    {
        // A file where the directory would have to be.
        var blocker = System.IO.Path.Combine(Folder, "blocker");
        File.WriteAllText(blocker, "");

        CrashTrace.Start(System.IO.Path.Combine(blocker, "trace.log")).ShouldBeFalse();
        CrashTrace.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void ABlankPathIsRejected() =>
        Should.Throw<ArgumentException>(() => CrashTrace.Start("  "));
}
