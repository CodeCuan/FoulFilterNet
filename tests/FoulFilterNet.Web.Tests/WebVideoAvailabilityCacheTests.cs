using FoulFilterNet.Sources;
using Microsoft.Extensions.Time.Testing;

namespace FoulFilterNet.Web.Tests;

/// <summary>An availability probe the test can hold, fail, and count.</summary>
internal sealed class ProbeSource : IWebAudioSource
{
    private int _probes;

    public WebVideoAvailability Answer { get; set; } = new("2026.09.01", "2.5.0");

    public Exception? Failure { get; set; }

    public Gate? Hold { get; set; }

    public CancellationToken LastToken { get; private set; }

    public int Probes => Volatile.Read(ref _probes);

    public async Task<WebVideoAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default
    )
    {
        Interlocked.Increment(ref _probes);
        LastToken = cancellationToken;

        if (Hold is { } gate)
        {
            await gate.PassAsync(cancellationToken);
        }

        return Failure is { } failure ? throw failure : Answer;
    }

    public Task<WebAudio> FetchAudioAsync(
        VideoRef video,
        string directory,
        CancellationToken cancellationToken = default
    ) => throw new NotSupportedException("The availability cache never fetches.");
}

public abstract class AvailabilityCacheScenario : IDisposable
{
    private protected AvailabilityCacheScenario()
    {
        Cache = new WebVideoAvailabilityCache(Source, Time);
    }

    private protected ProbeSource Source { get; } = new();

    private protected FakeTimeProvider Time { get; } =
        new(new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero));

    private protected WebVideoAvailabilityCache Cache { get; }

    private protected WebVideoAvailability Get() =>
        Cache.GetAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    public void Dispose()
    {
        Cache.Dispose();
        GC.SuppressFinalize(this);
    }
}

public sealed class WhenAvailabilityIsAskedTwiceInQuickSuccession : AvailabilityCacheScenario
{
    private readonly WebVideoAvailability _first;
    private readonly WebVideoAvailability _second;

    public WhenAvailabilityIsAskedTwiceInQuickSuccession()
    {
        _first = Get();
        _second = Get();
    }

    [Fact]
    public void ProbesOnce() => Source.Probes.ShouldBe(1);

    [Fact]
    public void AnswersTheSame() => _second.ShouldBeSameAs(_first);

    [Fact]
    public void AnswersWhatTheProbeFound() => _first.ShouldBe(Source.Answer);
}

/// <summary>A working setup is re-checked every five minutes, so an uninstall is noticed.</summary>
public sealed class WhenAnAvailableAnswerAges : AvailabilityCacheScenario
{
    public WhenAnAvailableAnswerAges()
    {
        Get();
    }

    [Fact]
    public void IsKeptForJustUnderFiveMinutes()
    {
        Time.Advance(WebVideoAvailabilityCache.AvailableFor - TimeSpan.FromSeconds(1));
        Get();

        Source.Probes.ShouldBe(1);
    }

    [Fact]
    public void IsProbedAgainAtFiveMinutes()
    {
        Time.Advance(WebVideoAvailabilityCache.AvailableFor);
        Get();

        Source.Probes.ShouldBe(2);
    }

    [Fact]
    public void LastsFiveMinutes() =>
        WebVideoAvailabilityCache.AvailableFor.ShouldBe(TimeSpan.FromMinutes(5));
}

/// <summary>
/// A missing tool is re-checked sooner: the user who has just installed Deno
/// reloads the page and should not wait five minutes to be believed.
/// </summary>
public sealed class WhenAnUnavailableAnswerAges : AvailabilityCacheScenario
{
    public WhenAnUnavailableAnswerAges()
    {
        Source.Answer = new WebVideoAvailability("2026.09.01", null);
        Get();
    }

    [Fact]
    public void IsKeptForJustUnderItsShorterLife()
    {
        Time.Advance(WebVideoAvailabilityCache.UnavailableFor - TimeSpan.FromSeconds(1));
        Get();

        Source.Probes.ShouldBe(1);
    }

    [Fact]
    public void IsProbedAgainOnceItHasLapsed()
    {
        Time.Advance(WebVideoAvailabilityCache.UnavailableFor);
        Get();

        Source.Probes.ShouldBe(2);
    }

    [Fact]
    public void LivesShorterThanAnAvailableAnswer() =>
        WebVideoAvailabilityCache.UnavailableFor.ShouldBeLessThan(
            WebVideoAvailabilityCache.AvailableFor
        );

