using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

public class WhenPlanningSubWindowsForAWindowShorterThanOne
{
    private readonly IReadOnlyList<TranscriptionWindow> _subWindows = PriorityWindows.Plan(3.0);

    [Fact]
    public void UsesOneSubWindow() => _subWindows.Count.ShouldBe(1);

    [Fact]
    public void CoversTheWholeWindowAndKeepsEverything() =>
        _subWindows[0]
            .ShouldBe(
                new TranscriptionWindow(0.0, 3.0, double.NegativeInfinity, double.PositiveInfinity)
            );

    [Fact]
    public void UsesOneSubWindowForExactlyOneLength() =>
        PriorityWindows
            .Plan(5.0)
            .ShouldBe([
                new TranscriptionWindow(0.0, 5.0, double.NegativeInfinity, double.PositiveInfinity),
            ]);

    [Fact]
    public void UsesOneEmptySubWindowForAnEmptyWindow() =>
        PriorityWindows
            .Plan(0.0)
            .ShouldBe([
                new TranscriptionWindow(0.0, 0.0, double.NegativeInfinity, double.PositiveInfinity),
            ]);

    [Fact]
    public void RejectsANegativeLength() =>
        Should.Throw<ArgumentOutOfRangeException>(() => PriorityWindows.Plan(-1.0));
}

public class WhenPlanningSubWindowsForAnExactMultipleOfTheStep
{
    private readonly IReadOnlyList<TranscriptionWindow> _subWindows = PriorityWindows.Plan(10.0);

    [Fact]
    public void StepsByHalfASubWindow() =>
        _subWindows.Select(w => w.Start).ShouldBe([0.0, 2.5, 5.0]);

    [Fact]
    public void EndsEachSubWindowOneLengthLater() =>
        _subWindows.Select(w => w.End).ShouldBe([5.0, 7.5, 10.0]);

    [Fact]
    public void SharesTheFirstOverlapAtItsMiddle() => _subWindows[0].KeepTo.ShouldBe(3.75);

    [Fact]
    public void SharesTheSecondOverlapAtItsMiddle() => _subWindows[1].KeepTo.ShouldBe(6.25);
}

public class WhenPlanningSubWindowsForAWindowJustOverOneLength
{
    private readonly IReadOnlyList<TranscriptionWindow> _subWindows = PriorityWindows.Plan(6.0);

    [Fact]
    public void PullsTheSecondSubWindowBackToEndWithTheWindow() =>
        _subWindows.ShouldBe([
            new TranscriptionWindow(0.0, 5.0, double.NegativeInfinity, 3.0),
            new TranscriptionWindow(1.0, 6.0, 3.0, double.PositiveInfinity),
        ]);
}

public class WhenPlanningSubWindowsForAFullTranscriptionWindow
{
    private readonly IReadOnlyList<TranscriptionWindow> _subWindows = PriorityWindows.Plan(
        TranscriptionWindows.LengthSeconds
    );

    [Fact]
    public void HearsItInElevenSubWindows() => _subWindows.Count.ShouldBe(11);

    [Fact]
    public void GivesEverySubWindowTheFullLength() =>
        _subWindows.ShouldAllBe(w => w.End - w.Start == PriorityWindows.LengthSeconds);

    [Fact]
    public void EndsWithTheWindow() => _subWindows[^1].End.ShouldBe(28.0);

    [Fact]
    public void PullsTheLastSubWindowBack() => _subWindows[^1].Start.ShouldBe(23.0);

    [Fact]
    public void HandsEachInstantToExactlyOneSubWindow() =>
        _subWindows.Zip(_subWindows.Skip(1)).ShouldAllBe(p => p.First.KeepTo == p.Second.KeepFrom);
}

public class WhenStitchingWhatTheSubWindowsHeard
{
    private readonly IReadOnlyList<Word> _stitched;

    public WhenStitchingWhatTheSubWindowsHeard()
    {
        var subWindows = PriorityWindows.Plan(10.0);
        subWindows.Count.ShouldBe(3);

        // Shares meet at 3.75 s and 6.25 s.
        _stitched = PriorityWindows.Stitch(
            [
                (
                    subWindows[0],
                    new TranscriptionResult(
                        [new Segment(0.0, 5.0, "hello fucking fuck")],
                        [
                            new Word(" fuck", 4.0, 4.4),
                            new Word(" Hello", 1.0, 1.4),
                            new Word(" Fucking,", 3.2, 3.6),
                        ]
                    )
                ),
                (
                    subWindows[1],
                    new TranscriptionResult(
                        [],
                        [
                            new Word(" fucking", 0.7, 1.1),
                            new Word(" fuck", 1.5, 1.9),
                            new Word(" shit", 2.0, 2.4),
                        ]
                    )
                ),
                (
                    subWindows[2],
                    new TranscriptionResult([], [new Word(" Motherfucker.", 3.0, 3.5)])
                ),
            ],
            PriorityWordList.Default
        );
    }

    [Fact]
    public void KeepsEachPriorityWordOnceInTimeOrder() =>
        _stitched.Select(w => w.Start).ShouldBe([3.2, 4.0, 8.0]);

    [Fact]
    public void KeepsAWordAsItWasHeard() => _stitched[0].Text.ShouldBe(" Fucking,");

