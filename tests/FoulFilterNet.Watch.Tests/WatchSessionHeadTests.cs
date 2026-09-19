using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// A harness over <see cref="HeadStartVideo"/>: yt-dlp claims 200 s, the whole
/// WAV holds 200.4 s, and a head WAV holds the first two minutes of it.
/// </summary>
internal static class HeadHarness
{
    public const double Claimed = 200.0;

    public static Harness Create()
    {
        var harness = new Harness(HeadStartVideo.Script);
        harness.Source.DurationSeconds = Claimed;
        harness.BadWords.List = HeadStartVideo.BadWords;
        harness.Engine.HeadScript = HeadStartVideo.Script.Head(HeadStartVideo.HeadSeconds);
        return harness;
    }

    public static IReadOnlyList<Hit> BatchHits =>
        HeadStartVideo.Script.BatchHits(HeadStartVideo.BadWords);
}

public class WhenCheckingTheHeadHarness
{
    [Fact]
    public void PlansNineWindowsForTheWholeFile() => HeadStartVideo.Script.Plan.Count.ShouldBe(9);

    [Fact]
    public void PlansNineWindowsForTheClaimedDurationToo() =>
        TranscriptionWindows.Plan(HeadHarness.Claimed).Count.ShouldBe(9);

    [Fact]
    public void PlansTheClaimedDurationsLastWindowsDifferently() =>
        TranscriptionWindows
            .Plan(HeadHarness.Claimed)[8]
            .ShouldNotBe(HeadStartVideo.Script.Plan[8]);

    [Fact]
    public void GivesTheHeadSixWindowsOfItsOwn() =>
        HeadStartVideo.Script.Head(120.0).Plan.Count.ShouldBe(6);

    [Fact]
    public void HasHitsBothInsideAndPastTheHead() =>
        HeadHarness
            .BatchHits.Select(h => h.Phrase)
            .ShouldBe(["damn", "shit", "crap", "hell", "damn", "crap"]);
}

