using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// The 100-second file most of these tests use. <see cref="TranscriptionWindows.Plan"/>
/// starts its windows at 0, 22, 44, 66 and 72 s, so the shares are
/// <c>[0, 25)</c>, <c>[25, 47)</c>, <c>[47, 69)</c>, <c>[69, 83)</c> and
/// <c>[83, 100]</c> once clamped to the file.
/// </summary>
internal static class HundredSeconds
{
    public const double Duration = 100.0;

    public static readonly IReadOnlyList<TranscriptionWindow> Plan = TranscriptionWindows.Plan(
        Duration
    );

    public static Coverage With(params int[] finished) => Coverage.ForDuration(Duration, finished);

    public static Coverage With(double guard, params int[] finished) =>
        Coverage.ForDuration(Duration, finished, guard);

    public static CoverageInterval Interval(double from, double to) => new(from, to);
}

public class WhenCheckingTheHundredSecondPlan
{
    [Fact]
    public void HasFiveWindows() => HundredSeconds.Plan.Count.ShouldBe(5);

    [Fact]
    public void HasTheSharesTheOtherTestsAssume() =>
        HundredSeconds
            .Plan.Select(w => w.KeepTo)
            .ShouldBe([25.0, 47.0, 69.0, 83.0, double.PositiveInfinity]);
}

public class WhenReadingTheDefaultGuard
{
    [Fact]
    public void IsOneSecond() => Coverage.DefaultGuardSeconds.ShouldBe(1.0);
}

public class WhenASingleWindowFileIsFinished
{
    private readonly Coverage _coverage = Coverage.ForDuration(8.0, [0]);

    [Fact]
    public void CoversTheWholeFile() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 8.0)]);

    [Fact]
    public void IsComplete() => _coverage.IsComplete.ShouldBeTrue();

    [Fact]
    public void IsNotEmpty() => _coverage.IsEmpty.ShouldBeFalse();

    [Fact]
    public void KnowsTheDuration() => _coverage.DurationSeconds.ShouldBe(8.0);

    [Fact]
    public void ContainsTheWholeFile() => _coverage.Contains(0.0, 8.0).ShouldBeTrue();

    [Fact]
    public void CoversEverythingAheadOfTheStart() => _coverage.CoveredAheadOf(0.0).ShouldBe(8.0);
}

public class WhenASingleWindowFileIsUnfinished
{
    private readonly Coverage _coverage = Coverage.ForDuration(8.0, []);

    [Fact]
    public void HasNoIntervals() => _coverage.Intervals.ShouldBeEmpty();

    [Fact]
    public void IsEmpty() => _coverage.IsEmpty.ShouldBeTrue();

    [Fact]
    public void IsNotComplete() => _coverage.IsComplete.ShouldBeFalse();

    [Fact]
    public void ContainsNoPoint() => _coverage.Contains(4.0, 4.0).ShouldBeFalse();

    [Fact]
    public void CoversNothingAheadOfTheStart() => _coverage.CoveredAheadOf(0.0).ShouldBe(0.0);
}

public class WhenEveryWindowIsFinished
{
    private readonly Coverage _coverage = HundredSeconds.With(0, 1, 2, 3, 4);

    [Fact]
    public void MergesEveryShareIntoOneInterval() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 100.0)]);

    [Fact]
    public void IsComplete() => _coverage.IsComplete.ShouldBeTrue();

    [Fact]
    public void IsNotEmpty() => _coverage.IsEmpty.ShouldBeFalse();

    [Fact]
    public void ContainsTheWholeFile() => _coverage.Contains(0.0, 100.0).ShouldBeTrue();

    [Fact]
    public void ContainsAStretchAcrossEveryShareBoundary() =>
        _coverage.Contains(24.0, 84.0).ShouldBeTrue();

    [Fact]
    public void CoversTheWholeFileAheadOfTheStart() =>
        _coverage.CoveredAheadOf(0.0).ShouldBe(100.0);
}

public class WhenEveryWindowOfALongFileIsFinished
{
    private readonly Coverage _coverage;

    public WhenEveryWindowOfALongFileIsFinished()
    {
        var plan = TranscriptionWindows.Plan(3140.5);
        _coverage = Coverage.From(plan, 3140.5, Enumerable.Range(0, plan.Count));
    }

    [Fact]
    public void MergesEveryShareIntoOneInterval() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 3140.5)]);

    [Fact]
    public void IsComplete() => _coverage.IsComplete.ShouldBeTrue();
}

public class WhenAllButOneWindowOfALongFileIsFinished
{
    private readonly IReadOnlyList<TranscriptionWindow> _plan = TranscriptionWindows.Plan(3140.5);
    private readonly Coverage _coverage;

