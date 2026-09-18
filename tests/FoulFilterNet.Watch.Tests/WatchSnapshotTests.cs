using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

internal static class Snapshots
{
    public static WatchSnapshot Of(
        WatchState state,
        int done = 0,
        int total = 0,
        double? realtimeFactor = null,
        HitSnapshot? analysis = null
    ) =>
        new(
            Harness.Video,
            state,
            Reason: null,
            FailureKind: null,
            Title: null,
            DurationSeconds: null,
            analysis,
            done,
            total,
            realtimeFactor,
            FromCache: false
        );
}

public class WhenASnapshotHasNoAnalysisYet
{
    private readonly WatchSnapshot _snapshot = Snapshots.Of(WatchState.Fetching);

    [Fact]
    public void IsAtRevisionZero() => _snapshot.Revision.ShouldBe(0);

    [Fact]
    public void HasEmptyCoverage() => _snapshot.Coverage.ShouldBeSameAs(Coverage.Empty);

    [Fact]
    public void HasNoProgress() => _snapshot.Progress.ShouldBe(0.0);

    [Fact]
    public void IsKeyedByItsVideo() => _snapshot.Key.ShouldBe(Harness.Video.Key);
}

public class WhenASnapshotHasAnAnalysis
{
    private readonly HitSnapshot _analysis = HundredSecondVideo.Finish(0, 1).Snapshot;
    private readonly WatchSnapshot _snapshot;

    public WhenASnapshotHasAnAnalysis()
    {
        _snapshot = Snapshots.Of(WatchState.Transcribing, 2, 5, analysis: _analysis);
    }

    [Fact]
    public void ServesItsRevision() => _snapshot.Revision.ShouldBe(2);

    [Fact]
    public void ServesItsCoverage() => _snapshot.Coverage.ShouldBeSameAs(_analysis.Coverage);

    [Fact]
    public void ReportsTheShareOfWindowsDone() => _snapshot.Progress.ShouldBe(0.4);
}

public class WhenJudgingASnapshotsProgress
{
    [Fact]
    public void IsWholeOnceComplete() =>
        Snapshots.Of(WatchState.Complete, done: 1, total: 5).Progress.ShouldBe(1.0);

    [Fact]
    public void IsWholeForACompleteSessionWithNoPlan() =>
        Snapshots.Of(WatchState.Complete).Progress.ShouldBe(1.0);

    [Fact]
    public void IsNothingWithNoPlan() => Snapshots.Of(WatchState.Preparing).Progress.ShouldBe(0.0);

    [Fact]
    public void KeepsItsShareWhenFailed() =>
        Snapshots.Of(WatchState.Failed, done: 1, total: 4).Progress.ShouldBe(0.25);
}

public class WhenJudgingWhetherASessionIsKeepingUp
{
    [Fact]
    public void IsNotWhenTranscribingSlowerThanRealTime() =>
        Snapshots.Of(WatchState.Transcribing, realtimeFactor: 0.5).IsKeepingUp.ShouldBeFalse();

    [Fact]
    public void IsAtExactlyRealTime() =>
        Snapshots.Of(WatchState.Transcribing, realtimeFactor: 1.0).IsKeepingUp.ShouldBeTrue();

    [Fact]
    public void IsWhenFasterThanRealTime() =>
        Snapshots.Of(WatchState.Transcribing, realtimeFactor: 30.0).IsKeepingUp.ShouldBeTrue();

    [Fact]
    public void IsBeforeAnyMeasurement() =>
        Snapshots.Of(WatchState.Transcribing).IsKeepingUp.ShouldBeTrue();

    [Theory]
    [InlineData(WatchState.Complete)]
    [InlineData(WatchState.Failed)]
    [InlineData(WatchState.Cancelled)]
    public void IsOnceThereIsNothingLeftToTranscribe(WatchState state) =>
        Snapshots.Of(state, realtimeFactor: 0.1).IsKeepingUp.ShouldBeTrue();
}

public class WhenASnapshotIsCompared
{
    [Fact]
    public void EqualsOneWithTheSameValues() =>
        Snapshots.Of(WatchState.Queued).ShouldBe(Snapshots.Of(WatchState.Queued));

