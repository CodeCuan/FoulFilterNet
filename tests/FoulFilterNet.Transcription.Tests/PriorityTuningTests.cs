using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;

namespace FoulFilterNet.Transcription.Tests;

public class WhenThePriorityTuningIsLeftAtItsDefaults
{
    private readonly PriorityTuning _tuning = PriorityTuning.ForOptions(new TranscriptionOptions());

    [Fact]
    public void PadsAPriorityHitByAQuarterSecondBefore() => _tuning.Padding.Pre.ShouldBe(0.25);

    [Fact]
    public void PadsAPriorityHitByHalfASecondAfter() => _tuning.Padding.Post.ShouldBe(0.5);

    [Fact]
    public void TakesAPriorityHitToBeAtLeastFourFifthsOfASecond() =>
        _tuning.MinimumCutSeconds.ShouldBe(0.8);

    [Fact]
    public void HearsSubWindowsInTheMeasuredLayout() =>
        _tuning.SubWindows.ShouldBe(PrioritySubWindows.Default);

    [Fact]
    public void IsTheShippedTuning() => _tuning.ShouldBe(PriorityTuning.Default);

    [Fact]
    public void BuildsTheSameCutWindowsTheDomainDefaultsDo() =>
        _tuning
            .PaddingFor(PriorityWordList.Default)
            .Widen("fuck", 10.0, 10.01)
            .ShouldBe(
                CutPadding.ForPriorityWords(PriorityWordList.Default).Widen("fuck", 10.0, 10.01)
            );
}

public class WhenThePriorityTuningIsConfigured
{
    private readonly PriorityTuning _tuning = PriorityTuning.ForOptions(
        new TranscriptionOptions
        {
            PriorityPaddingPre = 0.4,
            PriorityPaddingPost = 0.9,
            PriorityMinimumCutSeconds = 1.2,
            PrioritySubWindowSeconds = 4.0,
            PrioritySubWindowStepSeconds = 1.0,
        }
    );

    [Fact]
    public void TakesTheConfiguredPreRoll() => _tuning.Padding.Pre.ShouldBe(0.4);

    [Fact]
    public void TakesTheConfiguredPostRoll() => _tuning.Padding.Post.ShouldBe(0.9);

    [Fact]
    public void TakesTheConfiguredMinimumCut() => _tuning.MinimumCutSeconds.ShouldBe(1.2);

    [Fact]
    public void TakesTheConfiguredSubWindowLength() =>
        _tuning.SubWindows.LengthSeconds.ShouldBe(4.0);

    [Fact]
    public void TakesTheConfiguredSubWindowStep() => _tuning.SubWindows.StepSeconds.ShouldBe(1.0);

    [Fact]
    public void CutsAPriorityWordTheConfiguredWayRoundIt() =>
        _tuning
            .PaddingFor(PriorityWordList.Default)
            .Widen("fuck", 10.0, 10.01)
            .ShouldBe((10.01 - 1.2 - 0.4, 10.01 + 0.9));

    [Fact]
    public void LeavesOrdinaryWordsOnTheOrdinaryPadding() =>
        _tuning
            .PaddingFor(PriorityWordList.Default)
            .Widen("damn", 10.0, 10.4)
            .ShouldBe((10.0 - HitPadding.Default.Pre, 10.4 + HitPadding.Default.Post));

    [Fact]
    public void ReachesFurtherBackForAShortPriorityHit() =>
        _tuning.PaddingFor(PriorityWordList.Default).ShortHitReachSeconds.ShouldBe(1.6);

    [Fact]
    public void PadsNothingWiderWithAnEmptyPriorityWordList() =>
        _tuning.PaddingFor(PriorityWordList.FromLines([])).ShouldBeSameAs(CutPadding.Default);
}