    public WhenAllButOneWindowOfALongFileIsFinished()
    {
        _coverage = Coverage.From(
            _plan,
            3140.5,
            Enumerable.Range(0, _plan.Count).Where(i => i != 70)
        );
        _coverage.Intervals.Count.ShouldBe(2);
    }

    [Fact]
    public void EndsTheFirstRunAGuardBeforeTheMissingShare() =>
        _coverage.Intervals[0].ShouldBe(HundredSeconds.Interval(0.0, _plan[70].KeepFrom - 1.0));

    [Fact]
    public void StartsTheSecondRunAGuardAfterTheMissingShare() =>
        _coverage.Intervals[1].ShouldBe(HundredSeconds.Interval(_plan[70].KeepTo + 1.0, 3140.5));

    [Fact]
    public void IsNotComplete() => _coverage.IsComplete.ShouldBeFalse();
}

public class WhenOnlyTheFirstWindowIsFinished
{
    private readonly Coverage _coverage = HundredSeconds.With(0);

    [Fact]
    public void StartsAtZeroUnguarded() => _coverage.Intervals[0].From.ShouldBe(0.0);

    [Fact]
    public void EndsAGuardBeforeTheUnfinishedNeighbour() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 24.0)]);

    [Fact]
    public void IsNotComplete() => _coverage.IsComplete.ShouldBeFalse();

    [Fact]
    public void IsNotEmpty() => _coverage.IsEmpty.ShouldBeFalse();
}

public class WhenOnlyTheLastWindowIsFinished
{
    private readonly Coverage _coverage = HundredSeconds.With(4);

    [Fact]
    public void StartsAGuardAfterTheUnfinishedNeighbourAndEndsWithTheFile() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(84.0, 100.0)]);

    [Fact]
    public void ClampsTheEndlessShareToTheDuration() =>
        _coverage.Intervals[0].To.ShouldBe(HundredSeconds.Duration);

    [Fact]
    public void IsNotComplete() => _coverage.IsComplete.ShouldBeFalse();
}

public class WhenOnlyAMiddleWindowIsFinished
{
    private readonly Coverage _coverage = HundredSeconds.With(2);

    [Fact]
    public void IsGuardedOnBothSides() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(48.0, 68.0)]);
}

public class WhenAdjacentMiddleWindowsAreFinished
{
    private readonly Coverage _coverage = HundredSeconds.With(1, 2, 3);

    [Fact]
    public void MergesThemWithGuardsOnlyAtTheOuterEdges() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(26.0, 82.0)]);
}

public class WhenWindowsFinishOutOfOrder
{
    private readonly Coverage _coverage = HundredSeconds.With(3, 1, 2);

    [Fact]
    public void MergesThemIntoOneInterval() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(26.0, 82.0)]);

    [Fact]
    public void MatchesFinishingThemInOrder() =>
        _coverage.Intervals.ShouldBe(HundredSeconds.With(1, 2, 3).Intervals);
}

public class WhenAllWindowsFinishInReverseOrder
{
    private readonly Coverage _coverage = HundredSeconds.With(4, 3, 2, 1, 0);

    [Fact]
    public void CoversTheWholeFile() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 100.0)]);

    [Fact]
    public void IsComplete() => _coverage.IsComplete.ShouldBeTrue();
}

public class WhenFinishedWindowsAlternate
{
    private readonly Coverage _coverage = HundredSeconds.With(0, 2, 4);

    [Fact]
    public void MakesOneGuardedIntervalPerRun() =>
        _coverage.Intervals.ShouldBe([
            HundredSeconds.Interval(0.0, 24.0),
            HundredSeconds.Interval(48.0, 68.0),
            HundredSeconds.Interval(84.0, 100.0),
        ]);

    [Fact]
    public void IsNotComplete() => _coverage.IsComplete.ShouldBeFalse();
}

public class WhenOneMiddleWindowIsMissing
{
    private readonly Coverage _coverage = HundredSeconds.With(0, 1, 3, 4);

    [Fact]
    public void LeavesAGuardedGapAroundIt() =>
        _coverage.Intervals.ShouldBe([
            HundredSeconds.Interval(0.0, 46.0),
            HundredSeconds.Interval(70.0, 100.0),
        ]);
}

public class WhenOnlyTheEndsAreMissing
{
    private readonly Coverage _coverage = HundredSeconds.With(1, 2, 3);

    [Fact]
    public void GuardsBothEndsOfTheRun() => _coverage.Intervals[0].From.ShouldBe(26.0);

