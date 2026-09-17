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

public class WhenPlanningWindowsForAnEmptyFile
{
    private readonly IReadOnlyList<TranscriptionWindow> _windows = TranscriptionWindows.Plan(0.0);

    [Fact]
    public void UsesOneWindow() => _windows.Count.ShouldBe(1);

    [Fact]
    public void CoversNothing() =>
        _windows[0]
            .ShouldBe(
                new TranscriptionWindow(0.0, 0.0, double.NegativeInfinity, double.PositiveInfinity)
            );
}

public class WhenPlanningWindowsForANegativeDuration
{
    [Fact]
    public void Refuses() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TranscriptionWindows.Plan(-1.0));
}

public class WhenPlanningWindowsForAFileExactlyOneWindowLong
{
    private readonly IReadOnlyList<TranscriptionWindow> _windows = TranscriptionWindows.Plan(
        TranscriptionWindows.LengthSeconds
    );

    [Fact]
    public void UsesOneWindow() => _windows.Count.ShouldBe(1);
}

/// <summary>
/// 72 s is exactly three windows stepping 22 s: the pulled-back last window
/// must land on the regular grid, not add a duplicate.
/// </summary>
public class WhenPlanningWindowsForAFileThatIsAnExactMultipleOfTheStep
{
    private readonly IReadOnlyList<TranscriptionWindow> _windows = TranscriptionWindows.Plan(72.0);

    [Fact]
    public void StartsEveryStepWithoutADuplicate() =>
        _windows.Select(w => w.Start).ShouldBe([0.0, 22.0, 44.0]);
}

/// <summary>
/// Just past one window, the last window is pulled back almost onto the first.
/// </summary>
public class WhenPlanningWindowsForAFileJustLongerThanOneWindow
{
    private readonly IReadOnlyList<TranscriptionWindow> _windows = TranscriptionWindows.Plan(28.5);

    [Fact]
    public void UsesTwoWindows() => _windows.Count.ShouldBe(2);

    [Fact]
    public void StartsTheLastWindowAfterTheFirst() =>
        _windows[1].Start.ShouldBeGreaterThan(_windows[0].Start);

    [Fact]
    public void KeepsEachShareInsideItsOwnWindow() =>
        _windows.ShouldAllBe(w =>
            (double.IsNegativeInfinity(w.KeepFrom) || w.KeepFrom >= w.Start)
            && (double.IsPositiveInfinity(w.KeepTo) || w.KeepTo <= w.End)
        );
}

/// <summary>
/// Whatever the duration, an instant anywhere in the file belongs to exactly
/// one window's share, and that window can hear it.
/// </summary>
public class WhenAssigningInstantsToWindows
{
    private static readonly double[] Durations =
    [
        0.0,
        8.0,
        28.0,
        28.5,
        49.9,
        50.0,
        50.1,
        72.0,
        100.0,
        3140.5,
    ];

    private readonly List<(double Duration, double Instant, int Owners, bool Heard)> _checks = [];

    public WhenAssigningInstantsToWindows()
    {
        foreach (var duration in Durations)
        {
            var windows = TranscriptionWindows.Plan(duration);
            for (var instant = 0.0; instant <= duration; instant += 0.25)
            {
                var owners = windows
                    .Where(w => instant >= w.KeepFrom && instant < w.KeepTo)
                    .ToList();
                _checks.Add(
                    (
                        duration,
                        instant,
                        owners.Count,
                        owners.Count == 1 && instant >= owners[0].Start && instant <= owners[0].End
                    )
                );
            }
        }
    }

    [Fact]
    public void GivesEveryInstantExactlyOneOwner() => _checks.ShouldAllBe(c => c.Owners == 1);

    [Fact]
    public void GivesEveryInstantToAWindowThatHearsIt() => _checks.ShouldAllBe(c => c.Heard);
}

public class WhenStitchingAWordWhoseMidpointIsExactlyOnAShareBoundary
{
    private readonly TranscriptionResult _stitched;

    public WhenStitchingAWordWhoseMidpointIsExactlyOnAShareBoundary()
    {
        var windows = TranscriptionWindows.Plan(50.0);

        // The shares meet at 25 s; both windows hear a word centred there.
        _stitched = TranscriptionWindows.Stitch([
            (windows[0], new TranscriptionResult([], [new Word("first", 24.5, 25.5)])),
            (windows[1], new TranscriptionResult([], [new Word("second", 2.5, 3.5)])),
        ]);
    }

    [Fact]
    public void KeepsItOnlyFromTheLaterWindow() =>
        _stitched.Words.Select(w => w.Text).ShouldBe(["second"]);
}
