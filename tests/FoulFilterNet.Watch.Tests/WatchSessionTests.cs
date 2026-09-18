using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Sources;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

public sealed class WhenAWatchSessionIsJustMade : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _snapshot;

    public WhenAWatchSessionIsJustMade()
    {
        _session = _harness.Session();
        _snapshot = _session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsQueued() => _session.State.ShouldBe(WatchState.Queued);

    [Fact]
    public void SnapshotsAsQueued() => _snapshot.State.ShouldBe(WatchState.Queued);

    [Fact]
    public void IsForItsVideo() => _snapshot.Key.ShouldBe("youtube-dQw4w9WgXcQ");

    [Fact]
    public void HasNoAnalysis() => _snapshot.Analysis.ShouldBeNull();

    [Fact]
    public void HasNoCoverage() => _snapshot.Coverage.IsEmpty.ShouldBeTrue();

    [Fact]
    public void IsAtRevisionZero() => _snapshot.Revision.ShouldBe(0);

    [Fact]
    public void HasNoProgress() => _snapshot.Progress.ShouldBe(0.0);

    [Fact]
    public void HasNoWindowsPlanned() => _snapshot.WindowsTotal.ShouldBe(0);

    [Fact]
    public void HasNoReason() => _snapshot.Reason.ShouldBeNull();

    [Fact]
    public void HasNoDuration() => _snapshot.DurationSeconds.ShouldBeNull();

    [Fact]
    public void CountsAsKeepingUp() => _snapshot.IsKeepingUp.ShouldBeTrue();

    [Fact]
    public void HasNotEnded() => _session.EndedAt.ShouldBeNull();

    [Fact]
    public void IsNotRunning() => _session.Completion.IsCompleted.ShouldBeFalse();

    [Fact]
    public void CountsAsHeardWhenMade() => _session.LastHeartbeatAt.ShouldBe(Harness.Epoch);

    [Fact]
    public void HasItsPlayheadAtZero() => _session.PlayheadSeconds.ShouldBe(0.0);

    [Fact]
    public void HasNotFetchedAnything() => _harness.Source.Calls.ShouldBe(0);

    [Fact]
    public void HasNotLookedInTheCache() => _harness.Store.Finds.ShouldBe(0);
}

public sealed class WhenTheTranscriptIsCached : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _snapshot;

    public WhenTheTranscriptIsCached()
    {
        _harness.Store.Cached = Harness.CachedTranscript(Harness.Video.Key);
        _session = Harness.Run(_harness.Session());
        _snapshot = _session.Snapshot();

        _snapshot.State.ShouldBe(WatchState.Complete);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void NeverFetchesTheAudio() => _harness.Source.Calls.ShouldBe(0);

    [Fact]
    public void NeverPreparesAWav() => _harness.Preparer.Calls.ShouldBe(0);

    [Fact]
    public void NeverOpensTheEngine() => _harness.Engine.Opened.ShouldBeEmpty();

    [Fact]
    public void SaysItCameFromTheCache() => _snapshot.FromCache.ShouldBeTrue();

    [Fact]
    public void FindsTheBatchPipelinesHits() =>
        _snapshot.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(HundredSecondVideo.BadWords));

    [Fact]
    public void IsCompleteCoverage() => _snapshot.Coverage.IsComplete.ShouldBeTrue();

    [Fact]
    public void CoversUpToTheLastWordHeard() =>
        _snapshot.Coverage.Intervals.ShouldBe([new CoverageInterval(0.0, 90.4)]);

    [Fact]
    public void TakesTheDurationFromTheLastWordHeard() => _snapshot.DurationSeconds.ShouldBe(90.4);

    [Fact]
    public void CountsAsOneWindowOfOne() =>
        (_snapshot.WindowsDone, _snapshot.WindowsTotal).ShouldBe((1, 1));

    [Fact]
    public void IsAllTheWayThrough() => _snapshot.Progress.ShouldBe(1.0);

    [Fact]
    public void IsAtRevisionOne() => _snapshot.Revision.ShouldBe(1);

    [Fact]
    public void ServesTheCachedTranscript() =>
        _snapshot.Analysis!.Transcript.Words.ShouldBe(Harness.Script.Batch().Words);

    [Fact]
    public void SavesNothing() => _harness.Store.Saves.ShouldBeEmpty();

    [Fact]
    public void HasNoTitle() => _snapshot.Title.ShouldBeNull();

    [Fact]
    public void HasNoRealtimeFactor() => _snapshot.RealtimeFactor.ShouldBeNull();

    [Fact]
    public void MakesNoScratchDirectory() => Directory.Exists(_harness.ScratchRoot).ShouldBeFalse();

    [Fact]
    public void HasEnded() => _session.EndedAt.ShouldBe(Harness.Epoch);
}