    [Fact]
    public void RebasesALaterSubWindowOntoTheWindowsTimeline() =>
        _stitched[1].ShouldBe(new Word(" fuck", 4.0, 4.4));

    [Fact]
    public void RebasesTheLastSubWindowOntoTheWindowsTimeline() =>
        _stitched[2].ShouldBe(new Word(" Motherfucker.", 8.0, 8.5));

    [Fact]
    public void DiscardsWordsNotOnTheList() =>
        _stitched.ShouldNotContain(w => w.Text == " Hello" || w.Text == " shit");
}

public class WhenAPriorityWordsMidpointFallsOnASubWindowShare
{
    private readonly IReadOnlyList<Word> _stitched;

    public WhenAPriorityWordsMidpointFallsOnASubWindowShare()
    {
        var subWindows = PriorityWindows.Plan(10.0);

        // Midpoint 3.75 s, heard by both of the first two sub-windows.
        _stitched = PriorityWindows.Stitch(
            [
                (subWindows[0], new TranscriptionResult([], [new Word("fuck", 3.5, 4.0)])),
                (subWindows[1], new TranscriptionResult([], [new Word("fucking", 1.0, 1.5)])),
            ],
            PriorityWordList.Default
        );
    }

    [Fact]
    public void KeepsItOnce() => _stitched.Count.ShouldBe(1);

    [Fact]
    public void KeepsItFromTheLaterSubWindow() => _stitched[0].Text.ShouldBe("fucking");
}

public class WhenStitchingNothing
{
    [Fact]
    public void GivesNoWordsForNoSubWindows() =>
        PriorityWindows.Stitch([], PriorityWordList.Default).ShouldBeEmpty();

    [Fact]
    public void GivesNoWordsForAnEmptyList() =>
        PriorityWindows
            .Stitch(
                [
                    (
                        PriorityWindows.Plan(5.0)[0],
                        new TranscriptionResult([], [new Word("fuck", 1, 2)])
                    ),
                ],
                PriorityWordList.FromLines([])
            )
            .ShouldBeEmpty();
}

public class WhenMergingPriorityWordsIntoWhatThePrimaryPassHeard
{
    private readonly TranscriptionResult _primary;
    private readonly PriorityMerge _merged;

    public WhenMergingPriorityWordsIntoWhatThePrimaryPassHeard()
    {
        _primary = new TranscriptionResult(
            [new Segment(0.0, 2.3, "Well fucking hell")],
            [
                new Word(" Well", 0.0, 0.3),
                new Word(" fucking", 1.0, 1.4),
                new Word(" hell", 2.0, 2.3),
            ]
        );

        _merged = PriorityWindows.Merge(
            _primary,
            [
                new Word(" Fucking,", 1.2, 1.5), // the primary word again, timed a little differently
                new Word(" fucking", 1.4, 1.8), // same word, but only touching the primary one
                new Word(" fuck", 2.1, 2.2), // overlaps a different word
                new Word(" fuck", 0.5, 0.6), // the voice the primary pass left out
            ]
        );
    }

    [Fact]
    public void AddsTheMissedWordsInTimeOrder() =>
        _merged.Result.Words.Select(w => w.Start).ShouldBe([0.0, 0.5, 1.0, 1.4, 2.0, 2.1]);

    [Fact]
    public void DropsAWordThePrimaryPassAlreadyHeard() =>
        _merged.Result.Words.ShouldNotContain(new Word(" Fucking,", 1.2, 1.5));

    [Fact]
    public void KeepsTheSameWordWhenItOnlyTouchesThePrimaryOne() =>
        _merged.Result.Words.ShouldContain(new Word(" fucking", 1.4, 1.8));

    [Fact]
    public void KeepsAWordThatOverlapsADifferentOne() =>
        _merged.Result.Words.ShouldContain(new Word(" fuck", 2.1, 2.2));

    [Fact]
    public void ReportsWhatItAddedInTimeOrder() =>
        _merged.Added.ShouldBe([
            new Word(" fuck", 0.5, 0.6),
            new Word(" fucking", 1.4, 1.8),
            new Word(" fuck", 2.1, 2.2),
        ]);

    [Fact]
    public void LeavesTheSegmentsAlone() => _merged.Result.Segments.ShouldBe(_primary.Segments);
}

public class WhenMergingWithNothingToAdd
{
    private readonly TranscriptionResult _primary = new(
        [new Segment(0.0, 1.0, "fuck")],
        [new Word(" fuck", 0.2, 0.6)]
    );

    [Fact]
    public void ReturnsThePrimaryResultForNoPriorityWords() =>
        PriorityWindows.Merge(_primary, []).Result.ShouldBeSameAs(_primary);

    [Fact]
    public void ReturnsThePrimaryResultWhenEveryWordIsADuplicate() =>
        PriorityWindows
            .Merge(_primary, [new Word("FUCK!", 0.5, 0.9)])
            .Result.ShouldBeSameAs(_primary);

    [Fact]
    public void ReportsNothingAdded() =>
        PriorityWindows.Merge(_primary, [new Word("fuck", 0.1, 0.3)]).Added.ShouldBeEmpty();

    [Fact]
    public void AddsEverythingToAPrimaryResultWithNoWords() =>
        PriorityWindows
            .Merge(new TranscriptionResult([], []), [new Word("fuck", 0.1, 0.3)])
            .Result.Words.ShouldBe([new Word("fuck", 0.1, 0.3)]);
}
