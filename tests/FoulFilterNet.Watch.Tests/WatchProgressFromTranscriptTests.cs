using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

public class WhenProgressIsBuiltFromAWholeTranscript
{
    private readonly WatchProgress _progress;
    private readonly HitSnapshot _snapshot;

    public WhenProgressIsBuiltFromAWholeTranscript()
    {
        _progress = WatchProgress.FromTranscript(
            HundredSecondVideo.Script.Batch(),
            HundredSecondVideo.BadWords
        );
        _snapshot = _progress.Snapshot;

        _snapshot.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void FindsExactlyTheBatchPipelinesHits() =>
        _snapshot.Hits.ShouldBe(HundredSecondVideo.Script.BatchHits(HundredSecondVideo.BadWords));

    [Fact]
    public void FindsTheSameHitsAsEveryWindowHeardOneByOne() =>
        _snapshot.Hits.ShouldBe(
            HundredSecondVideo.Finish(HundredSecondVideo.Script.AllWindows).Snapshot.Hits
        );

    [Fact]
    public void KeepsEveryWordWhereItIs() =>
        _snapshot.Transcript.Words.ShouldBe(HundredSecondVideo.Script.Batch().Words);

    [Fact]
    public void KeepsEverySegmentWhereItIs() =>
        _snapshot.Transcript.Segments.ShouldBe(HundredSecondVideo.Script.Batch().Segments);

    [Fact]
    public void PlansOneWindow() => _progress.Plan.Count.ShouldBe(1);

    [Fact]
    public void HasThatWindowFinished() => _progress.FinishedWindows.ShouldBe([0]);

    [Fact]
    public void IsAtRevisionOne() => _progress.Revision.ShouldBe(1);

    [Fact]
    public void CountsOneWindowOfOne() =>
        (_snapshot.FinishedWindowCount, _snapshot.TotalWindows).ShouldBe((1, 1));

    [Fact]
    public void EndsWhereTheLastThingHeardEnds() => _progress.DurationSeconds.ShouldBe(90.4);

    [Fact]
    public void CoversFromZeroToThatEnd() =>
        _snapshot.Coverage.Intervals.ShouldBe([new CoverageInterval(0.0, 90.4)]);

    [Fact]
    public void KeepsTheBadWordsList() =>
        _progress.BadWords.ShouldBeSameAs(HundredSecondVideo.BadWords);

    [Fact]
    public void RefusesASecondResultForItsWindow() =>
        Should.Throw<InvalidOperationException>(() =>
            _progress.With(0, HundredSecondVideo.Script.Batch())
        );
}

public class WhenProgressFromATranscriptMeetsANewBadWordsList
{
    private readonly WatchProgress _before;
    private readonly WatchProgress _after;

    public WhenProgressFromATranscriptMeetsANewBadWordsList()
    {
        _before = WatchProgress.FromTranscript(
            HundredSecondVideo.Script.Batch(),
            HundredSecondVideo.BadWords
        );
        _after = _before.WithBadWords(BadWordsList.FromLines(["yes"]));
    }

    [Fact]
    public void RaisesTheRevision() => _after.Revision.ShouldBe(2);

    [Fact]
    public void FindsTheNewPhrase() => _after.Snapshot.Hits.Single().Phrase.ShouldBe("yes");

    [Fact]
    public void StaysComplete() => _after.Snapshot.IsComplete.ShouldBeTrue();

    [Fact]
    public void LeavesTheOriginalsHitsAlone() =>
        _before.Snapshot.Hits.ShouldBe(
            HundredSecondVideo.Script.BatchHits(HundredSecondVideo.BadWords)
        );
}

public class WhenProgressIsBuiltFromAnEmptyTranscript
{
    private readonly WatchProgress _progress;

    public WhenProgressIsBuiltFromAnEmptyTranscript()
    {
        _progress = WatchProgress.FromTranscript(
            new TranscriptionResult([], []),
            HundredSecondVideo.BadWords
        );
    }

    [Fact]
    public void HasNoHits() => _progress.Snapshot.Hits.ShouldBeEmpty();

    [Fact]
    public void HasNoLength() => _progress.DurationSeconds.ShouldBe(0.0);

    [Fact]
    public void IsStillComplete() => _progress.Snapshot.IsComplete.ShouldBeTrue();

    [Fact]
    public void IsAtRevisionOne() => _progress.Revision.ShouldBe(1);
}

public class WhenATranscriptsLastSegmentOutlastsItsLastWord
{
    private readonly WatchProgress _progress;

    public WhenATranscriptsLastSegmentOutlastsItsLastWord()
    {
        _progress = WatchProgress.FromTranscript(
            new TranscriptionResult(
                [new Segment(1.0, 12.5, "damn that was long")],
                [new Word("damn", 1.0, 1.4)]
            ),
            HundredSecondVideo.BadWords
        );
    }

    [Fact]
    public void EndsWhereTheSegmentEnds() => _progress.DurationSeconds.ShouldBe(12.5);

    [Fact]
    public void StillFindsTheWord() => _progress.Snapshot.Hits.Count.ShouldBe(1);
}

public class WhenATranscriptHasSegmentsButNoWords
{
    private readonly WatchProgress _progress;

    public WhenATranscriptHasSegmentsButNoWords()
    {
        _progress = WatchProgress.FromTranscript(
            new TranscriptionResult([new Segment(2.0, 4.0, "oh damn")], []),
            HundredSecondVideo.BadWords
        );
    }

    [Fact]
    public void EndsWhereTheSegmentEnds() => _progress.DurationSeconds.ShouldBe(4.0);

    [Fact]
    public void FallsBackToTheSegmentEstimate() => _progress.Snapshot.Hits.Count.ShouldBe(1);
}

public class WhenProgressIsBuiltFromNothing
{
    [Fact]
    public void RefusesANullTranscript() =>
        Should.Throw<ArgumentNullException>(() =>
            WatchProgress.FromTranscript(null!, HundredSecondVideo.BadWords)
        );

    [Fact]
    public void RefusesANullBadWordsList() =>
        Should.Throw<ArgumentNullException>(() =>
            WatchProgress.FromTranscript(new TranscriptionResult([], []), null!)
        );
}
