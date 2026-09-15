using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

public class WhenSmartCutAdjustsAHit
{
    private readonly SmartCutDecision _decision;

    public WhenSmartCutAdjustsAHit()
    {
        _decision = SmartCutDecision.Adjust(5.2, 6.2);

        _decision.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsAnAdjustment() => _decision.Outcome.ShouldBe(SmartCutOutcome.Adjust);

    [Fact]
    public void CarriesTheNewWindow()
    {
        _decision.CutStart.ShouldBe(5.2);
        _decision.CutEnd.ShouldBe(6.2);
    }
}

public class WhenSmartCutDeclinesToChangeAHit
{
    [Fact]
    public void KeepOriginalIsDistinctFromReject() =>
        SmartCutDecision.KeepOriginal.Outcome.ShouldNotBe(SmartCutDecision.Reject.Outcome);

    [Fact]
    public void KeepOriginalMeansLeaveTheTimestampsAlone() =>
        SmartCutDecision.KeepOriginal.Outcome.ShouldBe(SmartCutOutcome.KeepOriginal);

    [Fact]
    public void RejectMeansDropTheHit() =>
        SmartCutDecision.Reject.Outcome.ShouldBe(SmartCutOutcome.Reject);
}

public class WhenSmartCutIsUnconfigured
{
    private readonly SmartCutOptions _options;

    public WhenSmartCutIsUnconfigured()
    {
        _options = new SmartCutOptions();
    }

    [Fact]
    public void IsDisabled() => _options.Enabled.ShouldBeFalse();

    [Fact]
    public void DefaultsToTheLocalTransport() => _options.Mode.ShouldBe(SmartCutMode.Local);

    [Fact]
    public void UsesTheSameContextRadiusAsThePython() => _options.ContextRadius.ShouldBe(11);
}
