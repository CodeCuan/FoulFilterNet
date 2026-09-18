using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>One stretch of speech on the file's timeline: a Segment and its Words.</summary>
internal sealed record Utterance(IReadOnlyList<Word> Words, string? SegmentText = null)
{
    public double Start => Words[0].Start;

    public double End => Words[^1].End;

    public string Text => SegmentText ?? string.Join(' ', Words.Select(w => w.Text));

    public static Utterance Of(params Word[] words) => new(words);

    public static Word W(string text, double start, double end) => new(text, start, end);
}

/// <summary>
/// A synthetic Web Video: what was said, on the file's timeline, and what each
/// window of <see cref="TranscriptionWindows.Plan"/> hears of it - every
/// Segment and Word lying wholly inside the window's audio, moved onto the
/// window's own timeline (to the millisecond), as the engine returns it. Neighbouring windows both
/// hear their overlap; <see cref="TranscriptionWindows.Stitch"/> decides who
/// keeps it.
/// </summary>
internal sealed class Script(double duration, params Utterance[] utterances)
{
    public double Duration => duration;

    public IReadOnlyList<TranscriptionWindow> Plan { get; } = TranscriptionWindows.Plan(duration);

    public TranscriptionResult Heard(int index)
    {
        var window = Plan[index];
        var segments = new List<Segment>();
        var words = new List<Word>();

        foreach (var utterance in utterances)
        {
            if (utterance.Start >= window.Start && utterance.End <= window.End)
            {
                segments.Add(
                    new Segment(
                        Times.Round(utterance.Start - window.Start),
                        Times.Round(utterance.End - window.Start),
                        utterance.Text
                    )
                );
            }

            foreach (var word in utterance.Words)
            {
                if (word.Start >= window.Start && word.End <= window.End)
                {
                    words.Add(
                        new Word(
                            word.Text,
                            Times.Round(word.Start - window.Start),
                            Times.Round(word.End - window.Start)
                        )
                    );
                }
            }
        }

        return new TranscriptionResult(segments, words);
    }

    public WatchProgress Start(BadWordsList badWords) => WatchProgress.Start(duration, badWords);

    /// <summary>Finish the windows at <paramref name="order"/>, one at a time.</summary>
    public WatchProgress Finish(BadWordsList badWords, params int[] order)
    {
        var progress = Start(badWords);
        foreach (var index in order)
        {
            progress = progress.With(index, Heard(index));
        }

        return progress;
    }

    public int[] AllWindows => [.. Enumerable.Range(0, Plan.Count)];

    /// <summary>The whole file stitched at once, as the batch engine does.</summary>
    public TranscriptionResult Batch() =>
        TranscriptionWindows.Stitch([.. AllWindows.Select(i => (Plan[i], Heard(i)))]);

    /// <summary>
    /// The batch pipeline's own rules run directly over <see cref="Batch"/>:
    /// Candidates, reconcile, merge, all with the default padding.
    /// </summary>
    public IReadOnlyList<Hit> BatchHits(BadWordsList badWords)
    {
        var batch = Batch();
        var candidates = PhraseMatcher.FindCandidates(batch.Segments, badWords);
        var reconciled = new HitReconciler(HitPadding.Default).Reconcile(
            candidates,
            batch.Words,
            badWords
        );
        return new HitMerger(HitPadding.Default).Merge(reconciled);
    }
}

