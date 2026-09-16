using FoulFilterNet.Domain;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>Shared material for the reconciliation specs.</summary>
internal static class ReconcileFixture
{
    public static BadWordsList BadWords { get; } = BadWordsList.FromLines(["damn", "hell", "go to hell"]);

    /// <summary>
    /// The five occurrences of "damn" in <c>repeated_hits.mp3</c>, exact by
    /// construction - see <c>tests/fixtures/media/manifest.json</c>. The audio
    /// itself is not needed: what is under test is what the pipeline does with
    /// these times once the aligner has lost most of them.
    /// </summary>
    public static IReadOnlyList<(double Start, double End)> Occurrences { get; } =
    [
        (0.000, 0.487),
        (2.202, 2.689),
        (4.651, 5.138),
        (6.790, 7.277),
        (8.925, 9.412),
    ];

    /// <summary>One segment per occurrence, so the interpolated estimate is the true time.</summary>
    public static IReadOnlyList<Segment> SegmentsFor(IEnumerable<(double Start, double End)> spans) =>
        [.. spans.Select(span => new Segment(span.Start, span.End, "damn"))];

    public static IReadOnlyList<Candidate> CandidatesFor(IReadOnlyList<Segment> segments) =>
        PhraseMatcher.FindCandidates(segments, BadWords);
}

/// <summary>
/// Finding 2, and the reason this task exists. The Python collected the aligned
/// phrases into a <em>set of strings</em> and skipped any Candidate whose phrase
/// was in it, so recovering one "damn" marked all five as covered and the other
/// four were silently never censored - uncensored profanity shipped by a
/// profanity filter.
/// </summary>
public class WhenOnlyOneOfFiveRepeatedOccurrencesWasAligned
{
    private readonly IReadOnlyList<Hit> _hits;
    private readonly IReadOnlyList<Hit> _merged;

    public WhenOnlyOneOfFiveRepeatedOccurrencesWasAligned()
    {
        var segments = ReconcileFixture.SegmentsFor(ReconcileFixture.Occurrences);
        var candidates = ReconcileFixture.CandidatesFor(segments);

        // Alignment recovered the third occurrence and lost the other four.
        IReadOnlyList<Word> words = [new Word("damn", 4.651, 5.138)];

        _hits = new HitReconciler().Reconcile(candidates, words, ReconcileFixture.BadWords);
        _merged = new HitMerger().Merge(_hits);

        candidates.Count.ShouldBe(5);
        _hits.ShouldNotBeNull();
        _hits.Select(h => h.Start).ShouldBeInOrder();
    }

    [Fact]
    public void CensorsEveryOccurrenceRatherThanOnlyTheAlignedOne() => _hits.Count.ShouldBe(5);

    [Fact]
    public void TakesExactTimesFromTheOccurrenceAlignmentRecovered() =>
        _hits.Single(h => h.WordIndex is not null).Start.ShouldBe(4.651, 0.001);

    [Fact]
    public void FallsBackToTheSegmentEstimateForTheOnesItLost() =>
        _hits.Count(h => h.WordIndex is null).ShouldBe(4);

    [Fact]
    public void CoversEverySpokenOccurrenceAfterMerging() =>
        ReconcileFixture.Occurrences.ShouldAllBe(
            o => _merged.Any(m => m.Start <= o.Start && m.End >= o.End));

    [Fact]
    public void KeepsThemAsFiveSeparateCutWindows() => _merged.Count.ShouldBe(5);

    [Fact]
    public void LeavesTheCleanSpeechBetweenThemAlone() =>
        _merged.Sum(m => m.Duration).ShouldBeLessThan(5.0);
}

public class WhenAlignmentRecoveredEveryCandidate
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenAlignmentRecoveredEveryCandidate()
    {
        var segments = ReconcileFixture.SegmentsFor(ReconcileFixture.Occurrences.Take(2));
        var candidates = ReconcileFixture.CandidatesFor(segments);
        IReadOnlyList<Word> words =
            [new Word("damn", 0.000, 0.487), new Word("damn", 2.202, 2.689)];

        _hits = new HitReconciler().Reconcile(candidates, words, ReconcileFixture.BadWords);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void AddsNoEstimatesOnTopOfThem() => _hits.Count.ShouldBe(2);

    [Fact]
    public void KeepsEveryHitTiedToTheWordItCameFrom() =>
        _hits.ShouldAllBe(h => h.WordIndex != null);

    [Fact]
    public void UsesTheExactWordTimes() => _hits[1].End.ShouldBe(2.689, 0.001);
}

