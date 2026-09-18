namespace FoulFilterNet.Sources.Tests;

public class WhenYtDlpAndDenoAreBothInstalled
{
    private readonly FakeProcessRunner _runner = new();
    private readonly WebVideoAvailability _availability;

    public WhenYtDlpAndDenoAreBothInstalled()
    {
        _runner
            .On("yt-dlp", 0, YtDlpSamples.YtDlpVersionOutput)
            .On("deno", 0, YtDlpSamples.DenoVersionOutput);

        _availability = new YtDlpAudioSource(_runner, new SourcesOptions())
            .CheckAvailabilityAsync()
            .GetAwaiter()
            .GetResult();

        _availability.ShouldNotBeNull();
    }

    [Fact]
    public void IsAvailable() => _availability.IsAvailable.ShouldBeTrue();

    [Fact]
    public void ReportsYtDlpsVersion() => _availability.YtDlpVersion.ShouldBe("2026.08.19");

    [Fact]
    public void ReportsDenosVersion() => _availability.DenoVersion.ShouldBe("2.9.6");

    [Fact]
    public void FoundYtDlp() => _availability.YtDlpFound.ShouldBeTrue();

    [Fact]
    public void FoundAJavaScriptRuntime() => _availability.JsRuntimeFound.ShouldBeTrue();

    [Fact]
    public void HasNoProblem() => _availability.Problem.ShouldBeNull();

    [Fact]
    public void AsksYtDlpForItsVersionIgnoringConfiguration() =>
        _runner
            .Calls.Single(c => c.FileName == "yt-dlp")
            .Arguments.ShouldBe(YtDlpArguments.ForVersion());

    [Fact]
    public void AsksDenoOnThePathForItsVersion() =>
        _runner
            .Calls.Single(c => c.FileName == "deno")
            .Arguments.ShouldBe(YtDlpArguments.ForDenoVersion());

    [Fact]
    public void DownloadsNothing() => _runner.Calls.Count.ShouldBe(2);
}

public class WhenYtDlpIsMissing
{
    private readonly WebVideoAvailability _availability;

    public WhenYtDlpIsMissing()
    {
        var runner = new FakeProcessRunner().On("deno", 0, YtDlpSamples.DenoVersionOutput);

        _availability = new YtDlpAudioSource(runner, new SourcesOptions())
            .CheckAvailabilityAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void IsNotAvailable() => _availability.IsAvailable.ShouldBeFalse();

    [Fact]
    public void DidNotFindYtDlp() => _availability.YtDlpFound.ShouldBeFalse();

    [Fact]
    public void HasNoYtDlpVersion() => _availability.YtDlpVersion.ShouldBeNull();

    [Fact]
    public void StillReportsDeno() => _availability.DenoVersion.ShouldBe("2.9.6");

    [Fact]
    public void SaysYtDlpIsTheProblem() =>
        _availability.Problem.ShouldNotBeNull().ShouldContain("yt-dlp");
}

/// <summary>
/// yt-dlp itself runs, but it only warns without a JavaScript runtime and
/// YouTube may then hide formats (ADR-0007), so web video is reported
/// unavailable up front rather than failing later.
/// </summary>
public class WhenDenoIsMissing
{
    private readonly WebVideoAvailability _availability;

    public WhenDenoIsMissing()
    {
        var runner = new FakeProcessRunner().On("yt-dlp", 0, YtDlpSamples.YtDlpVersionOutput);

        _availability = new YtDlpAudioSource(runner, new SourcesOptions())
            .CheckAvailabilityAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void IsNotAvailable() => _availability.IsAvailable.ShouldBeFalse();

    [Fact]
    public void FoundYtDlp() => _availability.YtDlpFound.ShouldBeTrue();

    [Fact]
    public void DidNotFindAJavaScriptRuntime() => _availability.JsRuntimeFound.ShouldBeFalse();

    [Fact]
    public void SaysDenoIsTheProblem() =>
        _availability.Problem.ShouldNotBeNull().ShouldContain("Deno");
}

public class WhenNeitherIsInstalled
{
    private readonly WebVideoAvailability _availability = new YtDlpAudioSource(
        new FakeProcessRunner(),
        new SourcesOptions()
    )
        .CheckAvailabilityAsync()
        .GetAwaiter()
        .GetResult();

    [Fact]
    public void IsNotAvailable() => _availability.IsAvailable.ShouldBeFalse();

    [Fact]
    public void NamesYtDlpFirst() =>
        _availability.Problem.ShouldNotBeNull().ShouldContain("yt-dlp");
}

public class WhenAProbedExecutableMisbehaves
{
    [Theory]
    [InlineData(1, "2026.08.19\n")]
    [InlineData(0, "")]
    [InlineData(0, "\n")]
    public void YtDlpIsNotFoundUnlessItAnswersCleanly(int exitCode, string output) =>
        Probe(
            new FakeProcessRunner()
                .On("yt-dlp", exitCode, output)
                .On("deno", 0, YtDlpSamples.DenoVersionOutput)
        )
            .YtDlpFound.ShouldBeFalse();