/// <summary>
/// The 100-second video most of these tests watch. Its windows start at 0, 22,
/// 44, 66 and 72 s; their shares are <c>[0, 25)</c>, <c>[25, 47)</c>,
/// <c>[47, 69)</c>, <c>[69, 83)</c> and <c>[83, 100]</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>"well damn it" at 3-4.5 s: window 0 only.</item>
/// <item>"go to" at 24-24.8 s and "hell yes" at 25.1-25.6 s: two Segments
/// either side of the 25 s share boundary, both heard by windows 0 and 1, so
/// "go to hell" is only whole once both are done.</item>
/// <item>"oh shit" at 46.5-47.5 s: "oh" is window 1's, "shit" window 2's.</item>
/// <item>"damn" at 68.5 s and "damn" at 69.1 s: either side of the 69 s
/// boundary, so their padded Hits overlap across it.</item>
/// <item>"Crap!" at 90 s: window 4 only, cased and punctuated.</item>
/// </list>
/// </remarks>
internal static class HundredSecondVideo
{
    public static readonly Script Script = new(
        100.0,
        Utterance.Of(
            Utterance.W("well", 3.0, 3.3),
            Utterance.W("damn", 3.4, 3.8),
            Utterance.W("it", 3.9, 4.5)
        ),
        Utterance.Of(Utterance.W("go", 24.0, 24.3), Utterance.W("to", 24.4, 24.8)),
        Utterance.Of(Utterance.W("hell", 25.1, 25.4), Utterance.W("yes", 25.45, 25.6)),
        Utterance.Of(Utterance.W("oh", 46.5, 46.8), Utterance.W("shit", 47.1, 47.5)),
        Utterance.Of(Utterance.W("damn", 68.5, 68.8)),
        Utterance.Of(Utterance.W("damn", 69.1, 69.4)),
        Utterance.Of(Utterance.W("Crap!", 90.0, 90.4))
    );

    public static readonly BadWordsList BadWords = BadWordsList.FromLines([
        "damn",
        "go to hell",
        "shit",
        "crap",
    ]);

    public static WatchProgress Finish(params int[] order) => Script.Finish(BadWords, order);

    public static Hit Hit(string phrase, double start, double end, int? wordIndex = null) =>
        new(phrase, start, end, wordIndex);
}

public class WhenCheckingTheHundredSecondVideo
{
    [Fact]
    public void HasFiveWindows() => HundredSecondVideo.Script.Plan.Count.ShouldBe(5);

    [Fact]
    public void HasTheWindowStartsTheOtherTestsAssume() =>
        HundredSecondVideo.Script.Plan.Select(w => w.Start).ShouldBe([0.0, 22.0, 44.0, 66.0, 72.0]);

    [Fact]
    public void HasTheSharesTheOtherTestsAssume() =>
        HundredSecondVideo
            .Script.Plan.Select(w => w.KeepTo)
            .ShouldBe([25.0, 47.0, 69.0, 83.0, double.PositiveInfinity]);

    [Fact]
    public void HasWindowZeroHearAcrossTheFirstShareBoundary() =>
        HundredSecondVideo.Script.Heard(0).Words.Select(w => w.Text).ShouldContain("hell");

    [Fact]
    public void HasWindowOneHearAcrossTheFirstShareBoundary() =>
        HundredSecondVideo.Script.Heard(1).Words.Select(w => w.Text).ShouldContain("go");

    [Fact]
    public void GivesTheEngineWindowLocalTimes() =>
        HundredSecondVideo.Script.Heard(4).Words.ShouldBe([new Word("Crap!", 18.0, 18.4)]);
}

public class WhenNoWindowIsFinished
{
    private readonly WatchProgress _progress;
    private readonly HitSnapshot _snapshot;

    public WhenNoWindowIsFinished()
    {
        _progress = HundredSecondVideo.Finish();
        _snapshot = _progress.Snapshot;
    }

    [Fact]
    public void HasNoHits() => _snapshot.Hits.ShouldBeEmpty();

    [Fact]
    public void HasEmptyCoverage() => _snapshot.Coverage.IsEmpty.ShouldBeTrue();

    [Fact]
    public void KnowsTheDurationItCovers() => _snapshot.Coverage.DurationSeconds.ShouldBe(100.0);

    [Fact]
    public void IsAtRevisionZero() => _snapshot.Revision.ShouldBe(0);

    [Fact]
    public void HasNoSegments() => _snapshot.Transcript.Segments.ShouldBeEmpty();

    [Fact]
    public void HasNoWords() => _snapshot.Transcript.Words.ShouldBeEmpty();