public sealed class WhenTheCachedTranscriptIsForAnIdDifferingOnlyInCase : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenTheCachedTranscriptIsForAnIdDifferingOnlyInCase()
    {
        // TranscriptStore matches digests ignoring case; YouTube IDs do not.
        _harness.Store.Cached = Harness.CachedTranscript("youtube-DQW4W9WGXCQ");
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void TreatsItAsAMiss() => _harness.Source.Calls.ShouldBe(1);

    [Fact]
    public void DoesNotClaimTheCache() => _snapshot.FromCache.ShouldBeFalse();

    [Fact]
    public void StillCompletes() => _snapshot.State.ShouldBe(WatchState.Complete);
}

public sealed class WhenAMissIsWatchedThroughEveryState : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _queued;
    private readonly WatchSnapshot _fetching;
    private readonly WatchSnapshot _preparing;
    private readonly WatchSnapshot _transcribing;
    private readonly WatchSnapshot _complete;

    public WhenAMissIsWatchedThroughEveryState()
    {
        var fetch = _harness.Source.Hold = new Gate();
        var prepare = _harness.Preparer.Hold = new Gate();
        var firstWindow = _harness.Engine.Hold(0);

        _session = _harness.Session();
        _queued = _session.Snapshot();
        _session.Start();

        fetch.WaitUntilEntered();
        _fetching = _session.Snapshot();
        fetch.Release();

        prepare.WaitUntilEntered();
        _preparing = _session.Snapshot();
        prepare.Release();

        firstWindow.WaitUntilEntered();
        _transcribing = _session.Snapshot();
        firstWindow.Release();

        Waits.On(_session.Completion);
        _complete = _session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StartsQueued() => _queued.State.ShouldBe(WatchState.Queued);

    [Fact]
    public void ThenFetches() => _fetching.State.ShouldBe(WatchState.Fetching);

    [Fact]
    public void ThenPrepares() => _preparing.State.ShouldBe(WatchState.Preparing);

    [Fact]
    public void ThenTranscribes() => _transcribing.State.ShouldBe(WatchState.Transcribing);

    [Fact]
    public void EndsComplete() => _complete.State.ShouldBe(WatchState.Complete);

    [Fact]
    public void HasNoTitleWhileFetching() => _fetching.Title.ShouldBeNull();

    [Fact]
    public void ShowsTheTitleOnceFetched() => _preparing.Title.ShouldBe("Me at the zoo");

    [Fact]
    public void ShowsYtDlpsDurationWhilePreparing() => _preparing.DurationSeconds.ShouldBe(101.0);

    [Fact]
    public void ShowsTheWavsDurationOnceTranscribing() =>
        _transcribing.DurationSeconds.ShouldBe(100.0);

    [Fact]
    public void HasNoAnalysisWhilePreparing() => _preparing.Analysis.ShouldBeNull();

    [Fact]
    public void PlansTheWindowsBeforeTheFirstIsHeard() => _transcribing.WindowsTotal.ShouldBe(5);

    [Fact]
    public void HasNothingDoneDuringTheFirstWindow() => _transcribing.WindowsDone.ShouldBe(0);

    [Fact]
    public void ServesEmptyCoverageDuringTheFirstWindow() =>
        _transcribing.Coverage.IsEmpty.ShouldBeTrue();

    [Fact]
    public void IsAtRevisionZeroDuringTheFirstWindow() => _transcribing.Revision.ShouldBe(0);

    [Fact]
    public void KeepsTheTitleToTheEnd() => _complete.Title.ShouldBe("Me at the zoo");

    [Fact]
    public void FetchesIntoItsOwnDirectoryUnderScratch() =>
        Path.GetDirectoryName(_harness.Source.Directories.Single()).ShouldBe(_harness.ScratchRoot);

    [Fact]
    public void NamesItsDirectoryAfterTheVideo() =>
        Path.GetFileName(_harness.Source.Directories.Single())
            .ShouldStartWith("youtube-dQw4w9WgXcQ-");

    [Fact]
    public void PreparesTheDownloadedAudio() =>
        _harness.Preparer.Requests.Single().Input.ShouldBe(_harness.Source.AudioPaths.Single());

    [Fact]
    public void PreparesWithNoPadding() => _harness.Preparer.Requests.Single().Offset.ShouldBe(0.0);

    [Fact]
    public void OpensThePreparedWav() =>
        _harness.Engine.Opened.Single().Wav.ShouldBe(_harness.Preparer.Prepared.Single());

    [Fact]
    public void OpensAtHighPriority() =>
        _harness.Engine.Opened.Single().Priority.ShouldBe(InferencePriority.High);
}

