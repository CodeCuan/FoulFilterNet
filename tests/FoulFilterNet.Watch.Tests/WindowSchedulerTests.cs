using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// The 200-second file the ordering tests use. <see cref="TranscriptionWindows.Plan"/>
/// starts its nine windows at 0, 22, 44, 66, 88, 110, 132, 154 and 172 s, so
/// the shares are <c>(-inf, 25)</c>, <c>[25, 47)</c>, <c>[47, 69)</c>,
/// <c>[69, 91)</c>, <c>[91, 113)</c>, <c>[113, 135)</c>, <c>[135, 157)</c>,
/// <c>[157, 177)</c> and <c>[177, +inf)</c>.
/// </summary>
internal static class TwoHundredSeconds
{
    public const double Duration = 200.0;

    public static readonly IReadOnlyList<TranscriptionWindow> Plan = TranscriptionWindows.Plan(
        Duration
    );

    public static int? Next(double playhead, params int[] finished) =>
        WindowScheduler.Next(Plan, finished, playhead);

    public static IReadOnlyList<int> Order(double playhead, params int[] finished) =>
        WindowScheduler.Order(Plan, finished, playhead);
}

/// <summary>A finished set that fails the test if it is read twice.</summary>
internal sealed class OnceOnly(params int[] indices) : IEnumerable<int>
{
    private bool _read;

    public IEnumerator<int> GetEnumerator()
    {
        _read.ShouldBeFalse("the finished set was enumerated more than once");
        _read = true;
        return ((IEnumerable<int>)indices).GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
}

public class WhenCheckingTheTwoHundredSecondPlan
{
    [Fact]
    public void HasNineWindows() => TwoHundredSeconds.Plan.Count.ShouldBe(9);

    [Fact]
    public void HasTheSharesTheOtherTestsAssume() =>
        TwoHundredSeconds
            .Plan.Select(w => w.KeepTo)
            .ShouldBe([
                25.0,
                47.0,
                69.0,
                91.0,
                113.0,
                135.0,
                157.0,
                177.0,
                double.PositiveInfinity,
            ]);

    [Fact]
    public void HasTheWindowStartsTheOtherTestsAssume() =>
        TwoHundredSeconds
            .Plan.Select(w => w.Start)
            .ShouldBe([0.0, 22.0, 44.0, 66.0, 88.0, 110.0, 132.0, 154.0, 172.0]);
}

public class WhenNothingIsFinishedAndThePlayheadIsAtTheStart
{
    [Fact]
    public void StartsWithTheFirstWindow() => TwoHundredSeconds.Next(0.0).ShouldBe(0);

    [Fact]
    public void GoesThroughEveryWindowInOrder() =>
        TwoHundredSeconds.Order(0.0).ShouldBe([0, 1, 2, 3, 4, 5, 6, 7, 8]);
}

public class WhenThePlayheadStaysAtTheStartAsWindowsFinish
{
    [Fact]
    public void TakesTheSecondWindowAfterTheFirst() => TwoHundredSeconds.Next(0.0, 0).ShouldBe(1);

    [Fact]
    public void TakesTheThirdWindowAfterTheFirstTwo() =>
        TwoHundredSeconds.Next(0.0, 0, 1).ShouldBe(2);

    [Fact]
    public void TakesTheLastWindowAfterAllTheOthers() =>
        TwoHundredSeconds.Next(0.0, 0, 1, 2, 3, 4, 5, 6, 7).ShouldBe(8);

    [Fact]
    public void LeavesTheRestInOrder() =>
        TwoHundredSeconds.Order(0.0, 0, 1, 2).ShouldBe([3, 4, 5, 6, 7, 8]);
}

public class WhenSeekingToTheMiddleWithNothingFinished
{
    // 100 s is in window 4's share, [91, 113).
    private readonly IReadOnlyList<int> _order = TwoHundredSeconds.Order(100.0);

    public WhenSeekingToTheMiddleWithNothingFinished() => _order.Count.ShouldBe(9);

    [Fact]
    public void StartsWithTheWindowWhoseShareHoldsThePlayhead() =>
        TwoHundredSeconds.Next(100.0).ShouldBe(4);

    [Fact]
    public void ContinuesInIndexOrderToTheEnd() => _order.Take(5).ShouldBe([4, 5, 6, 7, 8]);