    [Fact]
    public void HasFinishedNoWindows() => _snapshot.FinishedWindowCount.ShouldBe(0);

    [Fact]
    public void CountsEveryWindowInThePlan() => _snapshot.TotalWindows.ShouldBe(5);

    [Fact]
    public void IsNotComplete() => _snapshot.IsComplete.ShouldBeFalse();

    [Fact]
    public void ListsNoFinishedWindows() => _progress.FinishedWindows.ShouldBeEmpty();

    [Fact]
    public void KeepsThePlan() => _progress.Plan.ShouldBe(HundredSecondVideo.Script.Plan);

    [Fact]
    public void KeepsTheDuration() => _progress.DurationSeconds.ShouldBe(100.0);

    [Fact]
    public void KeepsTheBadWordsList() =>
        _progress.BadWords.ShouldBeSameAs(HundredSecondVideo.BadWords);

    [Fact]
    public void ReportsNoWindowAsFinished() => _progress.IsFinished(0).ShouldBeFalse();
}

public class WhenOnlyTheLastWindowOfTheVideoIsHeard
{
    private readonly HitSnapshot _snapshot;

    public WhenOnlyTheLastWindowOfTheVideoIsHeard()
    {
        _snapshot = HundredSecondVideo.Finish(4).Snapshot;

        _snapshot.Hits.Count.ShouldBe(1);
    }

    [Fact]
    public void StartsTheHitOnTheFileTimelineWithItsPadding() =>
        _snapshot.Hits[0].Start.ShouldBe(89.85);

    [Fact]
    public void EndsTheHitOnTheFileTimelineWithItsPadding() =>
        _snapshot.Hits[0].End.ShouldBe(90.65);

    [Fact]
    public void MatchesDespiteCaseAndPunctuation() => _snapshot.Hits[0].Phrase.ShouldBe("crap");

    [Fact]
    public void PointsTheHitAtItsWordInTheTranscript() =>
        _snapshot.Transcript.Words[_snapshot.Hits[0].WordIndex!.Value].Text.ShouldBe("Crap!");

    [Fact]
    public void MovesTheWordsOntoTheFileTimeline() =>
        _snapshot.Transcript.Words.ShouldBe([new Word("Crap!", 90.0, 90.4)]);

    [Fact]
    public void MovesTheSegmentsOntoTheFileTimeline() =>
        _snapshot.Transcript.Segments.ShouldBe([new Segment(90.0, 90.4, "Crap!")]);

    [Fact]
    public void IsAtRevisionOne() => _snapshot.Revision.ShouldBe(1);

    [Fact]
    public void HasFinishedOneWindow() => _snapshot.FinishedWindowCount.ShouldBe(1);

    [Fact]
    public void CoversTheLastShareToTheEndOfTheFile() =>
        _snapshot.Coverage.Intervals.ShouldBe([new CoverageInterval(84.0, 100.0)]);

    [Fact]
    public void IsNotComplete() => _snapshot.IsComplete.ShouldBeFalse();
}

public class WhenAWindowHearsWordsInItsNeighboursShare
{
    private readonly HitSnapshot _snapshot;

    public WhenAWindowHearsWordsInItsNeighboursShare()
    {
        _snapshot = HundredSecondVideo.Finish(0).Snapshot;

        // The precondition: window 0's audio runs to 28 s, so it heard these.
        HundredSecondVideo.Script.Heard(0).Words.Select(w => w.Text).ShouldContain("yes");
    }

    [Fact]
    public void DropsTheWordsPastItsShare() =>
        _snapshot.Transcript.Words.Select(w => w.Text).ShouldBe(["well", "damn", "it", "go", "to"]);

    [Fact]
    public void DropsTheSegmentsPastItsShare() =>
        _snapshot.Transcript.Segments.Select(s => s.Text).ShouldBe(["well damn it", "go to"]);

    [Fact]
    public void DoesNotYetReportThePhraseCompletedPastItsShare() =>
        _snapshot.Hits.ShouldNotContain(h => h.Phrase.Contains("go to hell"));

