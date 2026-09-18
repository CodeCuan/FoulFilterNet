using FoulFilterNet.Sources;
using FoulFilterNet.Watch;
using FoulFilterNet.Web.Contracts;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// The wire spellings are a contract with the extension, written out rather
/// than derived from the enum members, so renaming a member cannot silently
/// change what the extension switches on.
/// </summary>
public class WhenAWatchStateIsSentOverTheWire
{
    [Theory]
    [InlineData(WatchState.Queued, "queued")]
    [InlineData(WatchState.Fetching, "fetching")]
    [InlineData(WatchState.Preparing, "preparing")]
    [InlineData(WatchState.Transcribing, "transcribing")]
    [InlineData(WatchState.Complete, "complete")]
    [InlineData(WatchState.Failed, "failed")]
    [InlineData(WatchState.Unsupported, "unsupported")]
    [InlineData(WatchState.Cancelled, "cancelled")]
    public void IsSpelledInLowerCase(WatchState state, string wire) =>
        WireNames.Of(state).ShouldBe(wire);

    [Fact]
    public void NamesEveryState() =>
        Enum.GetValues<WatchState>().Select(WireNames.Of).Distinct().Count().ShouldBe(8);

    [Fact]
    public void RefusesAStateThatDoesNotExist() =>
        Should.Throw<ArgumentOutOfRangeException>(() => WireNames.Of((WatchState)99));
}

public class WhenAFetchFailureKindIsSentOverTheWire
{
    [Theory]
    [InlineData(WebAudioFailure.Failed, "failed")]
    [InlineData(WebAudioFailure.NotInstalled, "not_installed")]
    [InlineData(WebAudioFailure.JsRuntimeMissing, "js_runtime_missing")]
    [InlineData(WebAudioFailure.Unavailable, "unavailable")]
    [InlineData(WebAudioFailure.SignInRequired, "sign_in_required")]
    [InlineData(WebAudioFailure.Unsupported, "unsupported")]
    [InlineData(WebAudioFailure.Network, "network")]
    public void IsSpelledInSnakeCase(WebAudioFailure kind, string wire) =>
        WireNames.Of(kind).ShouldBe(wire);

    [Fact]
    public void NamesEveryKind() =>
        Enum.GetValues<WebAudioFailure>().Select(WireNames.Of).Distinct().Count().ShouldBe(7);

    [Fact]
    public void RefusesAKindThatDoesNotExist() =>
        Should.Throw<ArgumentOutOfRangeException>(() => WireNames.Of((WebAudioFailure)99));
}

/// <summary>A snapshot turned into the view by hand, for the states the host tests cannot hold still.</summary>
public class WhenACancelledSnapshotIsViewed
{
    private readonly WatchView _view;

    public WhenACancelledSnapshotIsViewed()
    {
        var snapshot = new WatchSnapshot(
            VideoRef.Create("youtube", "jNQXAC9IVRw"),
            WatchState.Cancelled,
            Reason: "Cancelled.",
            FailureKind: null,
            Title: null,
            DurationSeconds: null,
            Analysis: null,
            WindowsDone: 0,
            WindowsTotal: 0,
            RealtimeFactor: null,
            FromCache: false
        )
        {
            SessionId = "abc",
        };

        _view = WatchView.From(snapshot, since: null, session: null);
    }

    [Fact]
    public void IsCancelled() => _view.State.ShouldBe("cancelled");

    [Fact]
    public void SaysWhy() => _view.Reason.ShouldBe("Cancelled.");

    [Fact]
    public void NamesTheSession() => _view.Session.ShouldBe("abc");

    [Fact]
    public void HasAnEmptyHitList() => _view.Hits.ShouldNotBeNull().ShouldBeEmpty();

    [Fact]
    public void HasNoCoverage() => _view.Coverage.ShouldBeEmpty();
}

/// <summary>A session still working slower than real time says so, for the overlay.</summary>
public class WhenASlowSnapshotIsViewed
{
    private readonly WatchView _view = WatchView.From(
        new WatchSnapshot(
            VideoRef.Create("youtube", "jNQXAC9IVRw"),
            WatchState.Transcribing,
            Reason: null,
            FailureKind: null,
            Title: "t",
            DurationSeconds: 100.0,
            Analysis: null,
            WindowsDone: 1,
            WindowsTotal: 4,
            RealtimeFactor: 0.5,
            FromCache: false
        ),
        since: null,
        session: null
    );

    [Fact]
    public void IsNotKeepingUp() => _view.KeepingUp.ShouldBeFalse();

    [Fact]
    public void ReportsTheFactor() => _view.RealtimeFactor.ShouldBe(0.5);

    [Fact]
    public void ReportsAQuarterDone() => _view.Progress.ShouldBe(0.25);
}