    [Fact]
    public void ThenWrapsRoundNearestThePlayheadFirst() => _order.Skip(5).ShouldBe([3, 2, 1, 0]);

    [Fact]
    public void ListsEveryWindowOnce() => _order.Order().ShouldBe([0, 1, 2, 3, 4, 5, 6, 7, 8]);
}

public class WhenSeekingToTheMiddleAfterWatchingTheStart
{
    // The viewer watched the first three shares, then skipped to 100 s.
    [Fact]
    public void StartsWithTheWindowHoldingThePlayhead() =>
        TwoHundredSeconds.Next(100.0, 0, 1, 2).ShouldBe(4);

    [Fact]
    public void FillsTheSkippedWindowLast() =>
        TwoHundredSeconds.Order(100.0, 0, 1, 2).ShouldBe([4, 5, 6, 7, 8, 3]);
}

public class WhenEverythingAheadOfThePlayheadIsFinished
{
    // 100 s is in window 4's share; 4 to 8 are done.
    [Fact]
    public void WrapsToTheWindowJustBehindThePlayhead() =>
        TwoHundredSeconds.Next(100.0, 4, 5, 6, 7, 8).ShouldBe(3);

    [Fact]
    public void WorksBackTowardsTheStart() =>
        TwoHundredSeconds.Order(100.0, 4, 5, 6, 7, 8).ShouldBe([3, 2, 1, 0]);

    [Fact]
    public void SkipsFinishedWindowsBehindThePlayhead() =>
        TwoHundredSeconds.Order(100.0, 2, 4, 5, 6, 7, 8).ShouldBe([3, 1, 0]);
}

public class WhenOnlyTheFirstWindowIsLeft
{
    [Fact]
    public void TakesItFromThePlayheadAtTheEnd() =>
        TwoHundredSeconds.Next(190.0, 1, 2, 3, 4, 5, 6, 7, 8).ShouldBe(0);

    [Fact]
    public void TakesItFromThePlayheadInTheMiddle() =>
        TwoHundredSeconds.Next(100.0, 1, 2, 3, 4, 5, 6, 7, 8).ShouldBe(0);

    [Fact]
    public void OrdersNothingElse() =>
        TwoHundredSeconds.Order(100.0, 1, 2, 3, 4, 5, 6, 7, 8).ShouldBe([0]);
}

public class WhenOnlyTheLastWindowIsLeft
{
    [Fact]
    public void TakesItFromThePlayheadAtTheStart() =>
        TwoHundredSeconds.Next(0.0, 0, 1, 2, 3, 4, 5, 6, 7).ShouldBe(8);

    [Fact]
    public void OrdersNothingElse() =>
        TwoHundredSeconds.Order(0.0, 0, 1, 2, 3, 4, 5, 6, 7).ShouldBe([8]);
}

public class WhenTheWindowHoldingThePlayheadIsFinished
{
    // 50 s is in window 2's share, [47, 69).
    [Fact]
    public void TakesTheNextUnfinishedWindowAfterIt() =>
        HundredSecondScheduler.Next(50.0, 2).ShouldBe(3);

    [Fact]
    public void SkipsFinishedWindowsAfterItToo() =>
        HundredSecondScheduler.Next(50.0, 2, 3).ShouldBe(4);

    [Fact]
    public void OrdersTheRestAheadThenBehind() =>
        HundredSecondScheduler.Order(50.0, 2).ShouldBe([3, 4, 1, 0]);
}

public class WhenEveryWindowIsFinishedForScheduling
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(50.0)]
    [InlineData(100.0)]
    [InlineData(-10.0)]
    [InlineData(1000.0)]
    public void HasNoNextWindow(double playhead) =>
        HundredSecondScheduler.Next(playhead, 0, 1, 2, 3, 4).ShouldBeNull();

    [Fact]
    public void HasAnEmptyOrder() =>
        HundredSecondScheduler.Order(50.0, 0, 1, 2, 3, 4).ShouldBeEmpty();

    [Fact]
    public void HasNoNextWindowWhenFinishedOutOfOrder() =>
        HundredSecondScheduler.Next(50.0, 4, 2, 0, 3, 1).ShouldBeNull();
}