public sealed class WhenALongVideoIsHeardFromItsHeadFirst : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _whileConverting;
    private readonly WatchSnapshot _complete;
    private readonly bool _headDeletedBeforeTheWholeFile;

    public WhenALongVideoIsHeardFromItsHeadFirst()
    {
        var whole = _harness.Preparer.Hold = new Gate();
        _session = _harness.Session();
        _session.Start();

        whole.WaitUntilEntered();
        Waits.Until(() => _session.Snapshot().WindowsDone == 4);
        _whileConverting = _session.Snapshot();
        _headDeletedBeforeTheWholeFile = Waits.Until(() =>
            _harness.Preparer.Heads.All(p => !File.Exists(p))
        );
        whole.Release();

        Waits.On(_session.Completion);
        _complete = _session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ConvertsTheHeadOfTheFetchedAudio() =>
        _harness.Preparer.HeadRequests.ShouldBe([
            (_harness.Source.AudioPaths.Single(), WatchOptions.DefaultHeadSeconds),
        ]);

    [Fact]
    public void StillConvertsTheWholeFile() =>
        _harness.Preparer.Requests.ShouldBe([(_harness.Source.AudioPaths.Single(), 0.0)]);

    [Fact]
    public void IsTranscribingWhileTheWholeFileConverts() =>
        _whileConverting.State.ShouldBe(WatchState.Transcribing);

    [Fact]
    public void HearsTheFixedWindowsFromTheHeadInOrder() =>
        _harness.Engine.HeardFromHead.ShouldBe([0, 1, 2, 3]);

    [Fact]
    public void HearsNothingElseFromTheHead() => _harness.Engine.HeardFromHead.Count().ShouldBe(4);

    [Fact]
    public void HearsTheRestFromTheWholeFile() =>
        _harness.Engine.HeardFromWhole.ShouldBe([4, 5, 6, 7, 8]);

    [Fact]
    public void NeverHearsAWindowTwice() => _harness.Engine.Requested.ShouldBeUnique();

    [Fact]
    public void CoversTheFixedSharesBeforeTheWholeFileIsReady() =>
        _whileConverting.Coverage.Intervals.ShouldBe([new CoverageInterval(0.0, 90.0)]);

    [Fact]
    public void ServesTheHeadsHitsBeforeTheWholeFileIsReady() =>
        _whileConverting.Analysis!.Hits.Select(h => h.Phrase).ShouldBe(["damn", "shit", "crap"]);

    [Fact]
    public void ShowsTheClaimedDurationUntilTheWholeFileIsReady() =>
        _whileConverting.DurationSeconds.ShouldBe(HeadHarness.Claimed);

    [Fact]
    public void PlansTheClaimedDurationUntilTheWholeFileIsReady() =>
        _whileConverting.WindowsTotal.ShouldBe(9);

    [Fact]
    public void DeletesTheHeadOnceItsWindowsAreHeard() =>
        _headDeletedBeforeTheWholeFile.ShouldBeTrue();

    [Fact]
    public void OpensTheHeadThenTheWholeFile() =>
        _harness
            .Engine.Opened.Select(o => o.Wav)
            .ShouldBe([_harness.Preparer.Heads.Single(), _harness.Preparer.Prepared.Single()]);

    [Fact]
    public void OpensBothAtHighPriority() =>
        _harness.Engine.Opened.ShouldAllBe(o => o.Priority == InferencePriority.High);

    [Fact]
    public void Completes() => _complete.State.ShouldBe(WatchState.Complete);

    [Fact]
    public void FindsTheBatchPipelinesHits() =>
        _complete.Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);

    [Fact]
    public void SavesExactlyTheBatchTranscript()
    {
        var saved = _harness.Store.Saves.Single().Transcript;
        saved.Words.ShouldBe(HeadStartVideo.Script.Batch().Words);
        saved.Segments.ShouldBe(HeadStartVideo.Script.Batch().Segments);
    }

    [Fact]
    public void EndsWithTheWholeFilesDuration() =>
        _complete.DurationSeconds.ShouldBe(HeadStartVideo.Duration);

    [Fact]
    public void EndsWithTheWholeFilesPlan() =>
        (_complete.WindowsDone, _complete.WindowsTotal).ShouldBe((9, 9));

    [Fact]
    public void CountsTheReplanAsOneRevision() => _complete.Revision.ShouldBe(4 + 1 + 5);

    [Fact]
    public void CoversTheWholeFile() => _complete.Coverage.IsComplete.ShouldBeTrue();

    [Fact]
    public void DeletesTheHeadAndTheWholeWav() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();

    [Fact]
    public void ClosesBothAudios() =>
        (_harness.Engine.Audios.Count, _harness.EveryAudioIsDisposed).ShouldBe((2, true));

    [Fact]
    public void LeavesNoScratchBehind() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void WarmsTheModelOnce() => _harness.Engine.WarmUps.ShouldBe(1);
}

public sealed class WhenTheWholeFileIsReadyMidwayThroughTheHead : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;

    public WhenTheWholeFileIsReadyMidwayThroughTheHead()
    {
        var whole = _harness.Preparer.Hold = new Gate();
        var secondWindow = _harness.Engine.Hold(1);
        _session = _harness.Session();
        _session.Start();

        secondWindow.WaitUntilEntered();
        whole.Release();
        Waits.Until(() => _harness.Preparer.Prepared.Count == 1);
        secondWindow.Release();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StopsHearingTheHeadAfterTheWindowInFlight() =>
        _harness.Engine.HeardFromHead.ShouldBe([0, 1]);

    [Fact]
    public void HearsTheRestOfTheFixedWindowsFromTheWholeFile() =>
        _harness.Engine.HeardFromWhole.ShouldBe([2, 3, 4, 5, 6, 7, 8]);

    [Fact]
    public void FindsTheBatchPipelinesHits() =>
        _session.Snapshot().Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);

    [Fact]
    public void DeletesEverything() =>
        (_harness.EveryPreparedWavIsDeleted, _harness.ScratchIsEmpty).ShouldBe((true, true));
}