/// <summary>
/// Nothing aligned - the aligner was never run, or gave nothing back. Every
/// Candidate then falls back to its segment estimate, which is the Python's
/// behaviour when the word list is empty and the only branch it got right.
/// </summary>
public class WhenAlignmentRecoveredNothing
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenAlignmentRecoveredNothing()
    {
        var segments = ReconcileFixture.SegmentsFor(ReconcileFixture.Occurrences.Take(3));
        var candidates = ReconcileFixture.CandidatesFor(segments);

        _hits = new HitReconciler().Reconcile(candidates, [], ReconcileFixture.BadWords);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void StillCensorsEveryCandidate() => _hits.Count.ShouldBe(3);

    [Fact]
    public void MarksThemAsEstimatesRatherThanAlignedWords() =>
        _hits.ShouldAllBe(h => h.WordIndex == null);

    [Fact]
    public void UsesTheInterpolatedSegmentTimes() => _hits[2].Start.ShouldBe(4.651, 0.001);
}

/// <summary>An aligned hit inside the estimated window is plainly the same occurrence.</summary>
public class WhenTheAlignedHitOverlapsTheEstimate
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenTheAlignedHitOverlapsTheEstimate()
    {
        IReadOnlyList<Segment> segments = [new Segment(2.200, 2.690, "damn")];
        var candidates = ReconcileFixture.CandidatesFor(segments);
        IReadOnlyList<Word> words = [new Word("damn", 2.250, 2.700)];

        _hits = new HitReconciler().Reconcile(candidates, words, ReconcileFixture.BadWords);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void CensorsItOnce() => _hits.Count.ShouldBe(1);

    [Fact]
    public void PrefersTheExactTimesOverTheEstimate() => _hits[0].Start.ShouldBe(2.250, 0.001);

    [Fact]
    public void RecordsWhichWordItCameFrom() => _hits[0].WordIndex.ShouldBe(0);
}

/// <summary>
/// The tolerance is the distance at which <see cref="HitMerger"/> stops fusing
/// two windows: 0.15 s of pre-padding plus 0.25 s of post-padding. Inside it,
/// suppressing the Candidate and adding a fallback produce the same final cut,
/// so the choice cannot hurt; the fact below shows exactly that.
/// </summary>
public class WhenTheAlignedHitIsJustInsideTheTolerance
{
    private readonly IReadOnlyList<Hit> _hits;
    private readonly IReadOnlyList<Hit> _hadBothBeenKept;

    public WhenTheAlignedHitIsJustInsideTheTolerance()
    {
        IReadOnlyList<Segment> segments = [new Segment(2.000, 2.400, "damn")];
        var candidates = ReconcileFixture.CandidatesFor(segments);
        IReadOnlyList<Word> words = [new Word("damn", 2.800, 3.200)];

        _hits = new HitReconciler().Reconcile(candidates, words, ReconcileFixture.BadWords);
        _hadBothBeenKept = new HitMerger().Merge(
            [new Hit("damn", 2.800, 3.200, 0), new Hit("damn", 2.000, 2.400)]);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void TreatsItAsTheSameOccurrence() => _hits.Count.ShouldBe(1);

    [Fact]
    public void KeepsTheExactTimesRatherThanTheEstimate() => _hits[0].WordIndex.ShouldBe(0);

    [Fact]
    public void AtThisDistanceAFallbackWouldHaveMergedIntoTheSameWindowAnyway() =>
        _hadBothBeenKept.Count.ShouldBe(1);
}

/// <summary>
/// Past the tolerance the two choices stop being equivalent: a fallback here
/// becomes its own cut window, which is exactly what a second occurrence of the
/// word deserves.
/// </summary>
public class WhenTheAlignedHitIsBeyondTheTolerance
{
    private readonly IReadOnlyList<Hit> _hits;
    private readonly IReadOnlyList<Hit> _merged;