/// <summary>
/// Shares of the 100-second plan are <c>(-inf, 25)</c>, <c>[25, 47)</c>,
/// <c>[47, 69)</c>, <c>[69, 83)</c> and <c>[83, +inf)</c>; see
/// <see cref="HundredSeconds"/>.
/// </summary>
internal static class HundredSecondScheduler
{
    public static int? Next(double playhead, params int[] finished) =>
        WindowScheduler.Next(HundredSeconds.Plan, finished, playhead);

    public static IReadOnlyList<int> Order(double playhead, params int[] finished) =>
        WindowScheduler.Order(HundredSeconds.Plan, finished, playhead);
}

public class WhenThePlayheadIsExactlyOnAShareBoundary
{
    // Shares include KeepFrom and exclude KeepTo, as Stitch keeps a midpoint.
    [Theory]
    [InlineData(25.0, 1)]
    [InlineData(47.0, 2)]
    [InlineData(69.0, 3)]
    [InlineData(83.0, 4)]
    public void PicksTheLaterWindow(double boundary, int expected) =>
        HundredSecondScheduler.Next(boundary).ShouldBe(expected);

    [Theory]
    [InlineData(25.0, 0)]
    [InlineData(47.0, 1)]
    [InlineData(69.0, 2)]
    [InlineData(83.0, 3)]
    public void PicksTheEarlierWindowJustBeforeIt(double boundary, int expected) =>
        HundredSecondScheduler.Next(Math.BitDecrement(boundary)).ShouldBe(expected);

    [Fact]
    public void WrapsFromTheLaterWindow() =>
        HundredSecondScheduler.Order(47.0).ShouldBe([2, 3, 4, 1, 0]);
}

public class WhenThePlayheadIsInAnOverlap
{
    // Window 0 is [0, 28] and window 1 is [22, 50]; their shares meet at 25.
    [Fact]
    public void PicksTheEarlierWindowInTheFirstHalfOfTheOverlap() =>
        HundredSecondScheduler.Next(23.0).ShouldBe(0);

    [Fact]
    public void PicksTheLaterWindowInTheSecondHalfOfTheOverlap() =>
        HundredSecondScheduler.Next(27.0).ShouldBe(1);

    // Window 3 is [66, 94] and window 4, pulled back to end with the file, is
    // [72, 100]; their shares meet at 83.
    [Fact]
    public void PicksByShareNotByWhereTheLaterWindowStarts() =>
        HundredSecondScheduler.Next(75.0).ShouldBe(3);

    [Fact]
    public void PicksByShareNotByWhereTheEarlierWindowEnds() =>
        HundredSecondScheduler.Next(90.0).ShouldBe(4);

    [Fact]
    public void WrapsFromTheWindowWhoseShareHoldsIt() =>
        HundredSecondScheduler.Order(75.0).ShouldBe([3, 4, 2, 1, 0]);
}

public class WhenThePlayheadIsPastTheEnd
{
    [Theory]
    [InlineData(100.0)]
    [InlineData(100.001)]
    [InlineData(5000.0)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    public void PicksTheLastWindow(double playhead) =>
        HundredSecondScheduler.Next(playhead).ShouldBe(4);

    [Fact]
    public void WorksBackwardsFromTheEnd() =>
        HundredSecondScheduler.Order(5000.0).ShouldBe([4, 3, 2, 1, 0]);

    [Fact]
    public void WrapsToTheWindowBeforeTheLastOnceTheLastIsDone() =>
        HundredSecondScheduler.Next(5000.0, 4).ShouldBe(3);
}

public class WhenThePlayheadIsBeforeTheStart
{
    [Theory]
    [InlineData(-0.001)]
    [InlineData(-10.0)]
    [InlineData(double.MinValue)]
    [InlineData(double.NegativeInfinity)]
    public void PicksTheFirstWindow(double playhead) =>
        HundredSecondScheduler.Next(playhead).ShouldBe(0);

    [Fact]
    public void GoesThroughEveryWindowInOrder() =>
        HundredSecondScheduler.Order(-10.0).ShouldBe([0, 1, 2, 3, 4]);

    [Fact]
    public void TreatsNegativeZeroAsTheStart() => HundredSecondScheduler.Next(-0.0).ShouldBe(0);
}

public class WhenAnExplicitPlanHasBoundedShares
{
    // Shares [0, 25) and [25, 50): nothing holds a playhead outside [0, 50),
    // so it is clamped to the first or last window.
    private readonly IReadOnlyList<TranscriptionWindow> _plan =
    [
        new(0.0, 28.0, 0.0, 25.0),
        new(22.0, 50.0, 25.0, 50.0),
    ];

