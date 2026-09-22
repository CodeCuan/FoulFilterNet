using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

public class WhenTheSubWindowLayoutIsLeftAtItsDefault
{
    private readonly PrioritySubWindows _layout = PrioritySubWindows.Default;

    [Fact]
    public void HearsFiveSecondsAtATime() => _layout.LengthSeconds.ShouldBe(5.0);

    [Fact]
    public void StepsByHalfASubWindow() => _layout.StepSeconds.ShouldBe(2.5);

    [Fact]
    public void IsTheMeasuredLayoutOfAdr0008() =>
        _layout.ShouldBe(
            new PrioritySubWindows(
                PrioritySubWindows.DefaultLengthSeconds,
                PrioritySubWindows.DefaultStepSeconds
            )
        );
}

public class WhenTheSubWindowLayoutIsChanged
{
    private readonly PrioritySubWindows _layout = new(4.0, 2.0);

    private readonly IReadOnlyList<TranscriptionWindow> _subWindows = new PrioritySubWindows(
        4.0,
        2.0
    ).Plan(8.0);

    [Fact]
    public void PlansSubWindowsOfTheConfiguredLength() =>
        _subWindows.ShouldAllBe(w => w.End - w.Start == 4.0);

    [Fact]
    public void PlansSubWindowsAtTheConfiguredStep() =>
        _subWindows.Select(w => w.Start).ShouldBe([0.0, 2.0, 4.0]);

    [Fact]
    public void PlansMoreOfThemThanTheDefaultLayoutDoes() =>
        _layout.Plan(20.0).Count.ShouldBeGreaterThan(PrioritySubWindows.Default.Plan(20.0).Count);

    [Fact]
    public void KeepsTheLengthItWasGiven() => _layout.LengthSeconds.ShouldBe(4.0);

    [Fact]
    public void KeepsTheStepItWasGiven() => _layout.StepSeconds.ShouldBe(2.0);

    [Fact]
    public void LeavesOutSubWindowsThatCannotContributeToTheWindowsShare() =>
        _layout.Plan(8.0, keepFrom: 0.0, keepTo: 3.0).Count.ShouldBe(2);
}

/// <summary>
/// A layout with no overlap - step as long as the sub-window - is legal but
/// hears every instant once, which is what the default layout exists to avoid.
/// </summary>
public class WhenTheSubWindowLayoutDoesNotOverlap
{
    private readonly IReadOnlyList<TranscriptionWindow> _subWindows = new PrioritySubWindows(
        5.0,
        5.0
    ).Plan(10.0);

    [Fact]
    public void PlansTouchingSubWindows() => _subWindows.Select(w => w.Start).ShouldBe([0.0, 5.0]);

    [Fact]
    public void HandsEachInstantToExactlyOneSubWindow() =>
        _subWindows.Zip(_subWindows.Skip(1)).ShouldAllBe(p => p.First.KeepTo == p.Second.KeepFrom);
}

public class WhenASubWindowLayoutIsNonsense
{
    [Fact]
    public void RejectsAZeroLength() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new PrioritySubWindows(0.0, 2.5));

    [Fact]
    public void RejectsANegativeLength() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new PrioritySubWindows(-5.0, 2.5));

    [Fact]
    public void RejectsANonFiniteLength() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new PrioritySubWindows(double.PositiveInfinity, 2.5)
        );

    [Fact]
    public void RejectsAZeroStep() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new PrioritySubWindows(5.0, 0.0));

    [Fact]
    public void RejectsANegativeStep() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new PrioritySubWindows(5.0, -2.5));

    [Fact]
    public void RejectsANotANumberStep() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new PrioritySubWindows(5.0, double.NaN));

    [Fact]
    public void RejectsAStepLongerThanTheSubWindow() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new PrioritySubWindows(5.0, 5.1));

    [Fact]
    public void SaysWhichValueIsWrong() =>
        Should
            .Throw<ArgumentOutOfRangeException>(() => new PrioritySubWindows(5.0, 5.1))
            .ParamName.ShouldBe("stepSeconds");
}