public sealed class WhenTheViewerStartsInsideTheHeadButNotAtTheStart : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;

    public WhenTheViewerStartsInsideTheHeadButNotAtTheStart()
    {
        var whole = _harness.Preparer.Hold = new Gate();
        _session = _harness.Session();
        _session.RecordHeartbeat(50.0);
        _session.Start();

        whole.WaitUntilEntered();
        Waits.Until(() => _session.Snapshot().WindowsDone == 4);
        whole.Release();
        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StartsWithTheWindowHoldingThePlayheadThenWrapsBack() =>
        _harness.Engine.HeardFromHead.ShouldBe([2, 3, 1, 0]);

    [Fact]
    public void StillFindsTheBatchPipelinesHits() =>
        _session.Snapshot().Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);
}

public sealed class WhenTheViewerStartsPastTheHead : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _whileConverting;

    public WhenTheViewerStartsPastTheHead()
    {
        var whole = _harness.Preparer.Hold = new Gate();
        _session = _harness.Session();
        _session.RecordHeartbeat(150.0);
        _session.Start();

        whole.WaitUntilEntered();
        Waits.Until(() => _harness.Preparer.HeadCalls == 1 && _harness.Engine.Audios.Count == 1);
        Waits.Until(() => _harness.Engine.Audios.Single().IsDisposed);
        _whileConverting = _session.Snapshot();
        whole.Release();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void HearsNothingFromTheHead() => _harness.Engine.HeardFromHead.ShouldBeEmpty();

    [Fact]
    public void KeepsPreparingUntilTheWholeFileIsReady() =>
        _whileConverting.State.ShouldBe(WatchState.Preparing);

    [Fact]
    public void ServesNoAnalysisUntilThen() => _whileConverting.Analysis.ShouldBeNull();

    [Fact]
    public void StartsTheWholeFileAtThePlayhead() =>
        _harness.Engine.HeardFromWhole.First().ShouldBe(6);

    [Fact]
    public void StillFindsTheBatchPipelinesHits() =>
        _session.Snapshot().Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);

    [Fact]
    public void CountsNoReplan() => _session.Snapshot().Revision.ShouldBe(9);

    [Fact]
    public void DeletesTheHead() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();
}

public sealed class WhenTheViewerSeeksPastTheHeadWhileItIsHeard : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;
    private readonly WatchSnapshot _whileConverting;

    public WhenTheViewerSeeksPastTheHeadWhileItIsHeard()
    {
        var whole = _harness.Preparer.Hold = new Gate();
        var secondWindow = _harness.Engine.Hold(1);
        _session = _harness.Session();
        _session.Start();

        secondWindow.WaitUntilEntered();
        _session.RecordHeartbeat(120.0);
        secondWindow.Release();

        whole.WaitUntilEntered();
        Waits.Until(() => _harness.Engine.Audios.First().IsDisposed);
        _whileConverting = _session.Snapshot();
        whole.Release();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StopsHearingTheHead() => _harness.Engine.HeardFromHead.ShouldBe([0, 1]);

    [Fact]
    public void KeepsWhatItHeard() => _whileConverting.WindowsDone.ShouldBe(2);

    [Fact]
    public void ThenStartsTheWholeFileAtThePlayhead() =>
        _harness.Engine.HeardFromWhole.First().ShouldBe(5);

    [Fact]
    public void StillFindsTheBatchPipelinesHits() =>
        _session.Snapshot().Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);
}

public sealed class WhenYtDlpGivesNoDuration : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSnapshot _whileConverting;
    private readonly WatchSnapshot _complete;

    public WhenYtDlpGivesNoDuration()
    {
        _harness.Source.DurationSeconds = null;
        var whole = _harness.Preparer.Hold = new Gate();
        var session = _harness.Session();
        session.Start();

        whole.WaitUntilEntered();
        Waits.Until(() => session.Snapshot().WindowsDone == 4);
        _whileConverting = session.Snapshot();
        whole.Release();

        Waits.On(session.Completion);
        _complete = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StillConvertsAHead() => _harness.Preparer.HeadCalls.ShouldBe(1);

    [Fact]
    public void PlansTheHeadsLengthProvisionally() =>
        _whileConverting.DurationSeconds.ShouldBe(HeadStartVideo.HeadSeconds);

    [Fact]
    public void HearsTheFixedWindows() => _harness.Engine.HeardFromHead.ShouldBe([0, 1, 2, 3]);

    [Fact]
    public void FindsTheBatchPipelinesHits() =>
        _complete.Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);

    [Fact]
    public void EndsWithTheWholeFilesDuration() =>
        _complete.DurationSeconds.ShouldBe(HeadStartVideo.Duration);
}