    [Fact]
    public void ClampsAPlayheadBeforeTheFirstShareToTheFirstWindow() =>
        WindowScheduler.Next(_plan, [], -3.0).ShouldBe(0);

    [Fact]
    public void ClampsAPlayheadAtTheLastShareEndToTheLastWindow() =>
        WindowScheduler.Next(_plan, [], 50.0).ShouldBe(1);

    [Fact]
    public void ClampsAPlayheadPastTheLastShareToTheLastWindow() =>
        WindowScheduler.Next(_plan, [], 60.0).ShouldBe(1);

    [Fact]
    public void OrdersFromTheClampedWindow() =>
        WindowScheduler.Order(_plan, [], 60.0).ShouldBe([1, 0]);
}

public class WhenThePlanHasOneWindow
{
    private readonly IReadOnlyList<TranscriptionWindow> _plan = TranscriptionWindows.Plan(8.0);

    public WhenThePlanHasOneWindow() => _plan.Count.ShouldBe(1);

    [Theory]
    [InlineData(0.0)]
    [InlineData(4.0)]
    [InlineData(8.0)]
    [InlineData(-1.0)]
    [InlineData(100.0)]
    public void PicksItWhereverThePlayheadIs(double playhead) =>
        WindowScheduler.Next(_plan, [], playhead).ShouldBe(0);

    [Fact]
    public void OrdersOnlyIt() => WindowScheduler.Order(_plan, [], 4.0).ShouldBe([0]);

    [Fact]
    public void HasNoNextWindowOnceItIsFinished() =>
        WindowScheduler.Next(_plan, [0], 4.0).ShouldBeNull();

    [Fact]
    public void HasAnEmptyOrderOnceItIsFinished() =>
        WindowScheduler.Order(_plan, [0], 4.0).ShouldBeEmpty();
}

public class WhenTheFileHasZeroDurationForScheduling
{
    private readonly IReadOnlyList<TranscriptionWindow> _plan = TranscriptionWindows.Plan(0.0);

    [Fact]
    public void PicksItsOnlyWindow() => WindowScheduler.Next(_plan, [], 0.0).ShouldBe(0);

    [Fact]
    public void HasNoNextWindowOnceItIsFinished() =>
        WindowScheduler.Next(_plan, [0], 0.0).ShouldBeNull();
}

public class WhenSchedulingALongFile
{
    // The 60-minute video from the W01 measurements.
    private readonly IReadOnlyList<TranscriptionWindow> _plan = TranscriptionWindows.Plan(3783.0);
    private readonly int _holding;
    private readonly IReadOnlyList<int> _order;

    public WhenSchedulingALongFile()
    {
        _holding = Enumerable
            .Range(0, _plan.Count)
            .Single(i => _plan[i].KeepFrom <= 1800.0 && 1800.0 < _plan[i].KeepTo);
        _order = WindowScheduler.Order(_plan, [], 1800.0);
        _order.Count.ShouldBe(_plan.Count);
    }

    [Fact]
    public void StartsWithTheWindowHoldingThePlayhead() => _order[0].ShouldBe(_holding);

    [Fact]
    public void ReachesTheLastWindowBeforeWrapping() =>
        _order[_plan.Count - 1 - _holding].ShouldBe(_plan.Count - 1);

    [Fact]
    public void WrapsToTheWindowJustBeforeThePlayhead() =>
        _order[_plan.Count - _holding].ShouldBe(_holding - 1);

    [Fact]
    public void EndsWithTheFirstWindow() => _order[^1].ShouldBe(0);

    [Fact]
    public void ListsEveryWindowOnce() => _order.Order().ShouldBe(Enumerable.Range(0, _plan.Count));
}

public class WhenFinishedIndicesRepeatForScheduling
{
    [Fact]
    public void IgnoresTheRepeatsInNext() =>
        HundredSecondScheduler.Next(0.0, 0, 0, 1, 1, 0).ShouldBe(2);

    [Fact]
    public void IgnoresTheRepeatsInOrder() =>
        HundredSecondScheduler.Order(0.0, 0, 0, 1, 1, 0).ShouldBe([2, 3, 4]);
}

public class WhenTheFinishedSetCanOnlyBeReadOnce
{
    [Fact]
    public void NextReadsItOnce() =>
        WindowScheduler.Next(HundredSeconds.Plan, new OnceOnly(0, 1), 0.0).ShouldBe(2);

