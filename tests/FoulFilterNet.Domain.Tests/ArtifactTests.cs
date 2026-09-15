using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

public class WhenDescribingAHit
{
    private readonly Hit _hit;

    public WhenDescribingAHit()
    {
        _hit = new Hit("go to hell", 1.4, 2.1);

        _hit.End.ShouldBeGreaterThan(_hit.Start);
    }

    [Fact]
    public void ReportsItsDuration() => _hit.Duration.ShouldBe(0.7, 1e-9);

    [Fact]
    public void HasNoWordIndexUntilAlignmentSuppliesOne() => _hit.WordIndex.ShouldBeNull();

    [Fact]
    public void ComparesByValue() => _hit.ShouldBe(new Hit("go to hell", 1.4, 2.1));

    [Fact]
    public void WidensWithoutMutating()
    {
        var widened = _hit with { Start = 1.0 };

        widened.Start.ShouldBe(1.0);
        _hit.Start.ShouldBe(1.4);
    }
}

public class WhenATranscriberReturnsOnlySegments
{
    private readonly TranscriptionResult _result;

    public WhenATranscriberReturnsOnlySegments()
    {
        _result = new TranscriptionResult([new Segment(0, 1, "hello")], []);

        _result.Segments.ShouldNotBeEmpty();
    }

    [Fact]
    public void ReportsNoWordTimestamps() => _result.HasWordTimestamps.ShouldBeFalse();
}

public class WhenATranscriberReturnsWords
{
    private readonly TranscriptionResult _result;

    public WhenATranscriberReturnsWords()
    {
        _result = new TranscriptionResult(
            [new Segment(0, 1, "hello")],
            [new Word("hello", 0.1, 0.4)]);

        _result.Segments.ShouldNotBeEmpty();
    }

    [Fact]
    public void ReportsWordTimestamps() => _result.HasWordTimestamps.ShouldBeTrue();
}