    [Fact]
    public void ReportsOnlyTheHitInsideItsShare() =>
        _snapshot.Hits.ShouldBe([HundredSecondVideo.Hit("damn", 3.25, 4.05, 1)]);
}

public class WhenAPhraseSpansTwoSharesAndOnlyTheLaterIsFinished
{
    private readonly HitSnapshot _snapshot = HundredSecondVideo.Finish(1).Snapshot;

    [Fact]
    public void DoesNotReportThePhrase() =>
        _snapshot.Hits.ShouldNotContain(h => h.Phrase.Contains("go to hell"));

    [Fact]
    public void ReportsNothingElse() => _snapshot.Hits.ShouldBeEmpty();

    [Fact]
    public void KeepsOnlyItsOwnHalfOfThePhrase() =>
        _snapshot.Transcript.Words.Select(w => w.Text).ShouldBe(["hell", "yes", "oh"]);
}

public class WhenAPhraseSpansTwoSharesAndBothAreFinished
{
    private readonly HitSnapshot _snapshot;

    public WhenAPhraseSpansTwoSharesAndBothAreFinished()
    {
        _snapshot = HundredSecondVideo.Finish(1, 0).Snapshot;

        _snapshot.Hits.Count.ShouldBe(2);
    }

    [Fact]
    public void ReportsThePhraseAsOneHit() => _snapshot.Hits[1].Phrase.ShouldBe("go to hell");

    [Fact]
    public void StartsItAtThePaddedFirstWord() => _snapshot.Hits[1].Start.ShouldBe(23.85);

    [Fact]
    public void EndsItAtThePaddedLastWord() => _snapshot.Hits[1].End.ShouldBe(25.65);

    [Fact]
    public void TakesItFromTheWords() => _snapshot.Hits[1].WordIndex.ShouldBe(3);

    [Fact]
    public void StillReportsTheEarlierHit() => _snapshot.Hits[0].Phrase.ShouldBe("damn");
}

/// <summary>
/// Whisper put "go to hell" in one Segment whose midpoint is window 0's, but
/// "hell" is window 1's word. Window 0 heard the whole Segment, so the batch
/// rules fall back to its estimate until window 1's words confirm it.
/// </summary>
public class WhenAPhraseInOneSegmentSpansTwoShares
{
    private static readonly Script Script = new(
        100.0,
        Utterance.Of(
            Utterance.W("go", 24.0, 24.3),
            Utterance.W("to", 24.4, 24.8),
            Utterance.W("hell", 25.1, 25.4)
        )
    );

    private static readonly BadWordsList BadWords = BadWordsList.FromLines(["go to hell"]);

    private readonly HitSnapshot _before;
    private readonly HitSnapshot _after;

    public WhenAPhraseInOneSegmentSpansTwoShares()
    {
        _before = Script.Finish(BadWords, 0).Snapshot;
        _after = Script.Finish(BadWords, 0, 1).Snapshot;

        _before.Hits.Count.ShouldBe(1);
        _after.Hits.Count.ShouldBe(1);
    }

    [Fact]
    public void ReportsTheSegmentEstimateOnceTheSegmentsWindowIsDone() =>
        _before.Hits[0].WordIndex.ShouldBeNull();

    [Fact]
    public void SendsTheEstimateEvenThoughItReachesPastCoverage() =>
        _before.Coverage.Contains(_before.Hits[0].Start, _before.Hits[0].End).ShouldBeFalse();

    [Fact]
    public void ReplacesTheEstimateWithTheWordsOnceBothAreDone() =>
        _after.Hits[0].WordIndex.ShouldBe(0);

    [Fact]
    public void EndsUpWithTheBatchHits() => _after.Hits.ShouldBe(Script.BatchHits(BadWords));
}

public class WhenEveryWindowIsFinishedInOrder
{
    private readonly HitSnapshot _snapshot;