    public WhenTheAlignedHitIsBeyondTheTolerance()
    {
        IReadOnlyList<Segment> segments = [new Segment(2.000, 2.400, "damn")];
        var candidates = ReconcileFixture.CandidatesFor(segments);
        IReadOnlyList<Word> words = [new Word("damn", 2.850, 3.250)];

        _hits = new HitReconciler().Reconcile(candidates, words, ReconcileFixture.BadWords);
        _merged = new HitMerger().Merge(_hits);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsTheCandidateAsAHitOfItsOwn() => _hits.Count.ShouldBe(2);

    [Fact]
    public void MarksThatOneAsAnEstimate() =>
        _hits.Count(h => h.WordIndex is null).ShouldBe(1);

    [Fact]
    public void LeavesThemAsTwoSeparateCutWindows() => _merged.Count.ShouldBe(2);
}

/// <summary>
/// Position alone is not enough either: another word being aligned at the same
/// moment says nothing about whether this one was found, and the estimate is
/// only an interpolation, so the candidate keeps its own window.
/// </summary>
public class WhenADifferentPhraseWasAlignedAtTheSameMoment
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenADifferentPhraseWasAlignedAtTheSameMoment()
    {
        IReadOnlyList<Segment> segments = [new Segment(2.000, 2.400, "damn")];
        var candidates = ReconcileFixture.CandidatesFor(segments);
        IReadOnlyList<Word> words = [new Word("hell", 2.000, 2.400)];

        _hits = new HitReconciler().Reconcile(candidates, words, ReconcileFixture.BadWords);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void CensorsBothTheAlignedWordAndTheCandidate() => _hits.Count.ShouldBe(2);

    [Fact]
    public void KeepsTheCandidatesOwnPhrase() =>
        _hits.ShouldContain(h => h.Phrase == "damn" && h.WordIndex == null);

    [Fact]
    public void StillCensorsTheWordThatWasAligned() =>
        _hits.ShouldContain(h => h.Phrase == "hell" && h.WordIndex != null);
}

public class WhenTheReconcilerIsGivenWiderPadding
{
    private readonly HitReconciler _sut;
    private readonly IReadOnlyList<Hit> _hits;

    public WhenTheReconcilerIsGivenWiderPadding()
    {
        IReadOnlyList<Segment> segments = [new Segment(2.000, 2.400, "damn")];
        var candidates = ReconcileFixture.CandidatesFor(segments);
        IReadOnlyList<Word> words = [new Word("damn", 3.200, 3.600)];

        _sut = new HitReconciler(new HitPadding(0.5, 0.5));
        _hits = _sut.Reconcile(candidates, words, ReconcileFixture.BadWords);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void TakesItsToleranceFromThatPadding() => _sut.ToleranceSeconds.ShouldBe(1.0, 0.001);

    [Fact]
    public void TreatsAGapThatWidePaddingWouldCloseAsOneOccurrence() => _hits.Count.ShouldBe(1);
}

public class WhenReadingTheDefaultTolerance
{
    private readonly HitReconciler _sut;

    public WhenReadingTheDefaultTolerance()
    {
        _sut = new HitReconciler();

        _sut.ShouldNotBeNull();
    }

    // Anything closer than this merges into one cut window regardless, so this is
    // the distance at which "same occurrence" stops being a free choice.
    [Fact]
    public void MatchesTheDistanceAtWhichPaddedWindowsTouch() =>
        _sut.ToleranceSeconds.ShouldBe(HitPadding.Default.Pre + HitPadding.Default.Post, 0.001);

    [Fact]
    public void IsFourHundredMilliseconds() => _sut.ToleranceSeconds.ShouldBe(0.4, 0.001);
}

public class WhenThereIsNothingToReconcile
{
    private readonly HitReconciler _sut;
    private readonly IReadOnlyList<Hit> _hits;

    public WhenThereIsNothingToReconcile()
    {
        _sut = new HitReconciler();
        _hits = _sut.Reconcile([], [], ReconcileFixture.BadWords);

        _hits.ShouldNotBeNull();
    }

    [Fact]
    public void ProducesNoHits() => _hits.ShouldBeEmpty();

    [Fact]
    public void RejectsANullCandidateList() =>
        Should.Throw<ArgumentNullException>(() => _sut.Reconcile(null!, [], ReconcileFixture.BadWords));

    [Fact]
    public void RejectsANullWordList() =>
        Should.Throw<ArgumentNullException>(() => _sut.Reconcile([], null!, ReconcileFixture.BadWords));

    [Fact]
    public void RejectsANullBadWordsList() =>
        Should.Throw<ArgumentNullException>(() => _sut.Reconcile([], [], null!));
}