    [Fact]
    public void DiffersFromOneInAnotherState() =>
        Snapshots.Of(WatchState.Queued).ShouldNotBe(Snapshots.Of(WatchState.Fetching));
}

public class WhenAskingAboutAWatchState
{
    [Theory]
    [InlineData(WatchState.Complete)]
    [InlineData(WatchState.Failed)]
    [InlineData(WatchState.Unsupported)]
    [InlineData(WatchState.Cancelled)]
    public void CountsAnEndAsTerminal(WatchState state) => state.IsTerminal().ShouldBeTrue();

    [Theory]
    [InlineData(WatchState.Queued)]
    [InlineData(WatchState.Fetching)]
    [InlineData(WatchState.Preparing)]
    [InlineData(WatchState.Transcribing)]
    public void CountsWorkAsNotTerminal(WatchState state) => state.IsTerminal().ShouldBeFalse();

    [Theory]
    [InlineData(WatchState.Failed)]
    [InlineData(WatchState.Unsupported)]
    public void CountsFailedAndUnsupportedAsFailures(WatchState state) =>
        state.IsFailure().ShouldBeTrue();

    [Theory]
    [InlineData(WatchState.Queued)]
    [InlineData(WatchState.Transcribing)]
    [InlineData(WatchState.Complete)]
    [InlineData(WatchState.Cancelled)]
    public void CountsEverythingElseAsNoFailure(WatchState state) =>
        state.IsFailure().ShouldBeFalse();
}

public class WhenWatchOptionsAreLeftAtTheirDefaults
{
    private readonly WatchOptions _options = new();

    [Fact]
    public void ReadsTheWatchSection() => WatchOptions.SectionName.ShouldBe("Watch");

    [Fact]
    public void IdlesOutAfterTwoMinutes() => _options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(2));

    [Fact]
    public void RetainsACompleteSessionForHalfAnHour() =>
        _options.CompletedRetention.ShouldBe(TimeSpan.FromMinutes(30));

    [Fact]
    public void RetainsAFailedSessionForAMinute() =>
        _options.FailedRetention.ShouldBe(TimeSpan.FromMinutes(1));

    [Fact]
    public void SweepsEveryFifteenSeconds() =>
        _options.SweepInterval.ShouldBe(TimeSpan.FromSeconds(15));

    [Fact]
    public void WorksUnderTheDataDirectory() =>
        _options.ScratchDirectory.ShouldEndWith(Path.Combine("scratch", "watch"));

    [Fact]
    public void SharesTheJobsTranscriptCache() =>
        _options.TranscriptDirectory.ShouldBe(
            FoulFilterNet.Pipeline.DataLocations.TranscriptDirectory(null, null)
        );

    [Fact]
    public void SharesTheJobsBadWordsList() =>
        _options.BadWordsPath.ShouldBe(
            FoulFilterNet.Pipeline.DataLocations.BadWordsPath(null, null)
        );

    [Fact]
    public void AreValid() => Should.NotThrow(_options.Validate);
}

public class WhenWatchOptionsCouldNotWork
{
    [Theory]
    [InlineData(nameof(WatchOptions.IdleTimeout), 0)]
    [InlineData(nameof(WatchOptions.IdleTimeout), -1)]
    [InlineData(nameof(WatchOptions.CompletedRetention), 0)]
    [InlineData(nameof(WatchOptions.FailedRetention), 0)]
    [InlineData(nameof(WatchOptions.SweepInterval), 0)]
    [InlineData(nameof(WatchOptions.SweepInterval), -5)]
    public void RefusesASpanThatIsNotPositive(string property, int seconds)
    {
        var options = new WatchOptions();
        typeof(WatchOptions)
            .GetProperty(property)!
            .SetValue(options, TimeSpan.FromSeconds(seconds));

        Should.Throw<ArgumentOutOfRangeException>(options.Validate);
    }

    [Theory]
    [InlineData(nameof(WatchOptions.ScratchDirectory))]
    [InlineData(nameof(WatchOptions.TranscriptDirectory))]
    [InlineData(nameof(WatchOptions.BadWordsPath))]
    public void RefusesABlankPath(string property)
    {
        var options = new WatchOptions();
        typeof(WatchOptions).GetProperty(property)!.SetValue(options, "  ");

        Should.Throw<ArgumentException>(options.Validate);
    }
}
