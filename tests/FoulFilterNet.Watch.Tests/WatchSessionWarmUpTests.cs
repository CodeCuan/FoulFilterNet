using FoulFilterNet.Sources;
using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// W17: the model loads while the audio is fetched, not after the WAV exists.
/// </summary>
public sealed class WhenAMissIsFetched : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly int _warmUpsWhileFetching;

    public WhenAMissIsFetched()
    {
        var fetch = _harness.Source.Hold = new Gate();
        _session = _harness.Session();
        _session.Start();

        fetch.WaitUntilEntered();
        Waits.Until(() => _harness.Engine.IsWarm);
        _warmUpsWhileFetching = _harness.Engine.WarmUps;
        fetch.Release();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void WarmsTheModelBeforeTheAudioArrives() => _warmUpsWhileFetching.ShouldBe(1);

    [Fact]
    public void WarmsItOnlyOnce() => _harness.Engine.WarmUps.ShouldBe(1);

    [Fact]
    public void FindsItWarmWhenTheWavIsOpened() =>
        _harness.Engine.WarmWhenOpened.ShouldAllBe(warm => warm);

    [Fact]
    public void StillCompletes() => _session.State.ShouldBe(WatchState.Complete);
}

public sealed class WhenTheModelIsStillLoadingOnceTheWavIsReady : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly WatchState _stateWhileLoading;
    private readonly int _opensWhileLoading;

    public WhenTheModelIsStillLoadingOnceTheWavIsReady()
    {
        var load = _harness.Engine.WarmUpHold = new Gate();
        _session = _harness.Session();
        _session.Start();

        load.WaitUntilEntered();
        Waits.Until(() => _harness.Preparer.Prepared.Count == 1);
        Thread.Sleep(50);
        _stateWhileLoading = _session.State;
        _opensWhileLoading = _harness.Engine.Opened.Count;
        load.Release();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FetchesAndPreparesWithoutWaitingForTheModel() =>
        _harness.Preparer.Prepared.Count.ShouldBe(1);

    [Fact]
    public void IsStillPreparingUntilTheModelIsReady() =>
        _stateWhileLoading.ShouldBe(WatchState.Preparing);

    [Fact]
    public void OpensNothingUntilTheModelIsReady() => _opensWhileLoading.ShouldBe(0);

    [Fact]
    public void ThenCompletes() => _session.State.ShouldBe(WatchState.Complete);
}

public sealed class WhenTheWarmUpFails : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;

    public WhenTheWarmUpFails()
    {
        _harness.Engine.WarmUpFailure = new InvalidOperationException("No CUDA today.");
        _session = Harness.Run(_harness.Session());
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void LeavesItToTheOpenToSayWhetherTheModelLoads() =>
        _session.State.ShouldBe(WatchState.Complete);

    [Fact]
    public void StillOpensTheWav() => _harness.Engine.Opened.Count.ShouldBe(1);
}

public sealed class WhenTheWarmUpFailsAndSoDoesTheOpen : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSnapshot _snapshot;

    public WhenTheWarmUpFailsAndSoDoesTheOpen()
    {
        _harness.Engine.WarmUpFailure = new FileNotFoundException("ggml-base.bin is missing.");
        _harness.Engine.OpenFailure = new FileNotFoundException("ggml-base.bin is missing.");
        _snapshot = Harness.Run(_harness.Session()).Snapshot();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Fails() => _snapshot.State.ShouldBe(WatchState.Failed);

    [Fact]
    public void GivesTheOpensReason() => _snapshot.Reason.ShouldBe("ggml-base.bin is missing.");

    [Fact]
    public void CleansUp() => _harness.ScratchIsEmpty.ShouldBeTrue();
}

public sealed class WhenTheTranscriptIsCachedTheModelIsLeftAlone : IDisposable
{
    private readonly Harness _harness = new();

    public WhenTheTranscriptIsCachedTheModelIsLeftAlone()
    {
        _harness.Store.Cached = Harness.CachedTranscript(Harness.Video.Key);
        Harness.Run(_harness.Session()).State.ShouldBe(WatchState.Complete);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void NeverWarmsTheModel() => _harness.Engine.WarmUps.ShouldBe(0);
}

public sealed class WhenTheFetchFailsWhileTheModelLoads : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;
    private readonly bool _completedWhileLoading;

    public WhenTheFetchFailsWhileTheModelLoads()
    {
        var load = _harness.Engine.WarmUpHold = new Gate();
        _harness.Source.Failure = FakeAudioSource.Failing(
            WebAudioFailure.Unavailable,
            "Video unavailable"
        );
        _session = _harness.Session();
        _session.Start();

        load.WaitUntilEntered();
        Waits.Until(() => _session.State == WatchState.Failed);
        Thread.Sleep(50);
        _completedWhileLoading = _session.Completion.IsCompleted;
        load.Release();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void FailsWithTheFetchsReason() =>
        _session.Snapshot().Reason.ShouldBe("Video unavailable");

    [Fact]
    public void WaitsForTheLoadBeforeCountingAsFinished() => _completedWhileLoading.ShouldBeFalse();

    [Fact]
    public void NeverOpensTheEngine() => _harness.Engine.Opened.ShouldBeEmpty();
}

public sealed class WhenCancelledWhileTheModelLoads : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _session;

    public WhenCancelledWhileTheModelLoads()
    {
        var load = _harness.Engine.WarmUpHold = new Gate();
        var fetch = _harness.Source.Hold = new Gate();
        _session = _harness.Session();
        _session.Start();

        load.WaitUntilEntered();
        fetch.WaitUntilEntered();
        _session.Cancel();

        Waits.On(_session.Completion);
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void EndsCancelled() => _session.State.ShouldBe(WatchState.Cancelled);

    [Fact]
    public void CancelsTheWarmUpToo() =>
        _harness.Engine.WarmUpTokens.Single().IsCancellationRequested.ShouldBeTrue();

    [Fact]
    public void CleansUp() => _harness.ScratchIsEmpty.ShouldBeTrue();
}