    [Fact]
    public void DoesNotCoverTheStart() => _coverage.CoveredAheadOf(0.0).ShouldBe(0.0);
}

public class WhenTheGuardExceedsAMiddleRun
{
    // Window 2's share is 22 s long; two 12 s guards need 24.
    private readonly Coverage _coverage = HundredSeconds.With(12.0, 2);

    [Fact]
    public void DropsTheRun() => _coverage.Intervals.ShouldBeEmpty();

    [Fact]
    public void IsEmpty() => _coverage.IsEmpty.ShouldBeTrue();
}

public class WhenTheGuardsExactlyConsumeAMiddleRun
{
    // Window 2's share is 22 s long; two 11 s guards leave a single point.
    private readonly Coverage _coverage = HundredSeconds.With(11.0, 2);

    [Fact]
    public void DropsTheZeroLengthRun() => _coverage.Intervals.ShouldBeEmpty();
}

public class WhenTheGuardExceedsOneRunButNotAnother
{
    private readonly Coverage _coverage = HundredSeconds.With(12.0, 0, 2);

    [Fact]
    public void KeepsOnlyTheRunThatSurvivesItsGuards() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 13.0)]);
}

public class WhenTheGuardExceedsAnEdgeRun
{
    // Window 0's share is 25 s and needs only one guard.
    private readonly Coverage _coverage = HundredSeconds.With(30.0, 0);

    [Fact]
    public void DropsTheRun() => _coverage.Intervals.ShouldBeEmpty();
}

public class WhenAGuardWouldExceedACompleteFile
{
    private readonly Coverage _coverage = HundredSeconds.With(500.0, 0, 1, 2, 3, 4);

    [Fact]
    public void AppliesNoGuardBecauseNoEdgeIsUnfinished() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 100.0)]);
}

public class WhenUsingACustomGuard
{
    private readonly Coverage _coverage = HundredSeconds.With(2.5, 0, 2, 4);

    [Fact]
    public void TrimsTheFirstRunByIt() =>
        _coverage.Intervals[0].ShouldBe(HundredSeconds.Interval(0.0, 22.5));

    [Fact]
    public void TrimsAMiddleRunByItOnBothSides() =>
        _coverage.Intervals[1].ShouldBe(HundredSeconds.Interval(49.5, 66.5));

    [Fact]
    public void TrimsTheLastRunByIt() =>
        _coverage.Intervals[2].ShouldBe(HundredSeconds.Interval(85.5, 100.0));
}

public class WhenTheGuardIsZero
{
    private readonly Coverage _coverage = HundredSeconds.With(0.0, 2);

    [Fact]
    public void KeepsTheShareExactly() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(47.0, 69.0)]);
}

public class WhenBuildingFromAnExplicitPlan
{
    private readonly Coverage _coverage = Coverage.From(HundredSeconds.Plan, 100.0, [2]);

    [Fact]
    public void MatchesBuildingFromTheDuration() =>
        _coverage.Intervals.ShouldBe(HundredSeconds.With(2).Intervals);

    [Fact]
    public void UsesTheDefaultGuard() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(48.0, 68.0)]);
}

/// <summary>
/// A plan whose later shares lie past the end of the file: they clamp to
/// nothing, so the run that reaches the end needs no guard there.
/// </summary>
public class WhenSharesRunPastTheDuration
{
    private readonly Coverage _coverage = Coverage.From(HundredSeconds.Plan, 50.0, [0, 1, 2]);

    [Fact]
    public void ClampsTheRunToTheDurationWithoutAGuard() =>
        _coverage.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 50.0)]);

    [Fact]
    public void IsStillNotComplete() => _coverage.IsComplete.ShouldBeFalse();
}

public class WhenFinishedIndicesRepeat
{
    private readonly Coverage _coverage = HundredSeconds.With(1, 1, 2, 2, 1);

    [Fact]
    public void MatchesTheDistinctSet() =>
        _coverage.Intervals.ShouldBe(HundredSeconds.With(1, 2).Intervals);

    [Fact]
    public void DoesNotCountAsComplete() =>
        HundredSeconds.With(0, 0, 0, 0, 0).IsComplete.ShouldBeFalse();
}

public class WhenTheFileHasZeroDuration
{
    private readonly Coverage _finished = Coverage.ForDuration(0.0, [0]);
    private readonly Coverage _unfinished = Coverage.ForDuration(0.0, []);

    [Fact]
    public void IsCompleteOnceItsOnlyWindowIsFinished() => _finished.IsComplete.ShouldBeTrue();