public sealed class WhenAMissCompletes : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _snapshot;
    private readonly Transcript _saved;
    private readonly string _baseName;

    public WhenAMissCompletes()
    {
        _session = Harness.Run(_harness.Session());
        _snapshot = _session.Snapshot();
        (_saved, _baseName) = _harness.Store.Saves.ShouldHaveSingleItem();

        _snapshot.State.ShouldBe(WatchState.Complete);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SavesUnderTheVideosKey() => _saved.FileHash.ShouldBe("youtube-dQw4w9WgXcQ");

    [Fact]
    public void SavesTheCurrentSchemaVersion() =>
        _saved.Version.ShouldBe(Transcript.CurrentVersion);

    [Fact]
    public void SavesTheStitchedSegments() =>
        _saved.Segments.ShouldBe(Harness.Script.Batch().Segments);

    [Fact]
    public void SavesTheStitchedWords() => _saved.Words.ShouldBe(Harness.Script.Batch().Words);

    [Fact]
    public void NamesTheCacheFileAfterTheTitle() => _baseName.ShouldBe("Me at the zoo");

    [Fact]
    public void HearsEveryWindowOnce() =>
        _harness.Engine.Requested.Order().ShouldBe([0, 1, 2, 3, 4]);

    [Fact]
    public void HearsThemInOrderFromTheStart() =>
        _harness.Engine.Requested.ShouldBe([0, 1, 2, 3, 4]);

    [Fact]
    public void FindsTheBatchPipelinesHits() =>
        _snapshot.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(HundredSecondVideo.BadWords));

    [Fact]
    public void CoversTheWholeVideo() =>
        _snapshot.Coverage.Intervals.ShouldBe([new CoverageInterval(0.0, 100.0)]);

    [Fact]
    public void CountsEveryWindow() =>
        (_snapshot.WindowsDone, _snapshot.WindowsTotal).ShouldBe((5, 5));

    [Fact]
    public void IsAtOneRevisionPerWindow() => _snapshot.Revision.ShouldBe(5);

    [Fact]
    public void IsAllTheWayThrough() => _snapshot.Progress.ShouldBe(1.0);

    [Fact]
    public void DidNotComeFromTheCache() => _snapshot.FromCache.ShouldBeFalse();

    [Fact]
    public void HasNoReason() => _snapshot.Reason.ShouldBeNull();

    [Fact]
    public void DisposesTheAudio() => _harness.EveryAudioIsDisposed.ShouldBeTrue();

    [Fact]
    public void DeletesThePreparedWav() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();

    [Fact]
    public void DeletesItsScratchDirectory() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void LooksInTheCacheOnce() => _harness.Store.Finds.ShouldBe(1);

    [Fact]
    public void FetchesOnce() => _harness.Source.Calls.ShouldBe(1);

    [Fact]
    public void RecordsWhenItEnded() => _session.EndedAt.ShouldNotBeNull();

    [Fact]
    public void KeepsSavingToOneEvenWhenPolled()
    {
        _session.Snapshot();
        _session.Snapshot();

        _harness.Store.Saves.Count.ShouldBe(1);
    }
}

public sealed class WhenTheTitleIsBlank : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenTheTitleIsBlank()
    {
        _harness.Source.Title = "  ";
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ReportsNoTitle() => _snapshot.Title.ShouldBeNull();

    [Fact]
    public void NamesTheCacheFileAfterTheKey() =>
        _harness.Store.Saves.Single().BaseName.ShouldBe("youtube-dQw4w9WgXcQ");
}

public sealed class WhenThePlayheadMovesBetweenWindows : IDisposable
{
    private readonly Harness _harness = new();

