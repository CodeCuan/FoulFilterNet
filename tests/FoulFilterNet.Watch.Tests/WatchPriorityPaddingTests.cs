using FoulFilterNet.Domain;
using FoulFilterNet.Transcription;
using FoulFilterNet.Watch;
using Microsoft.Extensions.DependencyInjection;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// A 100-second video with one F-word collapsed to a 10 ms DTW point at 10 s,
/// as crosstalk leaves it, and an ordinary "damn" at 90 s.
/// </summary>
internal static class PriorityVideo
{
    public static readonly Script Script = new(
        100.0,
        Utterance.Of(Utterance.W("oh", 9.5, 9.9), Utterance.W("fuck", 10.0, 10.01)),
        Utterance.Of(Utterance.W("damn", 90.0, 90.4))
    );

    public static readonly BadWordsList BadWords = BadWordsList.FromLines(["damn", "fuck"]);

    public static readonly CutPadding Padding = CutPadding.ForPriorityWords(
        PriorityWordList.Default
    );

    public static WatchProgress Finish(CutPadding? padding, params int[] order)
    {
        var progress = WatchProgress.Start(Script.Duration, BadWords, padding);
        foreach (var index in order)
        {
            progress = progress.With(index, Script.Heard(index));
        }

        return progress;
    }
}

public class WhenWatchingWithPriorityPadding
{
    private readonly WatchProgress _progress;
    private readonly IReadOnlyList<Hit> _hits;

    public WhenWatchingWithPriorityPadding()
    {
        _progress = PriorityVideo.Finish(PriorityVideo.Padding, PriorityVideo.Script.AllWindows);
        _hits = _progress.Snapshot.Hits;

        _hits.Count.ShouldBe(2);
    }

    // 10.01 - 0.8 - 0.25
    [Fact]
    public void GrowsTheShortPriorityHitBackwardAndPadsIt() => _hits[0].Start.ShouldBe(8.96, 0.001);

    [Fact]
    public void PadsThePriorityHitsEndByHalfASecond() => _hits[0].End.ShouldBe(10.51, 0.001);

    [Fact]
    public void PadsTheOrdinaryHitAsBefore() =>
        (_hits[1].Start, _hits[1].End).ShouldBe((89.85, 90.65));

    [Fact]
    public void FindsWhatTheBatchPipelineFindsWithTheSamePadding() =>
        _hits.ShouldBe(
            PriorityVideo.Script.BatchHits(PriorityVideo.BadWords, PriorityVideo.Padding)
        );

    [Fact]
    public void KeepsItsCutPadding() => _progress.CutPadding.ShouldBeSameAs(PriorityVideo.Padding);

    [Fact]
    public void KeepsItsCutPaddingWhenTheBadWordsListChanges() =>
        _progress
            .WithBadWords(BadWordsList.FromLines(["fuck"]))
            .CutPadding.ShouldBeSameAs(PriorityVideo.Padding);

    [Fact]
    public void KeepsItsCutPaddingWhenThePlanIsReplaced() =>
        WatchProgress
            .Start(
                HeadStartVideo.ProvisionalPlan,
                HeadStartVideo.ClaimedDuration,
                HeadStartVideo.BadWords,
                PriorityVideo.Padding
            )
            .With(0, HeadStartVideo.Script.Heard(0))
            .WithPlan(HeadStartVideo.Script.Plan, HeadStartVideo.Duration)
            .CutPadding.ShouldBeSameAs(PriorityVideo.Padding);
}

public class WhenWatchingWithoutPriorityPadding
{
    private readonly WatchProgress _progress;

    public WhenWatchingWithoutPriorityPadding()
    {
        _progress = PriorityVideo.Finish(null, PriorityVideo.Script.AllWindows);

        _progress.Snapshot.Hits.Count.ShouldBe(2);
    }

    [Fact]
    public void UsesTheDefaultCutPadding() =>
        _progress.CutPadding.ShouldBeSameAs(CutPadding.Default);

    [Fact]
    public void PadsTheFWordLikeAnyOtherWord() =>
        (_progress.Snapshot.Hits[0].Start, _progress.Snapshot.Hits[0].End).ShouldBe((9.85, 10.26));
}

/// <summary>
/// A 10 ms priority Hit just inside an unfinished neighbour's share reaches
/// 0.8 + 0.25 s back across the edge, past the 1 s default guard, so Coverage
/// is trimmed by that reach instead.
/// </summary>
public class WhenOnlyTheFirstWindowIsHeardWithPriorityPadding
{
    private readonly Coverage _priority;
    private readonly Coverage _ordinary;

    public WhenOnlyTheFirstWindowIsHeardWithPriorityPadding()
    {
        _priority = PriorityVideo.Finish(PriorityVideo.Padding, 0).Coverage;
        _ordinary = PriorityVideo.Finish(null, 0).Coverage;

        _priority.Intervals.Count.ShouldBe(1);
        _ordinary.Intervals.Count.ShouldBe(1);
    }

    [Fact]
    public void TrimsTheEdgeByTheReachOfAShortPriorityHit() =>
        _priority.Intervals[0].To.ShouldBe(23.95, 0.0001);

    [Fact]
    public void KeepsTheDefaultGuardWithoutPriorityWords() =>
        _ordinary.Intervals[0].To.ShouldBe(24.0, 0.0001);

    [Fact]
    public void GuardsAtLeastTheDefaultWhateverThePadding() =>
        WatchProgress.GuardSecondsFor(CutPadding.None).ShouldBe(Coverage.DefaultGuardSeconds);
}

public class WhenACachedTranscriptIsWatchedWithPriorityPadding
{
    private readonly WatchProgress _progress;