    [Fact]
    public void OrderReadsItOnce() =>
        WindowScheduler.Order(HundredSeconds.Plan, new OnceOnly(0, 1), 0.0).ShouldBe([2, 3, 4]);
}

public class WhenOrderIsCalledTwice
{
    [Fact]
    public void GivesTheSameOrder() =>
        HundredSecondScheduler.Order(50.0, 1).ShouldBe(HundredSecondScheduler.Order(50.0, 1));
}

public class WhenGivenAFinishedIndexOutsideThePlanForScheduling
{
    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void NextThrows(int index) =>
        Should.Throw<ArgumentOutOfRangeException>(() => HundredSecondScheduler.Next(0.0, 0, index));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void OrderThrows(int index) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            HundredSecondScheduler.Order(0.0, 0, index)
        );

    [Fact]
    public void NextThrowsEvenWhenEveryWindowIsFinished() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            HundredSecondScheduler.Next(0.0, 0, 1, 2, 3, 4, 5)
        );

    [Fact]
    public void NamesTheFinishedSet() =>
        Should
            .Throw<ArgumentOutOfRangeException>(() => HundredSecondScheduler.Next(0.0, 7))
            .ParamName.ShouldBe("finished");
}

public class WhenGivenInvalidSchedulingArguments
{
    [Fact]
    public void NextRejectsANullPlan() =>
        Should.Throw<ArgumentNullException>(() => WindowScheduler.Next(null!, [], 0.0));

    [Fact]
    public void OrderRejectsANullPlan() =>
        Should.Throw<ArgumentNullException>(() => WindowScheduler.Order(null!, [], 0.0));

    [Fact]
    public void NextRejectsANullFinishedSet() =>
        Should.Throw<ArgumentNullException>(() =>
            WindowScheduler.Next(HundredSeconds.Plan, null!, 0.0)
        );

    [Fact]
    public void OrderRejectsANullFinishedSet() =>
        Should.Throw<ArgumentNullException>(() =>
            WindowScheduler.Order(HundredSeconds.Plan, null!, 0.0)
        );

    [Fact]
    public void NextRejectsAnEmptyPlan() =>
        Should.Throw<ArgumentException>(() => WindowScheduler.Next([], [], 0.0));

    [Fact]
    public void OrderRejectsAnEmptyPlan() =>
        Should.Throw<ArgumentException>(() => WindowScheduler.Order([], [], 0.0));

    [Fact]
    public void NextRejectsANaNPlayhead() =>
        Should
            .Throw<ArgumentException>(() => HundredSecondScheduler.Next(double.NaN))
            .ParamName.ShouldBe("playheadSeconds");

    [Fact]
    public void OrderRejectsANaNPlayhead() =>
        Should
            .Throw<ArgumentException>(() => HundredSecondScheduler.Order(double.NaN))
            .ParamName.ShouldBe("playheadSeconds");

    [Fact]
    public void RejectsSharesThatDoNotMeet() =>
        Should.Throw<ArgumentException>(() =>
            WindowScheduler.Next(
                [
                    new TranscriptionWindow(0, 28, double.NegativeInfinity, 25),
                    new TranscriptionWindow(22, 50, 26, double.PositiveInfinity),
                ],
                [],
                0.0
            )
        );

    [Fact]
    public void RejectsSharesThatOverlap() =>
        Should.Throw<ArgumentException>(() =>
            WindowScheduler.Order(
                [
                    new TranscriptionWindow(0, 28, double.NegativeInfinity, 25),
                    new TranscriptionWindow(22, 50, 24, double.PositiveInfinity),
                ],
                [],
                0.0
            )
        );

    [Fact]
    public void RejectsAShareWithNoLength() =>
        Should.Throw<ArgumentException>(() =>
            WindowScheduler.Next(
                [
                    new TranscriptionWindow(0, 28, double.NegativeInfinity, 25),
                    new TranscriptionWindow(22, 50, 25, 25),
                    new TranscriptionWindow(44, 72, 25, double.PositiveInfinity),
                ],
                [],
                0.0
            )
        );

    [Fact]
    public void RejectsAShareThatRunsBackwards() =>
        Should.Throw<ArgumentException>(() =>
            WindowScheduler.Next([new TranscriptionWindow(0, 28, 10, 5)], [], 0.0)
        );

