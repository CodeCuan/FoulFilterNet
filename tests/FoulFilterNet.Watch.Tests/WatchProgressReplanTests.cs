using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// A 200.4-second video heard first from its two-minute head (W17): windows 0
/// to 3 are fixed by the head, and are heard against a provisional plan of the
/// 130 seconds yt-dlp claimed before the whole WAV says 200.4.
/// </summary>
internal static class HeadStartVideo
{
    public const double Duration = 200.4;
    public const double ClaimedDuration = 130.0;
    public const double HeadSeconds = 120.0;

    public static readonly Script Script = new(
        Duration,
        Utterance.Of(Utterance.W("damn", 3.0, 3.4)),
        Utterance.Of(Utterance.W("oh", 46.5, 46.8), Utterance.W("shit", 47.1, 47.5)),
        Utterance.Of(Utterance.W("crap", 88.0, 88.3)),
        Utterance.Of(Utterance.W("hell", 90.9, 91.2)),
        Utterance.Of(Utterance.W("damn", 150.0, 150.4)),
        Utterance.Of(Utterance.W("crap", 199.0, 199.5))
    );

    public static readonly BadWordsList BadWords = BadWordsList.FromLines([
        "damn",
        "shit",
        "crap",
        "hell",
    ]);

    public static IReadOnlyList<TranscriptionWindow> ProvisionalPlan =>
        TranscriptionWindows.Plan(ClaimedDuration);

    public static int Fixed => TranscriptionWindows.FixedPrefixCount(HeadSeconds);

    /// <summary>The fixed windows heard in <paramref name="order"/> against the provisional plan.</summary>
    public static WatchProgress HeardFromTheHead(params int[] order)
    {
        var progress = WatchProgress.Start(ProvisionalPlan, ClaimedDuration, BadWords);
        foreach (var index in order)
        {
            progress = progress.With(index, Script.Heard(index));
        }

        return progress;
    }
}

public class WhenCheckingTheHeadStartVideo
{
    [Fact]
    public void FixesFourWindows() => HeadStartVideo.Fixed.ShouldBe(4);

    [Fact]
    public void HasTheSameFixedWindowsInBothPlans() =>
        HeadStartVideo.ProvisionalPlan.Take(4).ShouldBe(HeadStartVideo.Script.Plan.Take(4));

    [Fact]
    public void PlansTheWindowAfterThemDifferently() =>
        HeadStartVideo.ProvisionalPlan[4].ShouldNotBe(HeadStartVideo.Script.Plan[4]);

    [Fact]
    public void HasMoreWindowsThanClaimed() =>
        HeadStartVideo.Script.Plan.Count.ShouldBeGreaterThan(HeadStartVideo.ProvisionalPlan.Count);
}

public class WhenTheWholeWavReplansTheHeadsProgress
{
    private readonly WatchProgress _before;
    private readonly WatchProgress _after;

    public WhenTheWholeWavReplansTheHeadsProgress()
    {
        _before = HeadStartVideo.HeardFromTheHead(0, 1, 2, 3);
        _after = _before.WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration);
    }

    [Fact]
    public void TakesTheWholeFilesPlan() => _after.Plan.ShouldBe(HeadStartVideo.Script.Plan);

    [Fact]
    public void TakesTheWholeFilesDuration() =>
        _after.DurationSeconds.ShouldBe(HeadStartVideo.Duration);

    [Fact]
    public void KeepsTheFinishedWindows() => _after.FinishedWindows.ShouldBe([0, 1, 2, 3]);

    [Fact]
    public void MovesOnOneRevision() => _after.Revision.ShouldBe(_before.Revision + 1);

    [Fact]
    public void KeepsTheBadWordsList() => _after.BadWords.ShouldBeSameAs(_before.BadWords);

    [Fact]
    public void KeepsTheSameCoverage() =>
        _after.Coverage.Intervals.ShouldBe(_before.Coverage.Intervals);

    [Fact]
    public void CoversTheFixedSharesLessTheGuard() =>
        _after.Coverage.Intervals.ShouldBe([new CoverageInterval(0.0, 90.0)]);

    [Fact]
    public void KeepsTheSameHits() => _after.Snapshot.Hits.ShouldBe(_before.Snapshot.Hits);

    [Fact]
    public void KeepsTheSameWords() =>
        _after.Snapshot.Transcript.Words.ShouldBe(_before.Snapshot.Transcript.Words);

    [Fact]
    public void KeepsTheSameSegments() =>
        _after.Snapshot.Transcript.Segments.ShouldBe(_before.Snapshot.Transcript.Segments);

    [Fact]
    public void CountsEveryWindowOfTheWholeFile() =>
        _after.Snapshot.TotalWindows.ShouldBe(HeadStartVideo.Script.Plan.Count);

    [Fact]
    public void IsExactlyWhatHearingTheWholeFileWouldHaveGiven()
    {
        var direct = HeadStartVideo.Script.Finish(HeadStartVideo.BadWords, 0, 1, 2, 3);

        _after.Snapshot.Hits.ShouldBe(direct.Snapshot.Hits);
        _after.Snapshot.Transcript.Words.ShouldBe(direct.Snapshot.Transcript.Words);
        _after.Snapshot.Transcript.Segments.ShouldBe(direct.Snapshot.Transcript.Segments);
        _after.Coverage.Intervals.ShouldBe(direct.Coverage.Intervals);
    }

    [Fact]
    public void LeavesTheWindowsPastTheHeadToBeHeard() =>
        Enumerable
            .Range(4, HeadStartVideo.Script.Plan.Count - 4)
            .ShouldAllBe(i => !_after.IsFinished(i));

    [Fact]
    public void FinishesToTheBatchPipelinesHits()
    {
        var progress = _after;
        for (var i = 4; i < HeadStartVideo.Script.Plan.Count; i++)
        {
            progress = progress.With(i, HeadStartVideo.Script.Heard(i));
        }

        progress.Snapshot.Hits.ShouldBe(HeadStartVideo.Script.BatchHits(HeadStartVideo.BadWords));
    }

    [Fact]
    public void FinishesToTheBatchTranscript()
    {
        var progress = _after;
        for (var i = HeadStartVideo.Script.Plan.Count - 1; i >= 4; i--)
        {
            progress = progress.With(i, HeadStartVideo.Script.Heard(i));
        }

        progress.Snapshot.Transcript.Words.ShouldBe(HeadStartVideo.Script.Batch().Words);
        progress.Snapshot.Transcript.Segments.ShouldBe(HeadStartVideo.Script.Batch().Segments);
    }

    [Fact]
    public void LeavesTheHeadsProgressAsItWas() =>
        _before.Plan.ShouldBe(HeadStartVideo.ProvisionalPlan);
}