    [Fact]
    public void CoversTheSinglePointWhenFinished() =>
        _finished.Intervals.ShouldBe([HundredSeconds.Interval(0.0, 0.0)]);

    [Fact]
    public void ContainsTheStartWhenFinished() => _finished.Contains(0.0, 0.0).ShouldBeTrue();

    [Fact]
    public void CoversNothingAheadWhenFinished() => _finished.CoveredAheadOf(0.0).ShouldBe(0.0);

    [Fact]
    public void IsEmptyWhenUnfinished() => _unfinished.IsEmpty.ShouldBeTrue();

    [Fact]
    public void IsNotCompleteWhenUnfinished() => _unfinished.IsComplete.ShouldBeFalse();
}

public class WhenUsingTheEmptyCoverage
{
    private readonly Coverage _coverage = Coverage.Empty;

    [Fact]
    public void HasNoIntervals() => _coverage.Intervals.ShouldBeEmpty();

    [Fact]
    public void IsEmpty() => _coverage.IsEmpty.ShouldBeTrue();

    [Fact]
    public void IsNotComplete() => _coverage.IsComplete.ShouldBeFalse();

    [Fact]
    public void ContainsNothing() => _coverage.Contains(0.0, 0.0).ShouldBeFalse();

    [Fact]
    public void CoversNothingAhead() => _coverage.CoveredAheadOf(0.0).ShouldBe(0.0);

    [Fact]
    public void IsASingleInstance() => Coverage.Empty.ShouldBeSameAs(Coverage.Empty);
}

/// <summary>Coverage <c>[0, 24]</c>, <c>[48, 68]</c>, <c>[84, 100]</c>.</summary>
public class WhenAskingWhetherAStretchIsCovered
{
    private readonly Coverage _coverage = HundredSeconds.With(0, 2, 4);

    [Fact]
    public void ContainsAStretchInside() => _coverage.Contains(50.0, 60.0).ShouldBeTrue();

    [Fact]
    public void ContainsAPointInside() => _coverage.Contains(55.0, 55.0).ShouldBeTrue();

    [Fact]
    public void ContainsAWholeInterval() => _coverage.Contains(48.0, 68.0).ShouldBeTrue();

    [Fact]
    public void ContainsTheStartOfAnInterval() => _coverage.Contains(48.0, 48.0).ShouldBeTrue();

    [Fact]
    public void ContainsTheEndOfAnInterval() => _coverage.Contains(68.0, 68.0).ShouldBeTrue();

    [Fact]
    public void ContainsTheStartOfTheFile() => _coverage.Contains(0.0, 0.0).ShouldBeTrue();

    [Fact]
    public void ContainsTheEndOfTheFile() => _coverage.Contains(100.0, 100.0).ShouldBeTrue();