    public WhenThePlayheadMovesBetweenWindows()
    {
        var first = _harness.Engine.Hold(0);
        var session = _harness.Session();
        session.Start();

        first.WaitUntilEntered();
        session.RecordHeartbeat(70.0); // window 3's share is [69, 83)
        first.Release();

        Waits.On(session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void JumpsToTheWindowHoldingThePlayheadAfterTheOneInFlight() =>
        _harness.Engine.Requested.Take(2).ShouldBe([0, 3]);

    [Fact]
    public void RunsOnToTheEndAndThenWrapsBackNearestFirst() =>
        _harness.Engine.Requested.ShouldBe([0, 3, 4, 2, 1]);

    [Fact]
    public void NeverRedoesAFinishedWindow() =>
        _harness.Engine.Requested.Distinct().Count().ShouldBe(5);
}

public sealed class WhenThePlayheadSeeksBackToAFinishedWindow : IDisposable
{
    private readonly Harness _harness = new();

    public WhenThePlayheadSeeksBackToAFinishedWindow()
    {
        var third = _harness.Engine.Hold(2);
        var session = _harness.Session();
        session.Start();

        third.WaitUntilEntered();
        session.RecordHeartbeat(10.0); // window 0, finished already
        third.Release();

        Waits.On(session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void CarriesOnFromTheNextUnfinishedWindow() =>
        _harness.Engine.Requested.ShouldBe([0, 1, 2, 3, 4]);
}

public sealed class WhenTheFirstHeartbeatIsMidVideo : IDisposable
{
    private readonly Harness _harness = new();

    public WhenTheFirstHeartbeatIsMidVideo()
    {
        var session = _harness.Session();
        session.RecordHeartbeat(50.0); // window 2's share is [47, 69)
        Harness.Run(session);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StartsWithTheWindowHoldingThePlayhead() =>
        _harness.Engine.Requested.First().ShouldBe(2);

    [Fact]
    public void HearsTheRestInTheSchedulersOrder() =>
        _harness.Engine.Requested.ShouldBe([2, 3, 4, 1, 0]);
}

public sealed class WhenAHeartbeatIsRecorded : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;

    public WhenAHeartbeatIsRecorded()
    {
        _session = _harness.Session();
        _harness.Time.Advance(TimeSpan.FromSeconds(42));
        _session.RecordHeartbeat(12.5);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void KeepsThePlayhead() => _session.PlayheadSeconds.ShouldBe(12.5);

    [Fact]
    public void KeepsWhenItArrived() =>
        _session.LastHeartbeatAt.ShouldBe(Harness.Epoch.AddSeconds(42));

    [Fact]
    public void DoesNotStartTheSession() => _session.State.ShouldBe(WatchState.Queued);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.001)]
    public void RejectsAPlayheadThatIsNotATime(double position) =>
        Should.Throw<ArgumentOutOfRangeException>(() => _session.RecordHeartbeat(position));

    [Fact]
    public void AcceptsAPlayheadAtZero() => Should.NotThrow(() => _session.RecordHeartbeat(0.0));

    [Fact]
    public void KeepsTheOldPlayheadWhenANewOneIsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => _session.RecordHeartbeat(double.NaN));

        _session.PlayheadSeconds.ShouldBe(12.5);
    }
}

public sealed class WhenTheVideoIsLive : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _snapshot;

    public WhenTheVideoIsLive()
    {
        _harness.Source.Failure = FakeAudioSource.Failing(
            WebAudioFailure.Unsupported,
            "This live event will begin in a few moments."
        );
        _session = Harness.Run(_harness.Session());
        _snapshot = _session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsUnsupported() => _snapshot.State.ShouldBe(WatchState.Unsupported);

    [Fact]
    public void CarriesYtDlpsReason() =>
        _snapshot.Reason.ShouldBe("This live event will begin in a few moments.");

    [Fact]
    public void CarriesTheKind() => _snapshot.FailureKind.ShouldBe(WebAudioFailure.Unsupported);

    [Fact]
    public void NeverPrepares() => _harness.Preparer.Calls.ShouldBe(0);

    [Fact]
    public void NeverOpensTheEngine() => _harness.Engine.Opened.ShouldBeEmpty();

    [Fact]
    public void DeletesItsScratchDirectory() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void SavesNothing() => _harness.Store.Saves.ShouldBeEmpty();

    [Fact]
    public void HasNoAnalysis() => _snapshot.Analysis.ShouldBeNull();

    [Fact]
    public void RecordsWhenItEnded() => _session.EndedAt.ShouldBe(Harness.Epoch);
}

public sealed class WhenTheFetchFails : IDisposable
{
    private readonly Harness _harness = new();

    public void Dispose() => _harness.Dispose();

    public static TheoryData<WebAudioFailure> FailedKinds =>
        [.. Enum.GetValues<WebAudioFailure>().Where(kind => kind != WebAudioFailure.Unsupported)];

    private WatchSnapshot FailWith(WebAudioFailure kind, string reason)
    {
        _harness.Source.Failure = FakeAudioSource.Failing(kind, reason);
        return Harness.Run(_harness.Session()).Snapshot();
    }

    [Theory]
    [MemberData(nameof(FailedKinds))]
    public void IsFailed(WebAudioFailure kind) =>
        FailWith(kind, "Video unavailable").State.ShouldBe(WatchState.Failed);

    [Theory]
    [MemberData(nameof(FailedKinds))]
    public void CarriesTheReason(WebAudioFailure kind) =>
        FailWith(kind, $"Because {kind}").Reason.ShouldBe($"Because {kind}");

    [Theory]
    [MemberData(nameof(FailedKinds))]
    public void CarriesTheKind(WebAudioFailure kind) =>
        FailWith(kind, "Nope").FailureKind.ShouldBe(kind);

