using FoulFilterNet.Domain;

namespace FoulFilterNet.Media.Tests;

/// <summary>
/// Shared construction for the filtergraph specs. Mirrors the Python suite's
/// <c>_hit(start, end, phrase="x")</c> helper so the ported assertions read the
/// same way.
/// </summary>
internal static class HitFactory
{
    public static Hit Hit(double start, double end, string phrase = "x") => new(phrase, start, end);

    public static IReadOnlyList<Hit> Hits(params Hit[] hits) => hits;
}

public class WhenBuildingTheSilenceFilter
{
    private readonly string _graph;

    public WhenBuildingTheSilenceFilter()
    {
        _graph = FilterGraph.Silence(
            HitFactory.Hits(HitFactory.Hit(1.0, 2.0), HitFactory.Hit(4.0, 5.0))
        );

        _graph.ShouldNotBeNullOrWhiteSpace();
        _graph.ShouldNotContain("[");
    }

    [Fact]
    public void ChainsOneVolumeGatePerHit() =>
        _graph.ShouldBe(
            "volume=enable='between(t,1.0,2.0)':volume=0,"
                + "volume=enable='between(t,4.0,5.0)':volume=0"
        );

    [Fact]
    public void JoinsTheGatesWithCommas() => _graph.Split(",volume=").Length.ShouldBe(2);

    [Fact]
    public void RendersAWholeSecondWithItsDecimalPointAsPythonDoes() =>
        FilterGraph
            .Silence(HitFactory.Hits(HitFactory.Hit(3.0, 6.0)))
            .ShouldBe("volume=enable='between(t,3.0,6.0)':volume=0");

    [Fact]
    public void RendersAFractionWithoutPaddingIt() =>
        FilterGraph
            .Silence(HitFactory.Hits(HitFactory.Hit(0.15, 2.7)))
            .ShouldBe("volume=enable='between(t,0.15,2.7)':volume=0");
}

public class WhenBuildingTheBleepFilter
{
    private readonly string _graph;

    public WhenBuildingTheBleepFilter()
    {
        _graph = FilterGraph.Bleep(HitFactory.Hits(HitFactory.Hit(2.0, 2.7)));

        _graph.ShouldNotBeNullOrWhiteSpace();
        _graph.ShouldEndWith("[out]");
    }

    [Fact]
    public void NormalisesTheSourceToStereo44100() =>
        _graph.ShouldContain("[0:a]aformat=sample_rates=44100:channel_layouts=stereo,");

    [Fact]
    public void MutesTheHitWindowToProduceTheBaseStream() =>
        _graph.ShouldContain("volume=enable='between(t,2.0,2.7)':volume=0[base]");

    [Fact]
    public void GeneratesAOneKilohertzSinePerHit() =>
        _graph.ShouldContain("sine=frequency=1000:duration=0.700[s0]");

    [Fact]
    public void DelaysTheSineIntoPositionOnBothChannels() =>
        _graph.ShouldContain("[s0]adelay=2000|2000[b0]");

    [Fact]
    public void MixesTheToneOverTheBaseWithoutNormalising() =>
        _graph.ShouldContain("[base][b0]amix=inputs=2:duration=first:normalize=0[out]");

    [Fact]
    public void ProducesTheWholeGraphExactly() =>
        _graph.ShouldBe(
            "[0:a]aformat=sample_rates=44100:channel_layouts=stereo,"
                + "volume=enable='between(t,2.0,2.7)':volume=0[base]"
                + ";sine=frequency=1000:duration=0.700[s0]"
                + ";[s0]adelay=2000|2000[b0]"
                + ";[base][b0]amix=inputs=2:duration=first:normalize=0[out]"
        );
}

public class WhenBleepingSeveralHits
{
    private readonly string _graph;

    public WhenBleepingSeveralHits()
    {
        _graph = FilterGraph.Bleep(
            HitFactory.Hits(HitFactory.Hit(0.0, 0.5), HitFactory.Hit(4.25, 4.26))
        );

        _graph.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void NumbersEachGeneratedSine() => _graph.ShouldContain("[s1]adelay=4250|4250[b1]");

    [Fact]
    public void CountsEveryStreamIntoTheMix() =>
        _graph.ShouldContain("[base][b0][b1]amix=inputs=3:duration=first:normalize=0[out]");

    [Fact]
    public void FloorsAVeryShortHitToFiftyMilliseconds() =>
        _graph.ShouldContain("sine=frequency=1000:duration=0.050[s1]");

    [Fact]
    public void DelaysAHitAtTheOriginByNothing() => _graph.ShouldContain("[s0]adelay=0|0[b0]");
}

public class WhenBuildingTheRemoveFilter
{
    private readonly string _graph;

    public WhenBuildingTheRemoveFilter()
    {
        _graph = FilterGraph.Remove(
            HitFactory.Hits(HitFactory.Hit(1.0, 2.0), HitFactory.Hit(3.0, 4.0))
        );

        _graph.ShouldNotBeNullOrWhiteSpace();
        _graph.ShouldEndWith("[out]");
    }