public class WhenReplanningWithSomeFixedWindowsStillUnheard
{
    private readonly WatchProgress _after;

    public WhenReplanningWithSomeFixedWindowsStillUnheard() =>
        _after = HeadStartVideo
            .HeardFromTheHead(2, 1)
            .WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration);

    [Fact]
    public void KeepsOnlyTheHeardOnes() => _after.FinishedWindows.ShouldBe([1, 2]);

    [Fact]
    public void MatchesHearingThoseFromTheWholeFile() =>
        _after.Snapshot.Hits.ShouldBe(
            HeadStartVideo.Script.Finish(HeadStartVideo.BadWords, 1, 2).Snapshot.Hits
        );
}

public class WhenReplanningBeforeAnyWindowIsHeard
{
    private readonly WatchProgress _before = HeadStartVideo.HeardFromTheHead();
    private readonly WatchProgress _after;

    public WhenReplanningBeforeAnyWindowIsHeard() =>
        _after = _before.WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration);

    [Fact]
    public void HasNothingFinished() => _after.FinishedWindows.ShouldBeEmpty();

    [Fact]
    public void HasEmptyCoverage() => _after.Coverage.IsEmpty.ShouldBeTrue();

    [Fact]
    public void StillMovesOnARevision() => _after.Revision.ShouldBe(1);
}

public class WhenReplanningWithTheSamePlan
{
    private readonly WatchProgress _before = HeadStartVideo.HeardFromTheHead(0, 1);

    [Fact]
    public void ReturnsTheSameInstance() =>
        _before
            .WithPlan(HeadStartVideo.ProvisionalPlan, HeadStartVideo.ClaimedDuration)
            .ShouldBeSameAs(_before);

    [Fact]
    public void ReturnsTheSameInstanceForAnEqualCopy() =>
        _before
            .WithPlan(
                [.. TranscriptionWindows.Plan(HeadStartVideo.ClaimedDuration)],
                HeadStartVideo.ClaimedDuration
            )
            .ShouldBeSameAs(_before);
}

public class WhenReplanningWouldChangeAHeardWindow
{
    private readonly WatchProgress _before = HeadStartVideo.HeardFromTheHead(0, 1, 2, 3, 4);

    [Fact]
    public void RefusesIt() =>
        Should.Throw<ArgumentException>(() =>
            _before.WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration)
        );

    [Fact]
    public void NamesTheWindow() =>
        Should
            .Throw<ArgumentException>(() =>
                _before.WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration)
            )
            .Message.ShouldContain("Window 4");

    [Fact]
    public void NamesThePlan() =>
        Should
            .Throw<ArgumentException>(() =>
                _before.WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration)
            )
            .ParamName.ShouldBe("plan");
}

public class WhenReplanningWouldDropAHeardWindow
{
    [Fact]
    public void RefusesIt() =>
        Should
            .Throw<ArgumentException>(() =>
                HeadStartVideo
                    .HeardFromTheHead(0, 5)
                    .WithPlan(TranscriptionWindows.Plan(100.0), 100.0)
            )
            .ParamName.ShouldBe("plan");
}

public class WhenReplanningWithBadArguments
{
    private readonly WatchProgress _before = HeadStartVideo.HeardFromTheHead(0);

    [Fact]
    public void RefusesNoPlan() =>
        Should.Throw<ArgumentNullException>(() => _before.WithPlan(null!, 10.0));

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RefusesADurationThatIsNotOne(double duration) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            _before.WithPlan(HeadStartVideo.Script.Plan, duration)
        );

    [Fact]
    public void RefusesAPlanThatDoesNotTileTheFile() =>
        Should.Throw<ArgumentException>(() =>
            _before.WithPlan(
                [.. HeadStartVideo.Script.Plan.Take(4), .. HeadStartVideo.Script.Plan.Skip(5)],
                HeadStartVideo.Duration
            )
        );
}

public class WhenTheBadWordsListChangesBetweenHeadAndWhole
{
    private readonly WatchProgress _after;

    public WhenTheBadWordsListChangesBetweenHeadAndWhole() =>
        _after = HeadStartVideo
            .HeardFromTheHead(0, 1, 2, 3)
            .WithBadWords(BadWordsList.FromLines(["crap"]))
            .WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration);

    [Fact]
    public void KeepsTheNewList() => _after.BadWords.Count.ShouldBe(1);

    [Fact]
    public void FindsOnlyItsHits() => _after.Snapshot.Hits.Select(h => h.Phrase).ShouldBe(["crap"]);

    [Fact]
    public void CountsBothChanges() => _after.Revision.ShouldBe(6);
}