    [Theory]
    [MemberData(nameof(FailedKinds))]
    public void DeletesItsScratchDirectory(WebAudioFailure kind)
    {
        FailWith(kind, "Nope");

        _harness.ScratchIsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void FallsBackToTheMessageWhenThereIsNoReason()
    {
        _harness.Source.Failure = new WebAudioException(
            Harness.Video,
            WebAudioFailure.Failed,
            reason: "",
            message: "yt-dlp exited with code 2"
        );

        Harness.Run(_harness.Session()).Snapshot().Reason.ShouldBe("yt-dlp exited with code 2");
    }

    [Fact]
    public void TruncatesAVeryLongReason() =>
        FailWith(WebAudioFailure.Failed, new string('x', 5000)).Reason!.Length.ShouldBe(500);
}

public sealed class WhenTheEngineFailsMidway : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenTheEngineFailsMidway()
    {
        _harness.Engine.FailOnWindow = 2;
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsFailed() => _snapshot.State.ShouldBe(WatchState.Failed);

    [Fact]
    public void CarriesTheEnginesMessage() => _snapshot.Reason.ShouldBe("The GPU fell over.");

    [Fact]
    public void IsNotAFetchFailure() => _snapshot.FailureKind.ShouldBeNull();

    [Fact]
    public void StopsAtTheFailedWindow() => _harness.Engine.Requested.ShouldBe([0, 1, 2]);

    [Fact]
    public void KeepsWhatWasHeardBeforeIt() => _snapshot.WindowsDone.ShouldBe(2);

    [Fact]
    public void DisposesTheAudio() => _harness.EveryAudioIsDisposed.ShouldBeTrue();

    [Fact]
    public void DeletesThePreparedWav() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();

    [Fact]
    public void DeletesItsScratchDirectory() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void SavesNothing() => _harness.Store.Saves.ShouldBeEmpty();
}

public sealed class WhenPreparingTheWavFails : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenPreparingTheWavFails()
    {
        _harness.Preparer.Failure = new InvalidOperationException("FFmpeg exited with code 1");
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsFailed() => _snapshot.State.ShouldBe(WatchState.Failed);

    [Fact]
    public void CarriesTheMessage() => _snapshot.Reason.ShouldBe("FFmpeg exited with code 1");

    [Fact]
    public void NeverOpensTheEngine() => _harness.Engine.Opened.ShouldBeEmpty();

    [Fact]
    public void DeletesItsScratchDirectory() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void KeepsTheTitleItFetched() => _snapshot.Title.ShouldBe("Me at the zoo");
}

public sealed class WhenTheEngineCannotOpenTheWav : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenTheEngineCannotOpenTheWav()
    {
        _harness.Engine.OpenFailure = new NotSupportedException("Not 16-bit PCM.");
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsFailed() => _snapshot.State.ShouldBe(WatchState.Failed);

    [Fact]
    public void CarriesTheMessage() => _snapshot.Reason.ShouldBe("Not 16-bit PCM.");

    [Fact]
    public void DeletesThePreparedWav() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();

    [Fact]
    public void DeletesItsScratchDirectory() => _harness.ScratchIsEmpty.ShouldBeTrue();
}

public sealed class WhenTheBadWordsListCannotBeReadAtTheStart : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenTheBadWordsListCannotBeReadAtTheStart()
    {
        _harness.BadWords.Failure = new InvalidOperationException("No Bad Words List.");
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsFailed() => _snapshot.State.ShouldBe(WatchState.Failed);

    [Fact]
    public void SaysWhy() => _snapshot.Reason.ShouldBe("No Bad Words List.");

    [Fact]
    public void NeverLooksInTheCache() => _harness.Store.Finds.ShouldBe(0);

    [Fact]
    public void NeverFetches() => _harness.Source.Calls.ShouldBe(0);
}

public sealed class WhenCancelledMidWindow : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _snapshot;

    public WhenCancelledMidWindow()
    {
        var second = _harness.Engine.Hold(1);
        _session = _harness.Session();
        _session.Start();

        second.WaitUntilEntered();
        _session.Cancel();
        Waits.On(_session.Completion);
        _snapshot = _session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsCancelled() => _snapshot.State.ShouldBe(WatchState.Cancelled);

    [Fact]
    public void SaysSo() => _snapshot.Reason.ShouldBe("Cancelled.");

    [Fact]
    public void AbandonsTheWindowInFlight() => _harness.Engine.Abandoned.ShouldBe([1]);

    [Fact]
    public void StartsNoFurtherWindow() => _harness.Engine.Requested.ShouldBe([0, 1]);

    [Fact]
    public void KeepsTheWindowItHadFinished() => _snapshot.WindowsDone.ShouldBe(1);

    [Fact]
    public void DisposesTheAudio() => _harness.EveryAudioIsDisposed.ShouldBeTrue();

    [Fact]
    public void DeletesThePreparedWav() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();

    [Fact]
    public void DeletesItsScratchDirectory() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void SavesNothing() => _harness.Store.Saves.ShouldBeEmpty();

    [Fact]
    public void IsNotAFailure() => _snapshot.FailureKind.ShouldBeNull();

    [Fact]
    public void CanBeCancelledAgain() => Should.NotThrow(_session.Cancel);
}

public sealed class WhenCancelledWhileFetching : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenCancelledWhileFetching()
    {
        var fetch = _harness.Source.Hold = new Gate();
        var session = _harness.Session();
        session.Start();