    public WhenEveryWindowIsFinishedInOrder()
    {
        _snapshot = HundredSecondVideo.Finish(0, 1, 2, 3, 4).Snapshot;
    }

    [Fact]
    public void HasTheSameHitsAsTheBatchRules() =>
        _snapshot.Hits.ShouldBe(HundredSecondVideo.Script.BatchHits(HundredSecondVideo.BadWords));

    [Fact]
    public void HasTheSameWordsAsTheBatchStitch() =>
        _snapshot.Transcript.Words.ShouldBe(HundredSecondVideo.Script.Batch().Words);

    [Fact]
    public void HasTheSameSegmentsAsTheBatchStitch() =>
        _snapshot.Transcript.Segments.ShouldBe(HundredSecondVideo.Script.Batch().Segments);

    [Fact]
    public void HasTheHitsWorkedOutByHand() =>
        _snapshot.Hits.ShouldBe([
            HundredSecondVideo.Hit("damn", 3.25, 4.05, 1),
            HundredSecondVideo.Hit("go to hell", 23.85, 25.65, 3),
            HundredSecondVideo.Hit("shit", 46.95, 47.75, 8),
            HundredSecondVideo.Hit("damn+damn", 68.35, 69.65, 9),
            HundredSecondVideo.Hit("crap", 89.85, 90.65, 11),
        ]);

    [Fact]
    public void IsComplete() => _snapshot.IsComplete.ShouldBeTrue();

    [Fact]
    public void CoversTheWholeFile() =>
        _snapshot.Coverage.Intervals.ShouldBe([new CoverageInterval(0.0, 100.0)]);

    [Fact]
    public void HasFinishedEveryWindow() => _snapshot.FinishedWindowCount.ShouldBe(5);

    [Fact]
    public void IsAtOneRevisionPerWindow() => _snapshot.Revision.ShouldBe(5);
}

public class WhenEveryWindowIsFinishedOutOfOrder
{
    private readonly HitSnapshot _inOrder;
    private readonly HitSnapshot _outOfOrder;
    private readonly WatchProgress _progress;

    public WhenEveryWindowIsFinishedOutOfOrder()
    {
        _inOrder = HundredSecondVideo.Finish(0, 1, 2, 3, 4).Snapshot;
        _progress = HundredSecondVideo.Finish(3, 1, 4, 0, 2);
        _outOfOrder = _progress.Snapshot;
    }

    [Fact]
    public void HasTheSameHits() => _outOfOrder.Hits.ShouldBe(_inOrder.Hits);

    [Fact]
    public void HasTheSameWords() =>
        _outOfOrder.Transcript.Words.ShouldBe(_inOrder.Transcript.Words);

    [Fact]
    public void HasTheSameSegments() =>
        _outOfOrder.Transcript.Segments.ShouldBe(_inOrder.Transcript.Segments);

    [Fact]
    public void HasTheSameCoverage() =>
        _outOfOrder.Coverage.Intervals.ShouldBe(_inOrder.Coverage.Intervals);

    [Fact]
    public void HasTheSameRevision() => _outOfOrder.Revision.ShouldBe(_inOrder.Revision);

    [Fact]
    public void ListsTheFinishedWindowsInIndexOrder() =>
        _progress.FinishedWindows.ShouldBe([0, 1, 2, 3, 4]);
}

public class WhenWindowsFinishInEveryOrder
{
    [Fact]
    public void AlwaysEndsWithTheBatchHits()
    {
        var expected = HundredSecondVideo.Script.BatchHits(HundredSecondVideo.BadWords);
        var random = new Random(20260918);

        for (var trial = 0; trial < 50; trial++)
        {
            var order = HundredSecondVideo.Script.AllWindows;
            random.Shuffle(order);

            HundredSecondVideo
                .Finish(order)
                .Snapshot.Hits.ShouldBe(expected, $"order {string.Join(",", order)}");
        }
    }
}

public class WhenPaddedHitsInNeighbouringWindowsOverlap
{
    private readonly HitSnapshot _one;
    private readonly HitSnapshot _both;

