using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

public class WhenTheRescanOffsetIsNotSpecified
{
    private readonly double _offset;

    public WhenTheRescanOffsetIsNotSpecified()
    {
        _offset = RescanPass.DefaultOffsetSeconds;

        _offset.ShouldBePositive();
    }

    [Fact]
    public void PadsTheStartByFourSeconds() => _offset.ShouldBe(4.0);
}

/// <summary>
/// The Rescan Pass transcribes audio padded with <c>offset</c> seconds of
/// silence, so every timestamp it returns sits <c>offset</c> seconds later than
/// the original timeline. Shifting rebases them.
/// </summary>
public class WhenShiftingRescanSegmentsBackOntoTheOriginalTimeline
{
    private readonly IReadOnlyList<Segment> _shifted;

    public WhenShiftingRescanSegmentsBackOntoTheOriginalTimeline()
    {
        _shifted = RescanPass.Shift(
            [
                new Segment(7.4105, 9.8977, "well damn"),
                new Segment(11.0, 12.5, "that went badly"),
            ],
            RescanPass.DefaultOffsetSeconds);

        _shifted.Count.ShouldBe(2);
    }

    [Fact]
    public void SubtractsTheOffsetFromTheStart() => _shifted[0].Start.ShouldBe(3.411);

    [Fact]
    public void SubtractsTheOffsetFromTheEnd() => _shifted[0].End.ShouldBe(5.898);

    [Fact]
    public void RoundsToThreeDecimalPlaces() =>
        _shifted[0].Start.ShouldBe(Times.Round(7.4105 - 4.0));

    [Fact]
    public void LeavesTheTextAlone() => _shifted[0].Text.ShouldBe("well damn");

    [Fact]
    public void ShiftsEverySegment() => _shifted[1].ShouldBe(new Segment(7.0, 8.5, "that went badly"));
}

public class WhenAShiftedSegmentFallsInsideThePadding
{
    private readonly IReadOnlyList<Segment> _shifted;

    public WhenAShiftedSegmentFallsInsideThePadding()
    {
        _shifted = RescanPass.Shift(
            [
                new Segment(0.5, 3.2, "silence artefact"),
                new Segment(2.0, 4.0, "ends exactly at the offset"),
                new Segment(5.0, 6.0, "real speech"),
            ],
            4.0);

        _shifted.ShouldNotBeEmpty();
    }

    [Fact]
    public void DropsASegmentThatEndsBeforeZero() =>
        _shifted.ShouldNotContain(s => s.Text == "silence artefact");

    [Fact]
    public void DropsASegmentThatEndsExactlyAtZero() =>
        _shifted.ShouldNotContain(s => s.Text == "ends exactly at the offset");

    [Fact]
    public void KeepsOnlyTheSurvivingSegment() =>
        _shifted.ShouldBe([new Segment(1.0, 2.0, "real speech")]);
}

public class WhenAShiftedSegmentStraddlesZero
{
    private readonly Segment _shifted;

    public WhenAShiftedSegmentStraddlesZero()
    {
        var result = RescanPass.Shift([new Segment(3.6, 4.9, "half in the padding")], 4.0);

        result.Count.ShouldBe(1);
        _shifted = result[0];
    }

    [Fact]
    public void IsKeptRatherThanDropped() => _shifted.Text.ShouldBe("half in the padding");

    [Fact]
    public void ClampsTheStartToZero() => _shifted.Start.ShouldBe(0.0);

    [Fact]
    public void KeepsTheShiftedEnd() => _shifted.End.ShouldBe(0.9);
}

public class WhenShiftingRescanWords
{
    private readonly IReadOnlyList<Word> _shifted;

    public WhenShiftingRescanWords()
    {
        _shifted = RescanPass.Shift(
            [
                new Word("intro", 1.0, 2.0),
                new Word("damn", 7.4105, 7.8971),
                new Word("straddling", 3.5, 4.25),
            ],
            4.0);

        _shifted.ShouldNotBeEmpty();
    }