        fetch.WaitUntilEntered();
        session.Cancel();
        Waits.On(session.Completion);
        _snapshot = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsCancelled() => _snapshot.State.ShouldBe(WatchState.Cancelled);

    [Fact]
    public void NeverPrepares() => _harness.Preparer.Calls.ShouldBe(0);

    [Fact]
    public void DeletesWhatWasDownloaded() => _harness.ScratchIsEmpty.ShouldBeTrue();
}

public sealed class WhenCancelledBeforeStarting : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;

    public WhenCancelledBeforeStarting()
    {
        _session = _harness.Session();
        _session.Cancel();
        _session.Start();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsCancelled() => _session.State.ShouldBe(WatchState.Cancelled);

    [Fact]
    public void IsAlreadyDone() => _session.Completion.IsCompleted.ShouldBeTrue();

    [Fact]
    public void NeverLooksInTheCache() => _harness.Store.Finds.ShouldBe(0);

    [Fact]
    public void RecordsWhenItEnded() => _session.EndedAt.ShouldBe(Harness.Epoch);
}

public sealed class WhenCancelledAfterCompleting : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;

    public WhenCancelledAfterCompleting()
    {
        _session = Harness.Run(_harness.Session());
        _session.Cancel();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StaysComplete() => _session.State.ShouldBe(WatchState.Complete);

    [Fact]
    public void KeepsItsHits() => _session.Snapshot().Analysis!.Hits.ShouldNotBeEmpty();
}

public sealed class WhenStartedTwice : IDisposable
{
    private readonly Harness _harness = new();

    public WhenStartedTwice()
    {
        var session = _harness.Session();
        session.Start();
        session.Start();
        Waits.On(session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void RunsOnce() => _harness.Source.Calls.ShouldBe(1);
}

public sealed class WhenTheSaveFails : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenTheSaveFails()
    {
        _harness.Store.SaveFailure = new IOException("Disk full.");
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StillCompletes() => _snapshot.State.ShouldBe(WatchState.Complete);

    [Fact]
    public void StillServesTheHits() =>
        _snapshot.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(HundredSecondVideo.BadWords));

    [Fact]
    public void StillCleansUp() => _harness.ScratchIsEmpty.ShouldBeTrue();
}

public sealed class WhenTheBadWordsListIsEditedAfterCompleting : IDisposable
{
    private static readonly BadWordsList OnlyCrap = BadWordsList.FromLines(["crap"]);

    private readonly Harness _harness = new();
    private readonly WatchSnapshot _before;
    private readonly WatchSnapshot _after;

    public WhenTheBadWordsListIsEditedAfterCompleting()
    {
        var session = Harness.Run(_harness.Session());
        _before = session.Snapshot();

        _harness.BadWords.List = OnlyCrap;
        _after = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FindsHitsWithTheNewList() =>
        _after.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(OnlyCrap));

    [Fact]
    public void BumpsTheRevision() => _after.Revision.ShouldBe(_before.Revision + 1);

    [Fact]
    public void DoesNotTranscribeAgain() => _harness.Engine.Requested.Count.ShouldBe(5);

    [Fact]
    public void DoesNotSaveAgain() => _harness.Store.Saves.Count.ShouldBe(1);

    [Fact]
    public void LeavesTheEarlierSnapshotAsItWas() =>
        _before.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(HundredSecondVideo.BadWords));

    [Fact]
    public void StaysComplete() => _after.State.ShouldBe(WatchState.Complete);
}

public sealed class WhenTheBadWordsListIsEditedOnACachedVideo : IDisposable
{
    private static readonly BadWordsList OnlyShit = BadWordsList.FromLines(["shit"]);

    private readonly Harness _harness = new();
    private readonly WatchSnapshot _after;

    public WhenTheBadWordsListIsEditedOnACachedVideo()
    {
        _harness.Store.Cached = Harness.CachedTranscript(Harness.Video.Key);
        var session = Harness.Run(_harness.Session());

        _harness.BadWords.List = OnlyShit;
        _after = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FindsHitsWithTheNewList() =>
        _after.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(OnlyShit));

    [Fact]
    public void BumpsTheRevision() => _after.Revision.ShouldBe(2);
}

public sealed class WhenTheBadWordsListIsRereadUnchanged : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _before;
    private readonly WatchSnapshot _after;