    [Fact]
    public void RejectsAShareWithANaNEnd() =>
        Should.Throw<ArgumentException>(() =>
            WindowScheduler.Next([new TranscriptionWindow(0, 28, double.NaN, 5)], [], 0.0)
        );
}

/// <summary>
/// Many random states of many plans, with a fixed seed so a failure repeats:
/// <see cref="WindowScheduler.Order"/> must be exactly what calling
/// <see cref="WindowScheduler.Next"/> over and over gives while the playhead
/// stays put, and every state must obey the rule the other tests pin down by
/// example.
/// </summary>
public class WhenComparingOrderAndNextOverRandomStates
{
    private sealed record State(
        IReadOnlyList<TranscriptionWindow> Plan,
        HashSet<int> Finished,
        double Playhead,
        int Holding,
        int? Next,
        IReadOnlyList<int> Order
    );

    private readonly List<State> _states = [];

    public WhenComparingOrderAndNextOverRandomStates()
    {
        var random = new Random(20260918);
        for (var n = 0; n < 1000; n++)
        {
            var duration = random.Next(4) switch
            {
                0 => random.NextDouble() * 30.0,
                1 => random.NextDouble() * 200.0,
                _ => random.NextDouble() * 1200.0,
            };
            var plan = TranscriptionWindows.Plan(duration);
            var chance = random.NextDouble();
            var finished = Enumerable
                .Range(0, plan.Count)
                .Where(_ => random.NextDouble() < chance)
                .ToHashSet();
            var playhead = random.Next(10) switch
            {
                // Sometimes exactly on a share boundary, sometimes off the file.
                0 when plan.Count > 1 => plan[random.Next(1, plan.Count)].KeepFrom,
                1 => -random.NextDouble() * 50.0,
                2 => duration + random.NextDouble() * 50.0,
                _ => random.NextDouble() * duration,
            };
            var holding = Enumerable
                .Range(0, plan.Count)
                .Single(i => plan[i].KeepFrom <= playhead && playhead < plan[i].KeepTo);

            _states.Add(
                new State(
                    plan,
                    finished,
                    playhead,
                    holding,
                    WindowScheduler.Next(plan, finished, playhead),
                    WindowScheduler.Order(plan, finished, playhead)
                )
            );
        }

        _states.ShouldContain(s => s.Next == null);
        _states.ShouldContain(s => s.Finished.Count == 0);
        _states.ShouldContain(s => s.Order.Count > 1 && s.Order[s.Order.Count - 1] < s.Holding);
    }

    [Fact]
    public void NextIsTheHeadOfOrder() =>
        _states.ShouldAllBe(s => s.Next == (s.Order.Count == 0 ? null : s.Order[0]));

    [Fact]
    public void NextIsNullExactlyWhenEveryWindowIsFinished() =>
        _states.ShouldAllBe(s => (s.Next == null) == (s.Finished.Count == s.Plan.Count));

    [Fact]
    public void OrderListsEveryUnfinishedWindowExactlyOnce() =>
        _states.ShouldAllBe(s =>
            s.Order.Order()
                .SequenceEqual(
                    Enumerable.Range(0, s.Plan.Count).Where(i => !s.Finished.Contains(i))
                )
        );

    [Fact]
    public void OrderIsWhatRepeatedlyCallingNextGives() =>
        _states.ShouldAllBe(s => s.Order.SequenceEqual(Drain(s)));

    [Fact]
    public void NextIsTheWindowHoldingThePlayheadWhenThatIsUnfinished() =>
        _states.Where(s => !s.Finished.Contains(s.Holding)).ShouldAllBe(s => s.Next == s.Holding);

    [Fact]
    public void OrderRisesFromThePlayheadThenFallsBehindIt() =>
        _states.ShouldAllBe(s =>
            s.Order.SequenceEqual(
                s.Order.Where(i => i >= s.Holding)
                    .Order()
                    .Concat(s.Order.Where(i => i < s.Holding).OrderDescending())
            )
        );

    private static List<int> Drain(State state)
    {
        var finished = new HashSet<int>(state.Finished);
        var drained = new List<int>();
        while (WindowScheduler.Next(state.Plan, finished, state.Playhead) is { } next)
        {
            drained.Add(next);
            finished.Add(next);
        }

        return drained;
    }
}