    public WhenPaddedHitsInNeighbouringWindowsOverlap()
    {
        _one = HundredSecondVideo.Finish(2).Snapshot;
        _both = HundredSecondVideo.Finish(2, 3).Snapshot;
    }

    [Fact]
    public void ReportsOnlyTheFirstWhileItsNeighbourIsUnfinished() =>
        _one.Hits.ShouldBe([
            HundredSecondVideo.Hit("shit", 46.95, 47.75, 0),
            HundredSecondVideo.Hit("damn", 68.35, 69.05, 1),
        ]);

    [Fact]
    public void MergesThemOnceBothAreFinished() =>
        _both.Hits.ShouldBe([
            HundredSecondVideo.Hit("shit", 46.95, 47.75, 0),
            HundredSecondVideo.Hit("damn+damn", 68.35, 69.65, 1),
        ]);
}

/// <summary>
/// Windows 0 and 2 are done, window 1 is not. Their words sit side by side in
/// the transcript so far, with 25 seconds nobody has heard between them.
/// </summary>
public class WhenAPhraseWouldSpanAnUnfinishedWindow
{
    private static readonly Script Script = new(
        100.0,
        Utterance.Of(Utterance.W("go", 23.0, 23.3), Utterance.W("to", 23.4, 23.8)),
        Utterance.Of(Utterance.W("hell", 48.0, 48.4), Utterance.W("shit", 50.0, 50.4))
    );

    private static readonly BadWordsList BadWords = BadWordsList.FromLines(["go to hell", "shit"]);

    private readonly HitSnapshot _snapshot;

    public WhenAPhraseWouldSpanAnUnfinishedWindow()
    {
        _snapshot = Script.Finish(BadWords, 0, 2).Snapshot;

        // The precondition: the words really are neighbours in the transcript.
        _snapshot.Transcript.Words.Select(w => w.Text).ShouldBe(["go", "to", "hell", "shit"]);
    }

    [Fact]
    public void DoesNotMatchAcrossTheGap() =>
        _snapshot.Hits.ShouldNotContain(h => h.Phrase.Contains("go to hell"));

    [Fact]
    public void StillMatchesInsideTheLaterRun() =>
        _snapshot.Hits.Select(h => h.Phrase).ShouldBe(["shit"]);

    [Fact]
    public void PointsTheLaterRunsHitAtItsWordInTheWholeTranscript() =>
        _snapshot.Hits[0].WordIndex.ShouldBe(3);
}

public class WhenTheSameWindowIsFinishedTwice
{
    private readonly WatchProgress _once;
    private readonly Exception _thrown;

    public WhenTheSameWindowIsFinishedTwice()
    {
        _once = HundredSecondVideo.Finish(2);
        _thrown = Should.Throw<InvalidOperationException>(() =>
            _once.With(2, HundredSecondVideo.Script.Heard(2))
        );
    }

    [Fact]
    public void SaysWhichWindow() => _thrown.Message.ShouldContain("2");

    [Fact]
    public void LeavesTheRevisionAlone() => _once.Revision.ShouldBe(1);

    [Fact]
    public void KeepsTheFirstResult() => _once.Snapshot.Hits.Count.ShouldBe(2);

    [Fact]
    public void RefusesEvenAnEmptyResult() =>
        Should.Throw<InvalidOperationException>(() =>
            _once.With(2, new TranscriptionResult([], []))
        );
}

public class WhenAWindowIsFinished
{
    private readonly WatchProgress _before;
    private readonly WatchProgress _after;

    public WhenAWindowIsFinished()
    {
        _before = HundredSecondVideo.Finish(0);
        _after = _before.With(3, HundredSecondVideo.Script.Heard(3));
    }

    [Fact]
    public void ReturnsANewInstance() => _after.ShouldNotBeSameAs(_before);

    [Fact]
    public void LeavesTheOriginalsFinishedWindowsAlone() => _before.FinishedWindows.ShouldBe([0]);

    [Fact]
    public void LeavesTheOriginalsRevisionAlone() => _before.Revision.ShouldBe(1);