/// <summary>
/// A value left out of configuration binds as zero, and so does one written as
/// <c>0</c>. Both mean "the shipped default" rather than "no padding at all":
/// the pass is turned off with <c>Transcription:PriorityPass</c>, not by zeroing
/// its tuning.
/// </summary>
public class WhenThePriorityTuningIsZeroed
{
    private readonly PriorityTuning _tuning = PriorityTuning.ForOptions(
        new TranscriptionOptions
        {
            PriorityPaddingPre = 0.0,
            PriorityPaddingPost = 0.0,
            PriorityMinimumCutSeconds = 0.0,
            PrioritySubWindowSeconds = 0.0,
            PrioritySubWindowStepSeconds = 0.0,
        }
    );

    [Fact]
    public void FallsBackToTheShippedTuning() => _tuning.ShouldBe(PriorityTuning.Default);

    [Fact]
    public void FallsBackPerValue() =>
        PriorityTuning
            .ForOptions(new TranscriptionOptions { PriorityPaddingPost = 0.0 })
            .Padding.Post.ShouldBe(0.5);

    [Fact]
    public void KeepsTheOtherValuesThatWereGiven() =>
        PriorityTuning
            .ForOptions(
                new TranscriptionOptions { PriorityPaddingPre = 0.0, PriorityPaddingPost = 0.9 }
            )
            .Padding.ShouldBe(new HitPadding(0.25, 0.9));
}

public class WhenThePriorityTuningIsNonsense
{
    private static string Message(TranscriptionOptions options) =>
        Should.Throw<InvalidOperationException>(() => PriorityTuning.ForOptions(options)).Message;

    [Fact]
    public void RefusesANegativePreRoll() =>
        Message(new TranscriptionOptions { PriorityPaddingPre = -0.1 })
            .ShouldContain("Transcription:PriorityPaddingPre");

    [Fact]
    public void RefusesANegativePostRoll() =>
        Message(new TranscriptionOptions { PriorityPaddingPost = -0.1 })
            .ShouldContain("Transcription:PriorityPaddingPost");

    [Fact]
    public void RefusesANegativeMinimumCut() =>
        Message(new TranscriptionOptions { PriorityMinimumCutSeconds = -1.0 })
            .ShouldContain("Transcription:PriorityMinimumCutSeconds");

    [Fact]
    public void RefusesANegativeSubWindowLength() =>
        Message(new TranscriptionOptions { PrioritySubWindowSeconds = -5.0 })
            .ShouldContain("Transcription:PrioritySubWindowSeconds");

    [Fact]
    public void RefusesANegativeSubWindowStep() =>
        Message(new TranscriptionOptions { PrioritySubWindowStepSeconds = -2.5 })
            .ShouldContain("Transcription:PrioritySubWindowStepSeconds");

    [Fact]
    public void RefusesAnInfinitePostRoll() =>
        Message(new TranscriptionOptions { PriorityPaddingPost = double.PositiveInfinity })
            .ShouldContain("finite");

    [Fact]
    public void RefusesANotANumberMinimumCut() =>
        Message(new TranscriptionOptions { PriorityMinimumCutSeconds = double.NaN })
            .ShouldContain("Transcription:PriorityMinimumCutSeconds");

    [Fact]
    public void RefusesAStepLongerThanTheSubWindow() =>
        Message(
                new TranscriptionOptions
                {
                    PrioritySubWindowSeconds = 5.0,
                    PrioritySubWindowStepSeconds = 6.0,
                }
            )
            .ShouldContain("Transcription:PrioritySubWindowStepSeconds");

    [Fact]
    public void SaysWhatTheOffendingValueWas() =>
        Message(new TranscriptionOptions { PriorityPaddingPre = -0.1 }).ShouldContain("-0.1");

    [Fact]
    public void SaysWhatTheDefaultIs() =>
        Message(new TranscriptionOptions { PriorityPaddingPre = -0.1 }).ShouldContain("0.25");

    [Fact]
    public void ChecksTheStepAgainstTheConfiguredLengthNotTheDefault() =>
        Message(new TranscriptionOptions { PrioritySubWindowSeconds = 2.0 })
            .ShouldContain("Transcription:PrioritySubWindowSeconds");
}
