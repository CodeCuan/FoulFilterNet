using System.Net;
using System.Text.Json;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// <c>?since=&lt;revision&gt;&amp;session=&lt;session&gt;</c>: a client polling once a
/// second that already holds this session's current revision is told so, and
/// not sent the Hits again. Everything else in the view is still current,
/// because the state and progress move without the revision moving.
/// </summary>
public abstract class RevisionScenario : WatchScenario
{
    private protected RevisionScenario()
    {
        WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        Complete = WatchApi.WaitForStateAsync(Client, "complete").GetAwaiter().GetResult();
        Revision = Complete.GetProperty("revision").GetInt64();
        Session = Complete.GetProperty("session").GetString()!;

        Revision.ShouldBeGreaterThan(0);
    }

    private protected JsonElement Complete { get; }

    private protected long Revision { get; }

    private protected string Session { get; }

    private protected JsonElement Get(string query)
    {
        using var response = WatchApi.GetAsync(Client, query: query).GetAwaiter().GetResult();
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return Api.ReadAsync(response).GetAwaiter().GetResult();
    }
}

public sealed class WhenTheClientAlreadyHasTheLatestRevision : RevisionScenario
{
    private readonly JsonElement _view;

    public WhenTheClientAlreadyHasTheLatestRevision()
    {
        _view = Get($"?since={Revision}&session={Session}");
    }

    [Fact]
    public void SaysNothingHasChanged() =>
        _view.GetProperty("unchanged").GetBoolean().ShouldBeTrue();

    [Fact]
    public void LeavesTheHitsOut() =>
        _view.GetProperty("hits").ValueKind.ShouldBe(JsonValueKind.Null);

    [Fact]
    public void StillSendsTheCoverage() =>
        _view.GetProperty("coverage").GetArrayLength().ShouldBe(1);

    [Fact]
    public void StillSendsTheState() => _view.GetProperty("state").GetString().ShouldBe("complete");

    [Fact]
    public void StillSendsTheRevision() =>
        _view.GetProperty("revision").GetInt64().ShouldBe(Revision);

    [Fact]
    public void StillSendsTheProgress() => _view.GetProperty("progress").GetDouble().ShouldBe(1.0);

    [Fact]
    public void KeepsEveryFieldName() =>
        WatchApi.PropertyNames(_view).ShouldBe(WatchApi.PropertyNames(Complete));
}

public sealed class WhenTheClientHasAnOlderRevision : RevisionScenario
{
    private readonly JsonElement _view;

    public WhenTheClientHasAnOlderRevision()
    {
        _view = Get($"?since={Revision - 1}&session={Session}");
    }

    [Fact]
    public void SaysItChanged() => _view.GetProperty("unchanged").GetBoolean().ShouldBeFalse();

    [Fact]
    public void SendsTheHits() => _view.GetProperty("hits").GetArrayLength().ShouldBe(1);
}

/// <summary>
/// A revision from a later session than the client has seen (the numbers
/// restarted, then overtook) is still a different revision: equality, not
/// "at least".
/// </summary>
public sealed class WhenTheClientClaimsANewerRevision : RevisionScenario
{
    private readonly JsonElement _view;

    public WhenTheClientClaimsANewerRevision()
    {
        _view = Get($"?since={Revision + 1}&session={Session}");
    }

    [Fact]
    public void SaysItChanged() => _view.GetProperty("unchanged").GetBoolean().ShouldBeFalse();

    [Fact]
    public void SendsTheHits() => _view.GetProperty("hits").GetArrayLength().ShouldBe(1);
}

/// <summary>
/// Revisions start again in every session, so revision 1 of a session that was
/// dropped and made again is not the revision 1 the client holds.
/// </summary>
public sealed class WhenTheClientsRevisionIsFromAnotherSession : RevisionScenario
{
    private readonly JsonElement _view;

    public WhenTheClientsRevisionIsFromAnotherSession()
    {
        _view = Get($"?since={Revision}&session=0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public void SaysItChanged() => _view.GetProperty("unchanged").GetBoolean().ShouldBeFalse();

    [Fact]
    public void SendsTheHits() => _view.GetProperty("hits").GetArrayLength().ShouldBe(1);
}

/// <summary>Without the session, a revision cannot be trusted, so the full answer is sent.</summary>
public sealed class WhenTheClientGivesARevisionWithoutASession : RevisionScenario
{
    private readonly JsonElement _view;

    public WhenTheClientGivesARevisionWithoutASession()
    {
        _view = Get($"?since={Revision}");
    }

    [Fact]
    public void SaysItChanged() => _view.GetProperty("unchanged").GetBoolean().ShouldBeFalse();

    [Fact]
    public void SendsTheHits() => _view.GetProperty("hits").GetArrayLength().ShouldBe(1);
}

/// <summary>The heartbeat takes the same query, since that is what the extension polls with.</summary>
public sealed class WhenAHeartbeatAlreadyHasTheLatestRevision : RevisionScenario
{
    private readonly JsonElement _view;

    public WhenAHeartbeatAlreadyHasTheLatestRevision()
    {
        using var response = WatchApi
            .PostAsync(Client, position: 3.0, query: $"?since={Revision}&session={Session}")
            .GetAwaiter()
            .GetResult();
        _view = Api.ReadAsync(response).GetAwaiter().GetResult();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void SaysNothingHasChanged() =>
        _view.GetProperty("unchanged").GetBoolean().ShouldBeTrue();

    [Fact]
    public void LeavesTheHitsOut() =>
        _view.GetProperty("hits").ValueKind.ShouldBe(JsonValueKind.Null);
}

public sealed class WhenTheRevisionIsNotANumber : RevisionScenario
{
    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("99999999999999999999")]
    public async Task GetRefusesIt(string since)
    {
        using var response = await WatchApi.GetAsync(
            Client,
            query: $"?since={Uri.EscapeDataString(since)}&session={Session}"
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostRefusesIt()
    {
        using var response = await WatchApi.PostAsync(
            Client,
            query: $"?since=abc&session={Session}"
        );

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NamesItAsTheProblem()
    {
        using var response = await WatchApi.GetAsync(Client, query: "?since=abc");
        var body = await Api.ReadAsync(response);

        body.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("since");
    }
}

/// <summary>Before there is any analysis, revision 0 is still a revision the client can hold.</summary>
public sealed class WhenTheClientHoldsRevisionZeroBeforeAnyAnalysis : WatchScenario
{
    private readonly JsonElement _view;

    public WhenTheClientHoldsRevisionZeroBeforeAnyAnalysis()
    {
        var fetch = App.WebAudio.Hold = new Gate();
        var first = WatchApi.StartAsync(Client).GetAwaiter().GetResult();
        fetch.WaitUntilEntered();

        using var response = WatchApi
            .GetAsync(Client, query: $"?since=0&session={first.GetProperty("session").GetString()}")
            .GetAwaiter()
            .GetResult();
        _view = Api.ReadAsync(response).GetAwaiter().GetResult();
    }

    [Fact]
    public void SaysNothingHasChanged() =>
        _view.GetProperty("unchanged").GetBoolean().ShouldBeTrue();

    [Fact]
    public void StillSaysItIsFetching() =>
        _view.GetProperty("state").GetString().ShouldBe("fetching");
}