    [Fact]
    public void DropsAWordThatEndsInsideThePadding() =>
        _shifted.ShouldNotContain(w => w.Text == "intro");

    [Fact]
    public void RebasesAndRoundsTheSurvivingWord() =>
        _shifted.ShouldContain(new Word("damn", 3.411, 3.897));

    [Fact]
    public void ClampsAStraddlingWordToZero() =>
        _shifted.ShouldContain(new Word("straddling", 0.0, 0.25));
}

public class WhenShiftingAWholeRescanResult
{
    private readonly TranscriptionResult _shifted;

    public WhenShiftingAWholeRescanResult()
    {
        _shifted = RescanPass.Shift(
            new TranscriptionResult(
                [new Segment(4.5, 6.0, "well damn")],
                [new Word("damn", 5.0, 5.4)]),
            4.0);

        _shifted.ShouldNotBeNull();
    }

    [Fact]
    public void RebasesTheSegments() =>
        _shifted.Segments.ShouldBe([new Segment(0.5, 2.0, "well damn")]);

    [Fact]
    public void RebasesTheWords() =>
        _shifted.Words.ShouldBe([new Word("damn", 1.0, 1.4)]);

    [Fact]
    public void StillReportsWordTimestamps() => _shifted.HasWordTimestamps.ShouldBeTrue();
}

public class WhenUnioningARescanIntoThePrimaryTranscript
{
    private readonly IReadOnlyList<Segment> _union;

    public WhenUnioningARescanIntoThePrimaryTranscript()
    {
        _union = RescanPass.Union(
            [
                new Segment(0.0, 2.0, "hello there"),
                new Segment(2.5, 4.0, "damn it"),
            ],
            [
                new Segment(2.4, 4.1, "damn it"),
                new Segment(5.0, 6.0, "recovered at the boundary"),
            ]);

        _union.ShouldNotBeEmpty();
    }

    [Fact]
    public void DropsTheDuplicatedOverlappingSpan() => _union.Count.ShouldBe(3);

    [Fact]
    public void SortsByStartTime() =>
        _union.Select(s => s.Start).ShouldBe([0.0, 2.4, 5.0]);

    [Fact]
    public void KeepsTheEarlierOfTwoIdenticalSpans() =>
        _union[1].ShouldBe(new Segment(2.4, 4.1, "damn it"));

    [Fact]
    public void KeepsSegmentsTheFirstPassMissed() =>
        _union.ShouldContain(new Segment(5.0, 6.0, "recovered at the boundary"));
}

public class WhenUnioningSegmentsThatOnlyLookLikeDuplicates
{
    private readonly IReadOnlyList<Segment> _union;

    public WhenUnioningSegmentsThatOnlyLookLikeDuplicates()
    {
        _union = RescanPass.Union(
            [new Segment(0.0, 1.0, "damn")],
            [new Segment(5.0, 6.0, "damn")]);

        _union.ShouldNotBeEmpty();
    }

    [Fact]
    public void KeepsRepeatedTextThatDoesNotOverlap() => _union.Count.ShouldBe(2);

    [Fact]
    public void DropsIdenticalTextAtAnIdenticalSpan() =>
        RescanPass.Union([new Segment(1.0, 2.0, "damn")], [new Segment(1.0, 2.0, "damn")]).Count.ShouldBe(1);

    [Fact]
    public void KeepsOverlappingSpansWithDifferentText() =>
        RescanPass.Union([new Segment(1.0, 3.0, "damn it")], [new Segment(2.0, 4.0, "damn")]).Count.ShouldBe(2);

    [Fact]
    public void ReturnsThePrimaryUnchangedWhenTheRescanFoundNothing() =>
        RescanPass.Union([new Segment(1.0, 2.0, "damn")], []).ShouldBe([new Segment(1.0, 2.0, "damn")]);
}
