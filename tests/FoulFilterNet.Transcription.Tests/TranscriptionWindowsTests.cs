using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

public class WhenPlanningWindowsForAFileShorterThanOneWindow
{
    private readonly IReadOnlyList<TranscriptionWindow> _windows = TranscriptionWindows.Plan(8.0);

    [Fact]
    public void UsesOneWindow() => _windows.Count.ShouldBe(1);

    [Fact]
    public void CoversTheWholeFile() =>
        _windows[0]
            .ShouldBe(
                new TranscriptionWindow(0.0, 8.0, double.NegativeInfinity, double.PositiveInfinity)
            );
}

/// <summary>
/// Whisper.net stops after 30 seconds with DTW on, so nothing longer than one
/// window may reach it.
/// </summary>
public class WhenPlanningWindowsForALongFile
{
    private readonly IReadOnlyList<TranscriptionWindow> _windows = TranscriptionWindows.Plan(
        3140.5
    );

    [Fact]
    public void KeepsEveryWindowWithinOneWhisperInput() =>
        _windows.ShouldAllBe(w => w.End - w.Start <= TranscriptionWindows.LengthSeconds);

    [Fact]
    public void StartsAtTheBeginning() => _windows[0].Start.ShouldBe(0.0);

    [Fact]
    public void EndsWithTheFile() => _windows[^1].End.ShouldBe(3140.5);

    [Fact]
    public void GivesTheLastWindowAFullLength() =>
        (_windows[^1].End - _windows[^1].Start).ShouldBe(TranscriptionWindows.LengthSeconds);

    [Fact]
    public void OverlapsEveryNeighbourByAtLeastTheOverlap() =>
        _windows
            .Zip(_windows.Skip(1))
            .ShouldAllBe(p => p.First.End - p.Second.Start >= TranscriptionWindows.OverlapSeconds);

    [Fact]
    public void HandsEachInstantToExactlyOneWindow() =>
        _windows.Zip(_windows.Skip(1)).ShouldAllBe(p => p.First.KeepTo == p.Second.KeepFrom);

    [Fact]
    public void MeetsInTheMiddleOfEachOverlap() =>
        _windows[0].KeepTo.ShouldBe((_windows[0].End + _windows[1].Start) / 2);

    [Fact]
    public void KeepsEverythingBeforeTheFirstShare() =>
        _windows[0].KeepFrom.ShouldBe(double.NegativeInfinity);

    [Fact]
    public void KeepsEverythingAfterTheLastShare() =>
        _windows[^1].KeepTo.ShouldBe(double.PositiveInfinity);
}

public class WhenStitchingWhatOverlappingWindowsHeard
{
    private readonly TranscriptionResult _stitched;

    public WhenStitchingWhatOverlappingWindowsHeard()
    {
        var windows = TranscriptionWindows.Plan(50.0);
        windows.Count.ShouldBe(2);

        // Window two starts at 22 s; their shares meet at 25 s.
        _stitched = TranscriptionWindows.Stitch([
            (
                windows[0],
                new TranscriptionResult(
                    [new Segment(20.0, 24.0, "well damn"), new Segment(24.5, 28.0, "that we")],
                    [
                        new Word("damn", 23.0, 23.5),
                        new Word("that", 24.5, 24.8),
                        new Word("we", 27.6, 28.0),
                    ]
                )
            ),
            (
                windows[1],
                new TranscriptionResult(
                    [new Segment(0.0, 1.0, "damn"), new Segment(2.5, 6.0, "that went badly")],
                    [
                        new Word("mn", 0.0, 0.4),
                        new Word("that", 2.5, 2.8),
                        new Word("went", 5.6, 6.0),
                    ]
                )
            ),
        ]);
    }

    [Fact]
    public void RebasesTheLaterWindowOntoTheFilesTimeline() =>
        _stitched.Words.ShouldContain(new Word("went", 27.6, 28.0));

    [Fact]
    public void KeepsEachWordOnce() =>
        _stitched.Words.Select(w => w.Text).ShouldBe(["damn", "that", "went"]);

    [Fact]
    public void DropsTheWordACutGarbled() =>
        _stitched.Words.ShouldNotContain(w => w.Text == "we" || w.Text == "mn");

    [Fact]
    public void KeepsEachSegmentOnce() =>
        _stitched.Segments.Select(s => s.Text).ShouldBe(["well damn", "that went badly"]);
}