    public WhenACachedTranscriptIsWatchedWithPriorityPadding()
    {
        _progress = WatchProgress.FromTranscript(
            PriorityVideo.Script.Batch(),
            PriorityVideo.BadWords,
            PriorityVideo.Padding
        );

        _progress.Snapshot.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void FindsWhatTheBatchPipelineFindsWithTheSamePadding() =>
        _progress.Snapshot.Hits.ShouldBe(
            PriorityVideo.Script.BatchHits(PriorityVideo.BadWords, PriorityVideo.Padding)
        );
}

public sealed class WhenASessionIsGivenPriorityPadding : IDisposable
{
    private readonly Harness _harness = new(PriorityVideo.Script);
    private readonly WatchSnapshot _snapshot;

    public WhenASessionIsGivenPriorityPadding()
    {
        _harness.BadWords.List = PriorityVideo.BadWords;
        _harness.CutPadding = PriorityVideo.Padding;
        _snapshot = Harness.Run(_harness.Session()).Snapshot();

        _snapshot.State.ShouldBe(WatchState.Complete);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void CutsThePriorityWordWider() =>
        _snapshot.Analysis!.Hits.ShouldBe(
            PriorityVideo.Script.BatchHits(PriorityVideo.BadWords, PriorityVideo.Padding)
        );
}

public sealed class WhenACachedSessionIsGivenPriorityPadding : IDisposable
{
    private readonly Harness _harness = new(PriorityVideo.Script);
    private readonly WatchSnapshot _snapshot;

    public WhenACachedSessionIsGivenPriorityPadding()
    {
        _harness.BadWords.List = PriorityVideo.BadWords;
        _harness.CutPadding = PriorityVideo.Padding;
        _harness.Store.Cached = new Transcript(
            Transcript.CurrentVersion,
            Harness.Video.Key,
            PriorityVideo.Script.Batch().Segments,
            PriorityVideo.Script.Batch().Words
        );
        _snapshot = Harness.Run(_harness.Session()).Snapshot();

        _snapshot.FromCache.ShouldBeTrue();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void CutsThePriorityWordWider() =>
        _snapshot.Analysis!.Hits.ShouldBe(
            PriorityVideo.Script.BatchHits(PriorityVideo.BadWords, PriorityVideo.Padding)
        );
}

public sealed class WhenAManagerIsGivenPriorityPadding : IDisposable
{
    private readonly Harness _harness = new(PriorityVideo.Script);
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot _snapshot;

    public WhenAManagerIsGivenPriorityPadding()
    {
        _harness.BadWords.List = PriorityVideo.BadWords;
        _harness.CutPadding = PriorityVideo.Padding;
        _manager = _harness.Manager();
        _manager.Heartbeat(Harness.Video, 0.0);
        Waits
            .Until(() => _manager.Find(Harness.Video)!.State == WatchState.Complete)
            .ShouldBeTrue();
        _snapshot = _manager.Find(Harness.Video)!;
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void GivesItsSessionsThatPadding() =>
        _snapshot.Analysis!.Hits.ShouldBe(
            PriorityVideo.Script.BatchHits(PriorityVideo.BadWords, PriorityVideo.Padding)
        );
}

public sealed class WhenWatchIsAddedWithACutPaddingRegistered : IDisposable
{
    private readonly Harness _harness = new(PriorityVideo.Script);
    private readonly ServiceProvider _provider;
    private readonly WatchSessionManager _manager;

    public WhenWatchIsAddedWithACutPaddingRegistered()
    {
        var services = WatchHost.Services(_harness);
        services.AddSingleton(PriorityVideo.Padding);
        services.AddWatch(WatchHost.Configuration(_harness));
        _provider = services.BuildServiceProvider();
        _manager = _provider.GetRequiredService<WatchSessionManager>();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void GivesTheManagerThatPadding() =>
        _manager.CutPadding.ShouldBeSameAs(PriorityVideo.Padding);
}

/// <summary>
/// The padding is configuration (X05), and the Coverage guard is derived from
/// whatever padding the host registered rather than from a constant: a wider
/// priority cut trims more off an edge next to an unfinished window, because a
/// 10 ms Hit just inside that window reaches that much further back.
/// </summary>
public class WhenThePriorityPaddingIsConfiguredWider
{
    private readonly CutPadding _padding = PriorityTuning
        .ForOptions(
            new TranscriptionOptions { PriorityPaddingPre = 0.5, PriorityMinimumCutSeconds = 2.0 }
        )
        .PaddingFor(PriorityWordList.Default);

    private readonly Coverage _coverage;

    public WhenThePriorityPaddingIsConfiguredWider()
    {
        _coverage = PriorityVideo.Finish(_padding, 0).Coverage;

        _coverage.Intervals.Count.ShouldBe(1);
    }

    [Fact]
    public void GuardsAsFarBackAsAShortPriorityHitNowReaches() =>
        WatchProgress.GuardSecondsFor(_padding).ShouldBe(2.5);

    [Fact]
    public void MovesTheGuardThatTheShippedPaddingPutsAt1Point05() =>
        WatchProgress
            .GuardSecondsFor(_padding)
            .ShouldBeGreaterThan(WatchProgress.GuardSecondsFor(PriorityVideo.Padding));

    [Fact]
    public void TrimsTheCoverageEdgeByTheConfiguredReach() =>
        _coverage.Intervals[0].To.ShouldBe(22.5, 0.0001);

    [Fact]
    public void CutsTheShortPriorityHitTheConfiguredWayRound() =>
        PriorityVideo
            .Finish(_padding, PriorityVideo.Script.AllWindows)
            .Snapshot.Hits[0]
            .Start.ShouldBe(10.01 - 2.0 - 0.5, 0.001);
}