    public WhenTheBadWordsListIsRereadUnchanged()
    {
        var session = Harness.Run(_harness.Session());
        _before = session.Snapshot();

        // A new instance with the same phrases, as a reread of an untouched file gives.
        _harness.BadWords.List = BadWordsList.FromLines(["crap", "shit", "go to hell", "damn"]);
        _after = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void KeepsTheRevision() => _after.Revision.ShouldBe(_before.Revision);

    [Fact]
    public void ServesTheSameAnalysis() => _after.Analysis.ShouldBeSameAs(_before.Analysis);
}

public sealed class WhenTheBadWordsListBreaksAfterStarting : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _before;
    private readonly WatchSnapshot _after;

    public WhenTheBadWordsListBreaksAfterStarting()
    {
        var session = Harness.Run(_harness.Session());
        _before = session.Snapshot();

        _harness.BadWords.Failure = new InvalidOperationException("Mid-save.");
        _after = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void KeepsTheHitsItHad() => _after.Analysis!.Hits.ShouldBe(_before.Analysis!.Hits);

    [Fact]
    public void KeepsTheRevision() => _after.Revision.ShouldBe(_before.Revision);

    [Fact]
    public void StaysComplete() => _after.State.ShouldBe(WatchState.Complete);
}

public sealed class WhenTheBadWordsListIsEditedMidTranscription : IDisposable
{
    private static readonly BadWordsList OnlyCrap = BadWordsList.FromLines(["crap"]);

    private readonly Harness _harness = new();
    private readonly WatchSnapshot _during;
    private readonly WatchSnapshot _complete;

    public WhenTheBadWordsListIsEditedMidTranscription()
    {
        var third = _harness.Engine.Hold(2);
        var session = _harness.Session();
        session.Start();

        third.WaitUntilEntered();
        _harness.BadWords.List = OnlyCrap;
        _during = session.Snapshot();
        third.Release();

        Waits.On(session.Completion);
        _complete = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void BumpsTheRevisionAtOnce() => _during.Revision.ShouldBe(3);

    [Fact]
    public void KeepsTheChangeThroughTheRemainingWindows() => _complete.Revision.ShouldBe(6);

    [Fact]
    public void EndsWithHitsFromTheNewList() =>
        _complete.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(OnlyCrap));

    [Fact]
    public void SavesTheSameTranscriptEitherWay() =>
        _harness.Store.Saves.Single().Transcript.Words.ShouldBe(Harness.Script.Batch().Words);
}

public sealed class WhenWindowsFinishSlowerThanRealTime : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;
    private readonly WatchSnapshot _complete;

    public WhenWindowsFinishSlowerThanRealTime()
    {
        _harness.Engine.WindowTime = _ => TimeSpan.FromSeconds(30);
        var fourth = _harness.Engine.Hold(3);
        var session = _harness.Session();
        session.Start();

        fourth.WaitUntilEntered();
        _snapshot = session.Snapshot();
        fourth.Release();

        Waits.On(session.Completion);
        _complete = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsNotKeepingUp() => _snapshot.IsKeepingUp.ShouldBeFalse();

    [Fact]
    public void ReportsTheSecondsMadeSafePerSecond() =>
        _snapshot.RealtimeFactor!.Value.ShouldBe((25.0 + 22.0 + 22.0) / 90.0, 1e-9);

    [Fact]
    public void StopsWorryingOnceComplete() => _complete.IsKeepingUp.ShouldBeTrue();
}

public sealed class WhenWindowsFinishFasterThanRealTime : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenWindowsFinishFasterThanRealTime()
    {
        _harness.Engine.WindowTime = _ => TimeSpan.FromSeconds(1);
        var fourth = _harness.Engine.Hold(3);
        var session = _harness.Session();
        session.Start();

        fourth.WaitUntilEntered();
        _snapshot = session.Snapshot();
        fourth.Release();
        Waits.On(session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsKeepingUp() => _snapshot.IsKeepingUp.ShouldBeTrue();

    [Fact]
    public void ReportsTheFactor() => _snapshot.RealtimeFactor!.Value.ShouldBe(69.0 / 3.0, 1e-9);
}

public sealed class WhenOnlyAnEarlyWindowWasSlow : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _afterSlowOne;
    private readonly WatchSnapshot _threeLater;