    [Fact]
    public void LeavesTheOriginalsSnapshotAlone() =>
        _before.Snapshot.FinishedWindowCount.ShouldBe(1);

    [Fact]
    public void AddsTheWindow() => _after.FinishedWindows.ShouldBe([0, 3]);

    [Fact]
    public void ReportsTheWindowAsFinished() => _after.IsFinished(3).ShouldBeTrue();

    [Fact]
    public void RaisesTheRevision() => _after.Revision.ShouldBe(2);

    [Fact]
    public void GivesTheSnapshotTheRevision() => _after.Snapshot.Revision.ShouldBe(2);

    [Fact]
    public void KeepsTheBadWordsList() => _after.BadWords.ShouldBeSameAs(_before.BadWords);

    [Fact]
    public void BuildsTheSnapshotOnce() => _after.Snapshot.ShouldBeSameAs(_after.Snapshot);
}

public class WhenAWindowHeardNothing
{
    private readonly WatchProgress _progress;

    public WhenAWindowHeardNothing()
    {
        _progress = HundredSecondVideo.Finish().With(1, new TranscriptionResult([], []));
    }

    [Fact]
    public void CountsItAsFinished() => _progress.Snapshot.FinishedWindowCount.ShouldBe(1);

    [Fact]
    public void CoversItsShare() =>
        _progress.Snapshot.Coverage.Intervals.ShouldBe([new CoverageInterval(26.0, 46.0)]);

    [Fact]
    public void HasNoHits() => _progress.Snapshot.Hits.ShouldBeEmpty();

    [Fact]
    public void RaisesTheRevision() => _progress.Revision.ShouldBe(1);
}

public class WhenTheBadWordsListChanges
{
    private readonly WatchProgress _before;
    private readonly WatchProgress _after;

    public WhenTheBadWordsListChanges()
    {
        _before = HundredSecondVideo.Finish(0, 1);
        _after = _before.WithBadWords(BadWordsList.FromLines(["yes"]));
    }

    [Fact]
    public void RaisesTheRevision() => _after.Revision.ShouldBe(3);

    [Fact]
    public void RebuildsTheHitsFromTheSameTranscript() =>
        _after.Snapshot.Hits.ShouldBe([HundredSecondVideo.Hit("yes", 25.3, 25.85, 6)]);

    [Fact]
    public void KeepsTheFinishedWindows() => _after.FinishedWindows.ShouldBe([0, 1]);

    [Fact]
    public void LeavesTheOriginalAlone() => _before.Snapshot.Hits.Count.ShouldBe(2);
}

public class WhenTheBadWordsListIsReplacedWithTheSamePhrases
{
    private readonly WatchProgress _before;
    private readonly WatchProgress _after;

    public WhenTheBadWordsListIsReplacedWithTheSamePhrases()
    {
        _before = HundredSecondVideo.Finish(0);
        _after = _before.WithBadWords(
            BadWordsList.FromLines(["# reread from disk", "CRAP", "shit", "Go to hell", "damn"])
        );
    }

    [Fact]
    public void ReturnsTheSameInstance() => _after.ShouldBeSameAs(_before);

    [Fact]
    public void LeavesTheRevisionAlone() => _after.Revision.ShouldBe(1);
}

public class WhenTheRevisionIsFollowedThroughASession
{
    private readonly IReadOnlyList<long> _revisions;

    public WhenTheRevisionIsFollowedThroughASession()
    {
        var script = HundredSecondVideo.Script;
        var progress = script.Start(HundredSecondVideo.BadWords);
        var revisions = new List<long> { progress.Revision };

        foreach (var index in new[] { 2, 0 })
        {
            progress = progress.With(index, script.Heard(index));
            revisions.Add(progress.Revision);
        }

        progress = progress.WithBadWords(BadWordsList.FromLines(["yes"]));
        revisions.Add(progress.Revision);

        progress = progress.WithBadWords(BadWordsList.FromLines(["yes"]));
        revisions.Add(progress.Revision);

        foreach (var index in new[] { 4, 1, 3 })
        {
            progress = progress.With(index, script.Heard(index));
            revisions.Add(progress.Revision);
        }

        _revisions = revisions;
    }