public sealed class WhenTheVideoIsNoLongerThanTheHead : IDisposable
{
    private readonly Harness _harness = new();

    public WhenTheVideoIsNoLongerThanTheHead() =>
        Harness.Run(_harness.Session()).State.ShouldBe(WatchState.Complete);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ConvertsNoHead() => _harness.Preparer.HeadCalls.ShouldBe(0);

    [Fact]
    public void OpensOnlyTheWholeFile() => _harness.Engine.Opened.Count.ShouldBe(1);
}

public sealed class WhenAShortVideoGivesNoDuration : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _complete;

    public WhenAShortVideoGivesNoDuration()
    {
        _harness.Source.DurationSeconds = null;
        _harness.Engine.HeadScript = Harness.Script.Head(Harness.Script.Duration);
        var whole = _harness.Preparer.Hold = new Gate();
        var session = _harness.Session();
        session.Start();

        whole.WaitUntilEntered();
        Waits.Until(() => session.Snapshot().WindowsDone == 3);
        whole.Release();

        Waits.On(session.Completion);
        _complete = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void HearsTheWindowsItsWholeLengthFixesFromTheHead() =>
        _harness.Engine.HeardFromHead.ShouldBe([0, 1, 2]);

    [Fact]
    public void HearsTheLastTwoFromTheWholeFile() =>
        _harness.Engine.HeardFromWhole.ShouldBe([3, 4]);

    [Fact]
    public void NeedsNoReplanWhenTheHeadWasTheWholeFile() => _complete.Revision.ShouldBe(5);

    [Fact]
    public void FindsTheBatchPipelinesHits() =>
        _complete.Analysis!.Hits.ShouldBe(Harness.Script.BatchHits(HundredSecondVideo.BadWords));
}

public sealed class WhenTheHeadCannotBeConverted : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;

    public WhenTheHeadCannotBeConverted()
    {
        _harness.Preparer.HeadFailure = new InvalidOperationException("FFmpeg fell over.");
        _session = Harness.Run(_harness.Session());
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void WaitsForTheWholeFileInstead() => _session.State.ShouldBe(WatchState.Complete);

    [Fact]
    public void HearsEverythingFromTheWholeFile() =>
        _harness.Engine.HeardFromWhole.ShouldBe([0, 1, 2, 3, 4, 5, 6, 7, 8]);

    [Fact]
    public void FindsTheBatchPipelinesHits() =>
        _session.Snapshot().Analysis!.Hits.ShouldBe(HeadHarness.BatchHits);
}

public sealed class WhenThePreparerCannotConvertAHead : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;

    public WhenThePreparerCannotConvertAHead()
    {
        _harness.AudioPreparer = new WholeFileOnlyPreparer(_harness.Preparer);
        _session = Harness.Run(_harness.Session());
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ConvertsTheWholeFileFirst() => _harness.Preparer.HeadCalls.ShouldBe(0);

    [Fact]
    public void HearsEverythingFromTheWholeFile() =>
        _harness.Engine.HeardFromWhole.ShouldBe([0, 1, 2, 3, 4, 5, 6, 7, 8]);

    [Fact]
    public void Completes() => _session.State.ShouldBe(WatchState.Complete);
}

public sealed class WhenTheHeadIsTurnedOff : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();

    public WhenTheHeadIsTurnedOff()
    {
        _harness.HeadSeconds = 0.0;
        Harness.Run(_harness.Session()).State.ShouldBe(WatchState.Complete);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void ConvertsNoHead() => _harness.Preparer.HeadCalls.ShouldBe(0);
}