    [Fact]
    public void DoesNotContainAStretchStartingJustBeforeAnInterval() =>
        _coverage.Contains(47.999, 60.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainAStretchEndingJustAfterAnInterval() =>
        _coverage.Contains(50.0, 68.001).ShouldBeFalse();

    [Fact]
    public void DoesNotContainAStretchSpanningAGap() =>
        _coverage.Contains(20.0, 50.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainAStretchSpanningEveryInterval() =>
        _coverage.Contains(0.0, 100.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainAPointInAGap() => _coverage.Contains(30.0, 30.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainTheUnguardedShareBoundary() =>
        _coverage.Contains(25.0, 25.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainAPointInTheGuard() => _coverage.Contains(24.5, 24.5).ShouldBeFalse();

    [Fact]
    public void DoesNotContainAStretchPastTheEnd() =>
        _coverage.Contains(90.0, 101.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainAPointPastTheEnd() =>
        _coverage.Contains(101.0, 101.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainANegativeStart() => _coverage.Contains(-1.0, 10.0).ShouldBeFalse();

    [Fact]
    public void DoesNotContainANegativePoint() => _coverage.Contains(-1.0, -1.0).ShouldBeFalse();
}

/// <summary>Coverage <c>[0, 24]</c>, <c>[48, 68]</c>, <c>[84, 100]</c>.</summary>
public class WhenAskingHowMuchIsCoveredAhead
{
    private readonly Coverage _coverage = HundredSeconds.With(0, 2, 4);

    [Fact]
    public void CountsFromTheStartOfTheFile() => _coverage.CoveredAheadOf(0.0).ShouldBe(24.0);

    [Fact]
    public void CountsFromInsideAnInterval() => _coverage.CoveredAheadOf(50.0).ShouldBe(18.0);

    [Fact]
    public void CountsFromTheStartOfAnInterval() => _coverage.CoveredAheadOf(48.0).ShouldBe(20.0);

    [Fact]
    public void IsZeroAtTheEndOfAnInterval() => _coverage.CoveredAheadOf(68.0).ShouldBe(0.0);

    [Fact]
    public void IsZeroInAGap() => _coverage.CoveredAheadOf(30.0).ShouldBe(0.0);

    [Fact]
    public void IsZeroJustBeforeAnInterval() => _coverage.CoveredAheadOf(47.5).ShouldBe(0.0);

    [Fact]
    public void RunsToTheEndOfTheFileInTheLastInterval() =>
        _coverage.CoveredAheadOf(90.0).ShouldBe(10.0);

    [Fact]
    public void IsZeroAtTheDuration() => _coverage.CoveredAheadOf(100.0).ShouldBe(0.0);

    [Fact]
    public void IsZeroPastTheDuration() => _coverage.CoveredAheadOf(150.0).ShouldBe(0.0);

    [Fact]
    public void IsZeroAtANegativePosition() => _coverage.CoveredAheadOf(-1.0).ShouldBe(0.0);

    [Fact]
    public void DoesNotJumpTheGapToTheNextInterval() =>
        _coverage.CoveredAheadOf(20.0).ShouldBe(4.0);
}

public class WhenACompleteFileIsAskedHowMuchIsCoveredAhead
{
    private readonly Coverage _coverage = HundredSeconds.With(0, 1, 2, 3, 4);

    [Fact]
    public void RunsAcrossEveryShareBoundaryFromTheMiddle() =>
        _coverage.CoveredAheadOf(40.0).ShouldBe(60.0);

    [Fact]
    public void RunsAcrossAShareBoundaryFromExactlyOnIt() =>
        _coverage.CoveredAheadOf(25.0).ShouldBe(75.0);

    [Fact]
    public void IsZeroAtTheDuration() => _coverage.CoveredAheadOf(100.0).ShouldBe(0.0);
}

public class WhenGivenAFinishedIndexOutsideThePlan
{
    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Throws(int index) =>
        Should.Throw<ArgumentOutOfRangeException>(() => HundredSeconds.With(0, index));
}

public class WhenGivenInvalidArguments
{
    [Fact]
    public void RejectsANullPlan() =>
        Should.Throw<ArgumentNullException>(() => Coverage.From(null!, 100.0, [0]));

    [Fact]
    public void RejectsAnEmptyPlan() =>
        Should.Throw<ArgumentException>(() => Coverage.From([], 100.0, []));

    [Fact]
    public void RejectsANullFinishedSet() =>
        Should.Throw<ArgumentNullException>(() => Coverage.ForDuration(100.0, null!));

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsABadDuration(double duration) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Coverage.From(HundredSeconds.Plan, duration, [])
        );

    [Theory]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsABadGuard(double guard) =>
        Should.Throw<ArgumentOutOfRangeException>(() => HundredSeconds.With(guard, 0));

    [Fact]
    public void RejectsSharesThatDoNotMeet() =>
        Should.Throw<ArgumentException>(() =>
            Coverage.From(
                [
                    new TranscriptionWindow(0, 28, double.NegativeInfinity, 25),
                    new TranscriptionWindow(22, 50, 26, double.PositiveInfinity),
                ],
                50.0,
                [0, 1]
            )
        );

    [Fact]
    public void RejectsAFirstShareStartingAfterZero() =>
        Should.Throw<ArgumentException>(() =>
            Coverage.From([new TranscriptionWindow(0, 28, 1, double.PositiveInfinity)], 28.0, [0])
        );

    [Fact]
    public void RejectsALastShareEndingBeforeTheDuration() =>
        Should.Throw<ArgumentException>(() =>
            Coverage.From([new TranscriptionWindow(0, 28, double.NegativeInfinity, 27)], 28.0, [0])
        );

    [Fact]
    public void RejectsAStretchThatEndsBeforeItStarts() =>
        Should.Throw<ArgumentException>(() => HundredSeconds.With(0).Contains(10.0, 5.0));

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.0, double.NaN)]
    public void RejectsANaNStretch(double from, double to) =>
        Should.Throw<ArgumentException>(() => HundredSeconds.With(0).Contains(from, to));

    [Fact]
    public void RejectsANaNPosition() =>
        Should.Throw<ArgumentException>(() => HundredSeconds.With(0).CoveredAheadOf(double.NaN));
}

public class WhenReadingAnInterval
{
    [Fact]
    public void KnowsItsLength() => HundredSeconds.Interval(48.0, 68.0).Length.ShouldBe(20.0);
}