    public WhenOnlyAnEarlyWindowWasSlow()
    {
        // The first window waited a long time (a model load, a Job's window).
        _harness.Engine.WindowTime = index =>
            index == 0 ? TimeSpan.FromSeconds(100) : TimeSpan.FromSeconds(1);
        var second = _harness.Engine.Hold(1);
        var last = _harness.Engine.Hold(4);
        var session = _harness.Session();
        session.Start();

        second.WaitUntilEntered();
        _afterSlowOne = session.Snapshot();
        second.Release();

        last.WaitUntilEntered();
        _threeLater = session.Snapshot();
        last.Release();
        Waits.On(session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsNotKeepingUpRightAfterIt() => _afterSlowOne.IsKeepingUp.ShouldBeFalse();

    [Fact]
    public void ForgetsItAfterThreeMoreWindows() => _threeLater.IsKeepingUp.ShouldBeTrue();

    [Fact]
    public void MeasuresOnlyTheLastThreeWindows() =>
        _threeLater.RealtimeFactor!.Value.ShouldBe((22.0 + 22.0 + 14.0) / 3.0, 1e-9);
}

public sealed class WhenAWindowTakesNoMeasurableTime : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenAWindowTakesNoMeasurableTime()
    {
        _harness.Engine.WindowTime = _ => TimeSpan.Zero;
        var second = _harness.Engine.Hold(1);
        var session = _harness.Session();
        session.Start();

        second.WaitUntilEntered();
        _snapshot = session.Snapshot();
        second.Release();
        Waits.On(session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void KeepsTheFactorFinite() =>
        double.IsFinite(_snapshot.RealtimeFactor!.Value).ShouldBeTrue();

    [Fact]
    public void IsKeepingUp() => _snapshot.IsKeepingUp.ShouldBeTrue();
}

public sealed class WhenASnapshotIsHeldWhileTheSessionMovesOn : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _held;
    private readonly HitSnapshot _heldAnalysis;
    private readonly IReadOnlyList<Hit> _heldHits;

    public WhenASnapshotIsHeldWhileTheSessionMovesOn()
    {
        var second = _harness.Engine.Hold(1);
        var session = _harness.Session();
        session.Start();

        second.WaitUntilEntered();
        _held = session.Snapshot();
        _heldAnalysis = _held.Analysis!;
        _heldHits = [.. _heldAnalysis.Hits];

        second.Release();
        Waits.On(session.Completion);
        _harness.BadWords.List = BadWordsList.FromLines(["crap"]);
        session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void KeepsItsState() => _held.State.ShouldBe(WatchState.Transcribing);

    [Fact]
    public void KeepsItsWindowCount() => _held.WindowsDone.ShouldBe(1);

    [Fact]
    public void KeepsItsAnalysis() => _held.Analysis.ShouldBeSameAs(_heldAnalysis);

    [Fact]
    public void KeepsItsRevision() => _held.Revision.ShouldBe(1);

    [Fact]
    public void KeepsItsHits() => _held.Analysis!.Hits.ShouldBe(_heldHits);

    [Fact]
    public void KeepsItsCoverage() =>
        _held.Coverage.Intervals.ShouldBe([new CoverageInterval(0.0, 24.0)]);
}

public sealed class WhenSavedToARealTranscriptStore : IDisposable
{
    private readonly Harness _harness = new();
    private readonly TranscriptStore _store;
    private readonly WatchSnapshot _first;
    private readonly WatchSnapshot _second;

    public WhenSavedToARealTranscriptStore()
    {
        _store = new TranscriptStore(Path.Combine(_harness.Root, "transcripts"));
        _first = Harness.Run(_harness.Session(_store)).Snapshot();
        _second = Harness.Run(_harness.Session(_store)).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task FindsItUnderTheKey() =>
        (
            await _store.FindAsync("youtube-dQw4w9WgXcQ", TestContext.Current.CancellationToken)
        )!.Words.ShouldBe(Harness.Script.Batch().Words);

    [Fact]
    public void NamesTheFileWithTheKeyAndTitle() =>
        Directory
            .GetFiles(_store.DirectoryPath)
            .Select(Path.GetFileName)
            .ShouldBe(["youtube-dQw4w9WgXcQ_Me_at_the_zoo.json"]);

    [Fact]
    public void ServesTheNextSessionFromTheCache() => _second.FromCache.ShouldBeTrue();

    [Fact]
    public void FetchesOnlyForTheFirst() => _harness.Source.Calls.ShouldBe(1);

    [Fact]
    public void GivesTheNextSessionTheSameHits() =>
        _second.Analysis!.Hits.ShouldBe(_first.Analysis!.Hits);
}

public sealed class WhenJudgingAWorkingSessionForExpiry : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchOptions _options;

    public WhenJudgingAWorkingSessionForExpiry()
    {
        _options = _harness.Options();
        _session = _harness.Session();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void IsKeptJustShortOfTheIdleTimeout() =>
        _session
            .IsExpired(Harness.Epoch + _options.IdleTimeout - TimeSpan.FromTicks(1), _options)
            .ShouldBeFalse();

    [Fact]
    public void ExpiresAtTheIdleTimeout() =>
        _session.IsExpired(Harness.Epoch + _options.IdleTimeout, _options).ShouldBeTrue();

    [Fact]
    public void CountsFromTheLatestHeartbeat()
    {
        _harness.Time.Advance(TimeSpan.FromMinutes(1));
        _session.RecordHeartbeat(1.0);

        _session.IsExpired(Harness.Epoch + _options.IdleTimeout, _options).ShouldBeFalse();
    }
}