    [Theory]
    [InlineData(1, "deno 2.9.6\n")]
    [InlineData(0, "")]
    [InlineData(0, "something else entirely\n")]
    public void DenoIsNotFoundUnlessItAnswersLikeDeno(int exitCode, string output) =>
        Probe(
            new FakeProcessRunner()
                .On("yt-dlp", 0, YtDlpSamples.YtDlpVersionOutput)
                .On("deno", exitCode, output)
        )
            .JsRuntimeFound.ShouldBeFalse();

    private static WebVideoAvailability Probe(FakeProcessRunner runner) =>
        new YtDlpAudioSource(runner, new SourcesOptions())
            .CheckAvailabilityAsync()
            .GetAwaiter()
            .GetResult();
}

public class WhenTheProbePathsAreConfigured
{
    private const string YtDlp = @"C:\Tools\yt-dlp.exe";
    private const string Deno = @"C:\Tools\deno.exe";

    private readonly FakeProcessRunner _runner = new();
    private readonly WebVideoAvailability _availability;

    public WhenTheProbePathsAreConfigured()
    {
        _runner
            .On(YtDlp, 0, YtDlpSamples.YtDlpVersionOutput)
            .On(Deno, 0, YtDlpSamples.DenoVersionOutput);

        _availability = new YtDlpAudioSource(
            _runner,
            new SourcesOptions { YtDlpPath = YtDlp, DenoPath = Deno }
        )
            .CheckAvailabilityAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void ProbesTheConfiguredExecutables() =>
        _runner.Calls.Select(c => c.FileName).Order(StringComparer.Ordinal).ShouldBe([Deno, YtDlp]);

    [Fact]
    public void IsAvailable() => _availability.IsAvailable.ShouldBeTrue();
}

/// <summary>
/// The probe backs <c>GET /config</c>, so a wedged executable must not hang it:
/// past the probe timeout the executable counts as not found. The caller's own
/// cancellation is different and still propagates.
/// </summary>
public class WhenAProbeHangs
{
    private readonly WebVideoAvailability _availability;

    public WhenAProbeHangs()
    {
        var runner = new FakeProcessRunner()
            .On(
                "yt-dlp",
                async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("unreachable");
                }
            )
            .On("deno", 0, YtDlpSamples.DenoVersionOutput);

        _availability = new YtDlpAudioSource(
            runner,
            new SourcesOptions(),
            TimeSpan.FromMilliseconds(50)
        )
            .CheckAvailabilityAsync()
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void CountsItAsNotFound() => _availability.YtDlpFound.ShouldBeFalse();

    [Fact]
    public void StillReportsTheOther() => _availability.JsRuntimeFound.ShouldBeTrue();

    [Fact]
    public void PropagatesTheCallersCancellation() =>
        Should.Throw<OperationCanceledException>(() =>
            new YtDlpAudioSource(new FakeProcessRunner(), new SourcesOptions())
                .CheckAvailabilityAsync(new CancellationToken(canceled: true))
                .GetAwaiter()
                .GetResult()
        );
}

public class WhenTheCallerCancelsWhileAProbeRuns
{
    [Fact]
    public void PropagatesRatherThanReportingTheToolMissing()
    {
        var runner = new FakeProcessRunner()
            .On(
                "yt-dlp",
                async (_, token) =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("unreachable");
                }
            )
            .On("deno", 0, YtDlpSamples.DenoVersionOutput);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Should.Throw<OperationCanceledException>(() =>
            new YtDlpAudioSource(runner, new SourcesOptions(), TimeSpan.FromMinutes(5))
                .CheckAvailabilityAsync(cancellation.Token)
                .GetAwaiter()
                .GetResult()
        );
    }
}

public class WhenTheProbeTimeoutIsNotPositive
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IsRejected(int milliseconds) =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new YtDlpAudioSource(
                new FakeProcessRunner(),
                new SourcesOptions(),
                TimeSpan.FromMilliseconds(milliseconds)
            )
        );
}

public class WhenReadingAnAvailabilityRecord
{
    [Theory]
    [InlineData("2026.08.19", "2.9.6", true)]
    [InlineData(null, "2.9.6", false)]
    [InlineData("2026.08.19", null, false)]
    [InlineData(null, null, false)]
    public void IsAvailableOnlyWithBoth(string? ytDlp, string? deno, bool available) =>
        new WebVideoAvailability(ytDlp, deno).IsAvailable.ShouldBe(available);

    [Fact]
    public void HasAProblemExactlyWhenUnavailable() =>
        new WebVideoAvailability(null, null).Problem.ShouldNotBeNullOrWhiteSpace();
}

public class WhenNoSourcesOptionsAreConfigured
{
    private readonly SourcesOptions _options = new();

    [Fact]
    public void BindsFromTheSourcesSection() => SourcesOptions.SectionName.ShouldBe("Sources");

    [Fact]
    public void LooksForYtDlpOnThePath() => _options.YtDlpPath.ShouldBe("yt-dlp");

    [Fact]
    public void LeavesDenoToThePath() => _options.DenoPath.ShouldBeNull();
}
