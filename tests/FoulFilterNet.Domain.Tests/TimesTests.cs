using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

public class WhenRoundingATimestamp
{
    private readonly double _rounded;

    public WhenRoundingATimestamp()
    {
        _rounded = Times.Round(1.23456789);

        _rounded.ShouldBePositive();
        _rounded.ShouldBeLessThan(2.0);
    }

    [Fact]
    public void KeepsThreeDecimalPlaces() => _rounded.ShouldBe(1.235);

    [Fact]
    public void LeavesAnAlreadyShortValueAlone() => Times.Round(0.15).ShouldBe(0.15);

    [Fact]
    public void RoundsHalvesAwayFromZeroAsPythonDoes() => Times.Round(2.0005).ShouldBe(2.001);

    [Fact]
    public void PreservesZero() => Times.Round(0).ShouldBe(0);
}

/// <summary>
/// These are the assertions that catch the formatting trap described in
/// <see cref="Times"/>: the silence filter interpolates raw floats the way
/// Python's repr does, and C#'s default rendering drops the decimal point.
/// </summary>
public class WhenRenderingATimestampAsPythonRepr
{
    private readonly string _integral;
    private readonly string _fractional;

    public WhenRenderingATimestampAsPythonRepr()
    {
        _integral = Times.ToRepr(1.0);
        _fractional = Times.ToRepr(2.7);

        _integral.ShouldNotBeNullOrWhiteSpace();
        _fractional.ShouldNotBeNullOrWhiteSpace();
        _integral.ShouldContain(".");
        _fractional.ShouldContain(".");
    }

    [Fact]
    public void KeepsTheDecimalPointOnAWholeNumber() => _integral.ShouldBe("1.0");

    [Fact]
    public void RendersAFractionUnchanged() => _fractional.ShouldBe("2.7");

    [Fact]
    public void RendersZeroWithAPoint() => Times.ToRepr(0).ShouldBe("0.0");

    [Fact]
    public void DoesNotPadToAFixedWidth() => Times.ToRepr(0.85).ShouldBe("0.85");

    [Fact]
    public void IsCultureInvariant() => Times.ToRepr(1.5).ShouldBe("1.5");
}

public class WhenRenderingATimestampToThreePlaces
{
    private readonly string _rendered;

    public WhenRenderingATimestampToThreePlaces()
    {
        _rendered = Times.ToFixed(1.0);

        _rendered.ShouldContain(".");
    }

    [Fact]
    public void AlwaysPadsToThreeDecimals() => _rendered.ShouldBe("1.000");

    [Fact]
    public void TruncatesLongerValues() => Times.ToFixed(0.7000001).ShouldBe("0.700");

    [Fact]
    public void RendersZeroPadded() => Times.ToFixed(0).ShouldBe("0.000");
}
