using FoulFilterNet.Watch;

namespace FoulFilterNet.Watch.Tests;

/// <summary>
/// W10: revisions start again in every new session, so a client that says "I
/// already have revision 3" is only right if it is the <em>same</em> session's
/// revision 3. Every snapshot names its session so the endpoint can tell.
/// </summary>
public sealed class WhenOneSessionIsSnapshottedTwice : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot _first;
    private readonly WatchSnapshot _second;

    public WhenOneSessionIsSnapshottedTwice()
    {
        _manager = _harness.Manager();

        _first = _manager.Heartbeat(Harness.Video, 0.0);
        Waits.Until(() => _manager.Find(Harness.Video)!.State == WatchState.Complete);
        _second = _manager.Heartbeat(Harness.Video, 5.0);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void NamesTheSession() => _first.SessionId.ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void KeepsTheSameNameAcrossSnapshots() => _second.SessionId.ShouldBe(_first.SessionId);

    [Fact]
    public void KeepsTheNameOnceThereIsAnAnalysis() =>
        _manager.Find(Harness.Video)!.SessionId.ShouldBe(_first.SessionId);
}

public sealed class WhenAVideosSessionIsReplaced : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSessionManager _manager;
    private readonly WatchSnapshot _before;
    private readonly WatchSnapshot _after;

    public WhenAVideosSessionIsReplaced()
    {
        _manager = _harness.Manager();

        _before = _manager.Heartbeat(Harness.Video, 0.0);
        _manager.Cancel(Harness.Video).ShouldBeTrue();
        _after = _manager.Heartbeat(Harness.Video, 0.0);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    [Fact]
    public void TheNewSessionHasANewName() => _after.SessionId.ShouldNotBe(_before.SessionId);
}

public sealed class WhenTwoSessionsAreMadeDirectly : IDisposable
{
    private readonly Harness _harness = new();
    private readonly WatchSession _one;
    private readonly WatchSession _two;

    public WhenTwoSessionsAreMadeDirectly()
    {
        _one = _harness.Session();
        _two = _harness.Session();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void EachHasItsOwnId() => _one.Id.ShouldNotBe(_two.Id);

    [Fact]
    public void ItsSnapshotCarriesIt() => _one.Snapshot().SessionId.ShouldBe(_one.Id);
}