    [Fact]
    public void KeepsTheHeadBeforeTheFirstHit() =>
        _graph.ShouldContain("atrim=start=0.000:end=1.000");

    [Fact]
    public void KeepsTheGapBetweenTwoHits() => _graph.ShouldContain("atrim=start=2.000:end=3.000");

    [Fact]
    public void KeepsAnOpenEndedTailAfterTheLastHit() => _graph.ShouldContain("atrim=start=4.000");

    [Fact]
    public void ConcatenatesEveryKeptSpanAsAudioOnly() =>
        _graph.ShouldContain("concat=n=3:v=0:a=1[out]");

    [Fact]
    public void ResetsPresentationTimestampsOnEachSpan() =>
        _graph
            .Split(';')
            .Count(part => part.Contains("asetpts=PTS-STARTPTS", StringComparison.Ordinal))
            .ShouldBe(3);

    [Fact]
    public void ProducesTheWholeGraphExactly() =>
        _graph.ShouldBe(
            "[0:a]atrim=start=0.000:end=1.000,asetpts=PTS-STARTPTS[clip0]"
                + ";[0:a]atrim=start=2.000:end=3.000,asetpts=PTS-STARTPTS[clip1]"
                + ";[0:a]atrim=start=4.000,asetpts=PTS-STARTPTS[clip2]"
                + ";[clip0][clip1][clip2]concat=n=3:v=0:a=1[out]"
        );
}

public class WhenRemovingHitsGivenTheFileDuration
{
    private readonly string _graph;

    public WhenRemovingHitsGivenTheFileDuration()
    {
        _graph = FilterGraph.Remove(
            HitFactory.Hits(HitFactory.Hit(1.0, 5.0)),
            totalDurationSeconds: 5.0
        );

        _graph.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DropsTheTailWhenTheLastHitReachesTheEnd() => _graph.ShouldNotContain("[clip1]");

    [Fact]
    public void ConcatenatesOnlyTheHead() =>
        _graph.ShouldBe(
            "[0:a]atrim=start=0.000:end=1.000,asetpts=PTS-STARTPTS[clip0]"
                + ";[clip0]concat=n=1:v=0:a=1[out]"
        );

    [Fact]
    public void SortsHitsByStartBeforeTrimming() =>
        FilterGraph
            .Remove(HitFactory.Hits(HitFactory.Hit(3.0, 4.0), HitFactory.Hit(1.0, 2.0)))
            .ShouldContain("atrim=start=0.000:end=1.000,asetpts=PTS-STARTPTS[clip0]");

    [Fact]
    public void SwallowsOverlappingHitsIntoOneGap() =>
        FilterGraph
            .Remove(HitFactory.Hits(HitFactory.Hit(1.0, 3.0), HitFactory.Hit(2.0, 4.0)))
            .ShouldBe(
                "[0:a]atrim=start=0.000:end=1.000,asetpts=PTS-STARTPTS[clip0]"
                    + ";[0:a]atrim=start=4.000,asetpts=PTS-STARTPTS[clip1]"
                    + ";[clip0][clip1]concat=n=2:v=0:a=1[out]"
            );
}

public class WhenHitsWouldConsumeTheWholeFile
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenHitsWouldConsumeTheWholeFile()
    {
        _hits = HitFactory.Hits(HitFactory.Hit(0.0, 10.0));

        _hits.ShouldHaveSingleItem();
    }

    [Fact]
    public void RefusesToBuildAGraphThatWouldLeaveNothing() =>
        Should.Throw<ArgumentException>(() =>
            FilterGraph.Remove(_hits, totalDurationSeconds: 10.0)
        );

    [Fact]
    public void SaysWhyItRefused() =>
        Should
            .Throw<ArgumentException>(() => FilterGraph.Remove(_hits, totalDurationSeconds: 10.0))
            .Message.ShouldContain("entire file", Case.Insensitive);

    [Fact]
    public void ToleratesAMillisecondOfSlackAtTheEnd() =>
        Should.Throw<ArgumentException>(() =>
            FilterGraph.Remove(_hits, totalDurationSeconds: 10.0005)
        );

    [Fact]
    public void StillBuildsWhenTheDurationIsUnknown() =>
        FilterGraph.Remove(_hits).ShouldContain("concat=n=1:v=0:a=1[out]");
}

public class WhenBuildingAFilterFromNoHits
{
    private readonly IReadOnlyList<Hit> _none;

    public WhenBuildingAFilterFromNoHits()
    {
        _none = HitFactory.Hits();

        _none.ShouldBeEmpty();
    }

    [Fact]
    public void SilenceRefuses() =>
        Should.Throw<ArgumentException>(() => FilterGraph.Silence(_none));

    [Fact]
    public void BleepRefuses() => Should.Throw<ArgumentException>(() => FilterGraph.Bleep(_none));

    [Fact]
    public void RemoveRefuses() => Should.Throw<ArgumentException>(() => FilterGraph.Remove(_none));

    [Fact]
    public void SilenceNamesTheOffendingArgument() =>
        Should
            .Throw<ArgumentException>(() => FilterGraph.Silence(_none))
            .ParamName.ShouldBe("hits");
}