    [Fact]
    public void RisesByOneForEveryChange() => _revisions.ShouldBe([0, 1, 2, 3, 3, 4, 5, 6]);
}

public class WhenStartingFromAPlan
{
    private readonly WatchProgress _progress;

    public WhenStartingFromAPlan()
    {
        _progress = WatchProgress.Start(
            [new TranscriptionWindow(0.0, 10.0, double.NegativeInfinity, double.PositiveInfinity)],
            10.0,
            HundredSecondVideo.BadWords
        );
    }

    [Fact]
    public void UsesThePlanGiven() => _progress.Snapshot.TotalWindows.ShouldBe(1);

    [Fact]
    public void KeepsTheDuration() => _progress.DurationSeconds.ShouldBe(10.0);

    [Fact]
    public void CompletesWithItsOneWindow() =>
        _progress.With(0, new TranscriptionResult([], [])).Snapshot.IsComplete.ShouldBeTrue();
}

public class WhenAZeroLengthVideoIsWatched
{
    private readonly WatchProgress _progress = WatchProgress.Start(
        0.0,
        HundredSecondVideo.BadWords
    );

    [Fact]
    public void HasOneWindow() => _progress.Snapshot.TotalWindows.ShouldBe(1);

    [Fact]
    public void IsCompleteOnceThatWindowIsDone() =>
        _progress.With(0, new TranscriptionResult([], [])).Snapshot.IsComplete.ShouldBeTrue();
}

public class WhenWatchProgressIsGivenBadArguments
{
    private readonly WatchProgress _progress = HundredSecondVideo.Finish();

    [Fact]
    public void RefusesANegativeIndex() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            _progress.With(-1, new TranscriptionResult([], []))
        );

    [Fact]
    public void RefusesAnIndexPastThePlan() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            _progress.With(5, new TranscriptionResult([], []))
        );

    [Fact]
    public void RefusesANullResult() =>
        Should.Throw<ArgumentNullException>(() => _progress.With(0, null!));

    [Fact]
    public void RefusesANullBadWordsListOnChange() =>
        Should.Throw<ArgumentNullException>(() => _progress.WithBadWords(null!));

    [Fact]
    public void RefusesANullBadWordsListOnStart() =>
        Should.Throw<ArgumentNullException>(() => WatchProgress.Start(100.0, null!));

    [Fact]
    public void RefusesANegativeDuration() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            WatchProgress.Start(-1.0, HundredSecondVideo.BadWords)
        );

    [Fact]
    public void RefusesAnInfiniteDuration() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            WatchProgress.Start(double.PositiveInfinity, HundredSecondVideo.BadWords)
        );

    [Fact]
    public void RefusesANaNDuration() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            WatchProgress.Start(double.NaN, HundredSecondVideo.BadWords)
        );

    [Fact]
    public void RefusesANullPlan() =>
        Should.Throw<ArgumentNullException>(() =>
            WatchProgress.Start(null!, 100.0, HundredSecondVideo.BadWords)
        );

    [Fact]
    public void RefusesAnEmptyPlan() =>
        Should.Throw<ArgumentException>(() =>
            WatchProgress.Start([], 100.0, HundredSecondVideo.BadWords)
        );

    [Fact]
    public void RefusesAPlanThatDoesNotCoverTheFile() =>
        Should.Throw<ArgumentException>(() =>
            WatchProgress.Start(
                [new TranscriptionWindow(0.0, 10.0, double.NegativeInfinity, 10.0)],
                100.0,
                HundredSecondVideo.BadWords
            )
        );

    [Fact]
    public void AnswersIsFinishedOnlyForIndicesInThePlan() =>
        Should.Throw<ArgumentOutOfRangeException>(() => _progress.IsFinished(5));
}