    [Fact]
    public void PicksUpTheFix()
    {
        Source.Answer = new WebVideoAvailability("2026.09.01", "2.5.0");
        Time.Advance(WebVideoAvailabilityCache.UnavailableFor);

        Get().IsAvailable.ShouldBeTrue();
    }
}

/// <summary>The UI loading while the startup probe is still running waits for that one, not a second.</summary>
public sealed class WhenCallersArriveWhileAProbeRuns : AvailabilityCacheScenario
{
    private readonly WebVideoAvailability _first;
    private readonly WebVideoAvailability _second;

    public WhenCallersArriveWhileAProbeRuns()
    {
        var gate = Source.Hold = new Gate();

        var first = Cache.GetAsync(TestContext.Current.CancellationToken);
        gate.WaitUntilEntered();

        // A probe still running is never "expired", however long it takes.
        Time.Advance(TimeSpan.FromHours(1));
        var second = Cache.GetAsync(TestContext.Current.CancellationToken);
        gate.Release();

        _first = first.GetAwaiter().GetResult();
        _second = second.GetAwaiter().GetResult();
    }

    [Fact]
    public void ProbesOnce() => Source.Probes.ShouldBe(1);

    [Fact]
    public void GivesBothTheSameAnswer() => _second.ShouldBeSameAs(_first);
}

/// <summary>One request hanging up must not cancel the probe everyone else is waiting on.</summary>
public sealed class WhenACallerGivesUpWaiting : AvailabilityCacheScenario
{
    private readonly Task<WebVideoAvailability> _impatient;
    private readonly WebVideoAvailability _patient;

    public WhenACallerGivesUpWaiting()
    {
        var gate = Source.Hold = new Gate();
        using var hangUp = new CancellationTokenSource();

        _impatient = Cache.GetAsync(hangUp.Token);
        gate.WaitUntilEntered();
        var patient = Cache.GetAsync(TestContext.Current.CancellationToken);
        hangUp.Cancel();
        gate.Release();

        _patient = patient.GetAwaiter().GetResult();
    }

    [Fact]
    public void TheImpatientCallerIsCancelled() =>
        Should.Throw<OperationCanceledException>(() => _impatient.GetAwaiter().GetResult());

    [Fact]
    public void ThePatientCallerStillGetsTheAnswer() => _patient.IsAvailable.ShouldBeTrue();

    [Fact]
    public void TheProbeWasNotCancelledByTheCaller() =>
        Source.LastToken.IsCancellationRequested.ShouldBeFalse();
}

/// <summary>
/// <c>/config</c> is the UI's first call and must not fail because of a feature
/// the batch UI does not even use: an unexpected probe error reads as "not
/// available", and is retried like one.
/// </summary>
public sealed class WhenTheProbeThrows : AvailabilityCacheScenario
{
    private readonly WebVideoAvailability _answer;

    public WhenTheProbeThrows()
    {
        Source.Failure = new InvalidOperationException("The process table is full.");
        _answer = Get();
    }

    [Fact]
    public void AnswersUnavailable() => _answer.IsAvailable.ShouldBeFalse();

    [Fact]
    public void HasNoVersions() => _answer.ShouldBe(new WebVideoAvailability(null, null));

    [Fact]
    public void RetriesLikeAnyUnavailableAnswer()
    {
        Source.Failure = null;
        Time.Advance(WebVideoAvailabilityCache.UnavailableFor);

        Get().IsAvailable.ShouldBeTrue();
    }
}

/// <summary>The probe is started with the host, so the UI's first <c>/config</c> does not wait on yt-dlp.</summary>
public sealed class WhenTheHostStarts : AvailabilityCacheScenario
{
    public WhenTheHostStarts()
    {
        Cache.StartAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    [Fact]
    public void ProbesStraightAway() => Source.Probes.ShouldBe(1);

    [Fact]
    public void ServesTheFirstRequestFromThatProbe()
    {
        Get();

        Source.Probes.ShouldBe(1);
    }
}

/// <summary>Shutting down kills a probe still running, rather than leaving yt-dlp behind.</summary>
public sealed class WhenTheHostStopsDuringAProbe : AvailabilityCacheScenario
{
    public WhenTheHostStopsDuringAProbe()
    {
        var gate = Source.Hold = new Gate();
        Cache.StartAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        gate.WaitUntilEntered();

        Cache.StopAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
    }

    [Fact]
    public void CancelsTheProbe() => Source.LastToken.IsCancellationRequested.ShouldBeTrue();
}
