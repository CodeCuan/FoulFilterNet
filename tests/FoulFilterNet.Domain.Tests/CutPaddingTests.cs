using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

public class WhenReadingThePriorityCutPadding
{
    private readonly CutPadding _padding;

    public WhenReadingThePriorityCutPadding()
    {
        _padding = CutPadding.ForPriorityWords(PriorityWordList.Default);

        _padding.ShouldNotBeNull();
        _padding.Ordinary.ShouldBe(HitPadding.Default);
    }

    // Crosstalk moves a start later by up to ~0.4 s on a word DTW still timed.
    [Fact]
    public void PadsAPriorityPreRollByTwoHundredAndFiftyMilliseconds() =>
        _padding.Priority.Pre.ShouldBe(0.25);

    // Measured: priority words end up to 0.44 s early under crosstalk.
    [Fact]
    public void PadsAPriorityPostRollByHalfASecond() => _padding.Priority.Post.ShouldBe(0.5);

    [Fact]
    public void GivesAPriorityHitAtLeastEightHundredMilliseconds() =>
        _padding.PriorityMinimumSeconds.ShouldBe(0.8);

    [Fact]
    public void KeepsTheListItWasGiven() =>
        _padding.PriorityWords.ShouldBeSameAs(PriorityWordList.Default);

    [Fact]
    public void ReachesOnePointZeroFiveSecondsBeforeAShortHitsEnd() =>
        _padding.ShortHitReachSeconds.ShouldBe(1.05, 0.0001);
}

public class WhenAPhraseIsOnThePriorityWordList
{
    private readonly CutPadding _padding = CutPadding.ForPriorityWords(PriorityWordList.Default);

    public WhenAPhraseIsOnThePriorityWordList()
    {
        _padding.PriorityWords.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void CountsAListedWordAsPriority() => _padding.IsPriority("fucking").ShouldBeTrue();

    [Fact]
    public void NormalizesThePhraseFirst() => _padding.IsPriority(" Fucking,").ShouldBeTrue();

    [Fact]
    public void DoesNotCountAnUnlistedWord() => _padding.IsPriority("damn").ShouldBeFalse();

    [Fact]
    public void DoesNotCountAPhraseThatOnlyContainsAListedWord() =>
        _padding.IsPriority("fucking hell").ShouldBeFalse();

    // A merged window joins its phrases with '+'; it holds a priority word.
    [Fact]
    public void CountsAMergedWindowHoldingAListedWord() =>
        _padding.IsPriority("damn+fuck").ShouldBeTrue();

    [Fact]
    public void DoesNotCountAMergedWindowOfUnlistedWords() =>
        _padding.IsPriority("damn+hell").ShouldBeFalse();

    [Fact]
    public void PadsAPriorityPhraseWithThePriorityPadding() =>
        _padding.For("fuck").ShouldBe(_padding.Priority);

    [Fact]
    public void PadsAnyOtherPhraseWithTheOrdinaryPadding() =>
        _padding.For("damn").ShouldBe(HitPadding.Default);

    [Fact]
    public void ToleratesThePriorityPrePlusPostForAPriorityPhrase() =>
        _padding.ToleranceFor("fuck").ShouldBe(0.75);

    [Fact]
    public void ToleratesTheOrdinaryPrePlusPostForAnyOtherPhrase() =>
        _padding.ToleranceFor("damn").ShouldBe(0.4);
}

public class WhenWideningAShortPriorityHit
{
    private readonly (double Start, double End) _window;

    public WhenWideningAShortPriorityHit()
    {
        // A 10 ms DTW word: grown backward from its end to 0.8 s, then padded.
        var padding = CutPadding.ForPriorityWords(PriorityWordList.Default);
        _window = padding.Widen("fuck", 4.16, 4.17);

        _window.End.ShouldBeGreaterThan(_window.Start);
    }

    [Fact]
    public void GrowsItBackwardFromItsEndBeforePadding() => _window.Start.ShouldBe(3.12, 0.0001);

    [Fact]
    public void PadsTheReportedEnd() => _window.End.ShouldBe(4.67, 0.0001);
}

public class WhenWideningALongEnoughPriorityHit
{
    private readonly (double Start, double End) _window;

    public WhenWideningALongEnoughPriorityHit()
    {
        var padding = CutPadding.ForPriorityWords(PriorityWordList.Default);
        _window = padding.Widen("fucking", 2.0, 3.0);

        _window.End.ShouldBeGreaterThan(_window.Start);
    }

    [Fact]
    public void OnlyPadsTheStart() => _window.Start.ShouldBe(1.75, 0.0001);

    [Fact]
    public void OnlyPadsTheEnd() => _window.End.ShouldBe(3.5, 0.0001);
}

public class WhenWideningAnOrdinaryHit
{
    private readonly (double Start, double End) _window;

    public WhenWideningAnOrdinaryHit()
    {
        var padding = CutPadding.ForPriorityWords(PriorityWordList.Default);
        _window = padding.Widen("damn", 4.16, 4.17);

        _window.End.ShouldBeGreaterThan(_window.Start);
    }

    [Fact]
    public void DoesNotGrowItToTheMinimum() => _window.Start.ShouldBe(4.01, 0.0001);

    [Fact]
    public void PadsItsEndOrdinarily() => _window.End.ShouldBe(4.42, 0.0001);

    // Clamping at zero is the merger's job, so a score can see the raw margin.
    [Fact]
    public void DoesNotClampAtZero() =>
        CutPadding.Default.Widen("damn", 0.05, 0.3).Start.ShouldBe(-0.1, 0.0001);
}

public class WhenThereAreNoPriorityWords
{
    private readonly CutPadding _padding = CutPadding.ForPriorityWords(
        PriorityWordList.FromLines([])
    );

    public WhenThereAreNoPriorityWords()
    {
        _padding.PriorityWords.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void CountsNothingAsPriority() => _padding.IsPriority("fuck").ShouldBeFalse();

    [Fact]
    public void PadsEveryPhraseOrdinarily() => _padding.For("fuck").ShouldBe(HitPadding.Default);

    [Fact]
    public void AddsNoReachBeyondTheOrdinaryPreRoll() =>
        _padding.ShortHitReachSeconds.ShouldBe(0.15);
}

public class WhenUsingTheStandardCutPaddings
{
    [Fact]
    public void DefaultPadsEverythingWithTheDefaultPadding() =>
        CutPadding.Default.For("fuck").ShouldBe(HitPadding.Default);

    [Fact]
    public void DefaultHasNoPriorityWords() =>
        CutPadding.Default.PriorityWords.IsEmpty.ShouldBeTrue();

    [Fact]
    public void NoneLeavesAHitAsItIs() =>
        CutPadding.None.Widen("damn", 1.0, 1.5).ShouldBe((1.0, 1.5));

    [Fact]
    public void RejectsANegativeMinimum() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new CutPadding(HitPadding.Default, PriorityWordList.Default, HitPadding.Default, -0.1)
        );

    [Fact]
    public void RejectsANullOrdinaryPadding() =>
        Should.Throw<ArgumentNullException>(() => new CutPadding(null!));

    [Fact]
    public void RejectsANullPriorityWordList() =>
        Should.Throw<ArgumentNullException>(() => CutPadding.ForPriorityWords(null!));
}
