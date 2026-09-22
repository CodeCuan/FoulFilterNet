using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

/// <summary>Ports <c>test_merge_pads_and_merges_overlaps</c> from <c>Legacy/src/test_pipeline_logic.py</c>.</summary>
public class WhenMergingOverlappingHits
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenMergingOverlappingHits()
    {
        var sut = new HitMerger(HitPadding.Default);
        _merged = sut.Merge([new Hit("x", 1.0, 1.5), new Hit("y", 1.55, 2.0)]);

        _merged.ShouldNotBeNull();
        _merged.ShouldAllBe(h => h.End > h.Start);
        _merged.Select(h => h.Start).ShouldBeInOrder();
    }

    // Padding of -0.15/+0.25 makes these two windows touch.
    [Fact]
    public void CollapsesToASingleWindow() => _merged.Count.ShouldBe(1);

    [Fact]
    public void StartsAtThePaddedFirstHit() => _merged[0].Start.ShouldBe(0.85, 0.001);

    [Fact]
    public void EndsAtThePaddedLastHit() => _merged[0].End.ShouldBe(2.25, 0.001);

    [Fact]
    public void ConcatenatesPhrases() => _merged[0].Phrase.ShouldBe("x+y");
}

/// <summary>Ports <c>test_merge_drops_inverted_windows_and_keeps_order</c>.</summary>
public class WhenSomeHitsAreInvertedOrOutOfOrder
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenSomeHitsAreInvertedOrOutOfOrder()
    {
        var sut = new HitMerger(HitPadding.Default);
        _merged = sut.Merge([
            new Hit("x", 5.0, 5.2),
            new Hit("x", 9.0, 9.0),
            new Hit("x", 3.0, 3.6),
        ]);

        _merged.ShouldNotBeNull();
        _merged.ShouldAllBe(h => h.End > h.Start);
    }

    [Fact]
    public void DropsTheZeroLengthWindow() => _merged.Count.ShouldBe(2);

    [Fact]
    public void SortsWhatSurvivesByStart() =>
        _merged.Select(h => h.Start).ShouldBe([2.85, 4.85], 0.001);

    [Fact]
    public void LeavesNonOverlappingWindowsSeparate() => _merged[0].End.ShouldBe(3.85, 0.001);

    [Fact]
    public void DropsAWindowThatEndsBeforeItStarts() =>
        new HitMerger().Merge([new Hit("x", 4.0, 3.0)]).ShouldBeEmpty();
}

/// <summary>Ports <c>test_merge_clamps_negative_start</c>.</summary>
public class WhenAHitStartsNearTheBeginningOfTheFile
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenAHitStartsNearTheBeginningOfTheFile()
    {
        var sut = new HitMerger(HitPadding.Default);
        _merged = sut.Merge([new Hit("x", 0.02, 0.4)]);

        _merged.ShouldNotBeNull();
        _merged.Count.ShouldBe(1);
    }

    [Fact]
    public void ClampsTheStartAtZero() => _merged[0].Start.ShouldBe(0.0);

    [Fact]
    public void StillPadsTheEnd() => _merged[0].End.ShouldBe(0.65, 0.001);

    [Fact]
    public void NeverProducesANegativeStart() =>
        new HitMerger().Merge([new Hit("x", 0.0, 0.1)])[0].Start.ShouldBe(0.0);
}

public class WhenTwoPaddedWindowsExactlyTouch
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenTwoPaddedWindowsExactlyTouch()
    {
        // 1.0-1.5 pads to 0.85-1.75; 1.9-2.2 pads to 1.75-2.45.
        var sut = new HitMerger(HitPadding.Default);
        _merged = sut.Merge([new Hit("a", 1.0, 1.5), new Hit("b", 1.9, 2.2)]);

        _merged.ShouldNotBeNull();
        _merged.ShouldAllBe(h => h.End > h.Start);
    }

    [Fact]
    public void CollapsesThemIntoOne() => _merged.Count.ShouldBe(1);

    [Fact]
    public void SpansBothWindows() => _merged[0].End.ShouldBe(2.45, 0.001);

    [Fact]
    public void RecordsBothPhrases() => _merged[0].Phrase.ShouldBe("a+b");
}

public class WhenOneHitSwallowsAnother
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenOneHitSwallowsAnother()
    {
        var sut = new HitMerger(HitPadding.Default);
        _merged = sut.Merge([new Hit("long", 1.0, 5.0), new Hit("short", 2.0, 2.2)]);

        _merged.ShouldNotBeNull();
        _merged.Count.ShouldBe(1);
    }

    [Fact]
    public void KeepsTheLongerEnd() => _merged[0].End.ShouldBe(5.25, 0.001);

    [Fact]
    public void DoesNotShrinkToTheContainedWindow() => _merged[0].Start.ShouldBe(0.85, 0.001);

    [Fact]
    public void StillRecordsBothPhrases() => _merged[0].Phrase.ShouldBe("long+short");
}