public sealed class WhenTheWholeFileIsReadyBeforeTheHead : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;

    public WhenTheWholeFileIsReadyBeforeTheHead()
    {
        var head = _harness.Preparer.HeadHold = new Gate();
        _session = _harness.Session();
        _session.Start();

        head.WaitUntilEntered();
        Waits.Until(() => _harness.Preparer.Prepared.Count == 1);
        head.Release();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void NeverOpensTheHead() => _harness.Engine.Opened.Count.ShouldBe(1);

    [Fact]
    public void HearsEverythingFromTheWholeFile() =>
        _harness.Engine.HeardFromWhole.ShouldBe([0, 1, 2, 3, 4, 5, 6, 7, 8]);

    [Fact]
    public void StillDeletesTheHead() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();
}

public sealed class WhenCancelledWhileHearingTheHead : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSession _session;

    public WhenCancelledWhileHearingTheHead()
    {
        var whole = _harness.Preparer.Hold = new Gate();
        var thirdWindow = _harness.Engine.Hold(2);
        _session = _harness.Session();
        _session.Start();

        whole.WaitUntilEntered();
        thirdWindow.WaitUntilEntered();
        _session.Cancel();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void EndsCancelled() => _session.State.ShouldBe(WatchState.Cancelled);

    [Fact]
    public void AbandonsTheWindowInFlight() => _harness.Engine.Abandoned.ShouldBe([2]);

    [Fact]
    public void StopsTheWholeConversion() =>
        _harness.Preparer.WholeToken.IsCancellationRequested.ShouldBeTrue();

    [Fact]
    public void NeverOpensTheWholeFile() => _harness.Engine.Opened.Count.ShouldBe(1);

    [Fact]
    public void ClosesTheHead() => _harness.EveryAudioIsDisposed.ShouldBeTrue();

    [Fact]
    public void DeletesTheHead() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();

    [Fact]
    public void LeavesNoScratchBehind() => _harness.ScratchIsEmpty.ShouldBeTrue();
}

public sealed class WhenTheWholeConversionFailsWhileTheHeadIsHeard : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSnapshot _snapshot;

    public WhenTheWholeConversionFailsWhileTheHeadIsHeard()
    {
        var whole = _harness.Preparer.Hold = new Gate();
        _harness.Preparer.Failure = new InvalidOperationException("Disk full.");
        var session = _harness.Session();
        session.Start();

        whole.WaitUntilEntered();
        Waits.Until(() => session.Snapshot().WindowsDone == 4);
        whole.Release();

        Waits.On(session.Completion);
        _snapshot = session.Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Fails() => _snapshot.State.ShouldBe(WatchState.Failed);

    [Fact]
    public void GivesTheConversionsReason() => _snapshot.Reason.ShouldBe("Disk full.");

    [Fact]
    public void KeepsWhatTheHeadHeard() => _snapshot.WindowsDone.ShouldBe(4);

    [Fact]
    public void DeletesTheHead() => _harness.EveryPreparedWavIsDeleted.ShouldBeTrue();

    [Fact]
    public void LeavesNoScratchBehind() => _harness.ScratchIsEmpty.ShouldBeTrue();

    [Fact]
    public void SavesNothing() => _harness.Store.Saves.ShouldBeEmpty();
}

public sealed class WhenTheEngineFailsOnTheHead : IDisposable
{
    private readonly Harness _harness = HeadHarness.Create();
    private readonly WatchSnapshot _snapshot;

    public WhenTheEngineFailsOnTheHead()
    {
        _harness.Engine.FailOnWindow = 1;
        _harness.Preparer.Hold = new Gate();
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FailsOnTheHeadsWindow() => _harness.Engine.HeardFromHead.ShouldBe([0, 1]);

    [Fact]
    public void GivesTheEnginesReason() => _snapshot.Reason.ShouldBe("The GPU fell over.");

    [Fact]
    public void Fails() => _snapshot.State.ShouldBe(WatchState.Failed);

    [Fact]
    public void StopsTheWholeConversion() =>
        _harness.Preparer.WholeToken.IsCancellationRequested.ShouldBeTrue();

    [Fact]
    public void DeletesEverything() =>
        (
            _harness.EveryPreparedWavIsDeleted,
            _harness.ScratchIsEmpty,
            _harness.EveryAudioIsDisposed
        ).ShouldBe((true, true, true));
}