public class WhenMergingHitsThatNeedRounding
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenMergingHitsThatNeedRounding()
    {
        var sut = new HitMerger(HitPadding.Default);
        _merged = sut.Merge([new Hit("x", 1.0001, 1.9999)]);

        _merged.ShouldNotBeNull();
        _merged.Count.ShouldBe(1);
    }

    [Fact]
    public void RoundsTheStartToThreePlaces() => _merged[0].Start.ShouldBe(0.85);

    [Fact]
    public void RoundsTheEndToThreePlaces() => _merged[0].End.ShouldBe(2.25);

    [Fact]
    public void KeepsTheWordIndexOfTheHitItCameFrom() =>
        new HitMerger().Merge([new Hit("x", 1.0, 1.5, 7)])[0].WordIndex.ShouldBe(7);
}

public class WhenThereIsNothingToMerge
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenThereIsNothingToMerge()
    {
        var sut = new HitMerger();
        _merged = sut.Merge([]);

        _merged.ShouldNotBeNull();
    }

    [Fact]
    public void ReturnsNoWindows() => _merged.ShouldBeEmpty();

    [Fact]
    public void RejectsANullHitList() =>
        Should.Throw<ArgumentNullException>(() => new HitMerger().Merge(null!));
}

public class WhenReadingTheDefaultPadding
{
    private readonly HitPadding _padding;

    public WhenReadingTheDefaultPadding()
    {
        _padding = HitPadding.Default;

        _padding.ShouldNotBeNull();
    }

    // Asymmetric on purpose: speech onsets are detected late.
    [Fact]
    public void PadsThePreRollByOneHundredAndFiftyMilliseconds() => _padding.Pre.ShouldBe(0.15);

    [Fact]
    public void PadsThePostRollByTwoHundredAndFiftyMilliseconds() => _padding.Post.ShouldBe(0.25);

    [Fact]
    public void IsWhatAMergerUsesWhenGivenNothing() =>
        new HitMerger().Merge([new Hit("x", 1.0, 1.5)])[0].Start.ShouldBe(0.85, 0.001);
}

public class WhenMergingPriorityHits
{
    private readonly IReadOnlyList<Hit> _merged;

    public WhenMergingPriorityHits()
    {
        // ct_edge_1_1000ms: primary and prompted pass each placed the F-word as
        // a 10 ms point, 80 ms apart; the damn is ordinary.
        var sut = new HitMerger(CutPadding.ForPriorityWords(PriorityWordList.Default));
        _merged = sut.Merge([
            new Hit("fuck", 3.412, 3.422, 4),
            new Hit("fuck", 3.500, 3.510, 5),
            new Hit("damn", 6.0, 6.5, 9),
        ]);

        _merged.ShouldNotBeNull();
        _merged.ShouldAllBe(h => h.End > h.Start);
    }

    [Fact]
    public void FusesTheTwoReportsOfOneWord() => _merged.Count.ShouldBe(2);

    // 3.422 - 0.8 - 0.25
    [Fact]
    public void GrowsThePriorityWindowBackwardAndPadsIt() => _merged[0].Start.ShouldBe(2.372);

    // 3.510 + 0.5
    [Fact]
    public void PadsThePriorityWindowsEndByHalfASecond() => _merged[0].End.ShouldBe(4.01);

    [Fact]
    public void RecordsBothReports() => _merged[0].Phrase.ShouldBe("fuck+fuck");

    [Fact]
    public void PadsAnOrdinaryHitAsBefore() =>
        (_merged[1].Start, _merged[1].End).ShouldBe((5.85, 6.75));

    [Fact]
    public void ReportsTheOrdinaryPaddingAsItsPadding() =>
        new HitMerger(CutPadding.ForPriorityWords(PriorityWordList.Default)).Padding.ShouldBe(
            HitPadding.Default
        );

    [Fact]
    public void ExposesTheCutPaddingItApplies() =>
        new HitMerger(CutPadding.None).CutPadding.ShouldBeSameAs(CutPadding.None);

    [Fact]
    public void ClampsAPriorityWindowAtTheStartOfTheFile() =>
        new HitMerger(CutPadding.ForPriorityWords(PriorityWordList.Default))
            .Merge([new Hit("fuck", 0.3, 0.31)])[0]
            .Start.ShouldBe(0.0);

    [Fact]
    public void StillDropsAZeroLengthPriorityHit() =>
        new HitMerger(CutPadding.ForPriorityWords(PriorityWordList.Default))
            .Merge([new Hit("fuck", 3.0, 3.0)])
            .ShouldBeEmpty();

    [Fact]
    public void UsesTheDefaultCutPaddingWhenGivenAHitPadding() =>
        new HitMerger(HitPadding.Default).CutPadding.Ordinary.ShouldBe(HitPadding.Default);
}
