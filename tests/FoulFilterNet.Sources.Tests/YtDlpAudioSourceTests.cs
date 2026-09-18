using FoulFilterNet.Media;

namespace FoulFilterNet.Sources.Tests;

public sealed class WhenTheAudioIsFetched : IDisposable
{
    private static readonly VideoRef Video = VideoRef.Create("youtube", "jNQXAC9IVRw");

    private readonly ScratchDirectory _scratch = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly WebAudio _audio;

    public WhenTheAudioIsFetched()
    {
        _runner.On("yt-dlp", FakeYtDlp.Downloads());
        var sut = new YtDlpAudioSource(_runner, new SourcesOptions());

        _audio = sut.FetchAudioAsync(Video, _scratch.Path, _cancellation.Token)
            .GetAwaiter()
            .GetResult();

        _audio.ShouldNotBeNull();
        _runner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public void RunsYtDlpFromThePathByDefault() => _runner.Calls[0].FileName.ShouldBe("yt-dlp");

    [Fact]
    public void RunsExactlyTheFetchArguments() =>
        _runner.Calls[0].Arguments.ShouldBe(YtDlpArguments.ForFetch(Video, _scratch.Path));

    [Fact]
    public void PassesTheCallersTokenToTheProcess() =>
        _runner.LastToken.ShouldBe(_cancellation.Token);

    [Fact]
    public void KeepsTheVideo() => _audio.Video.ShouldBe(Video);

    [Fact]
    public void ReportsTheTitle() => _audio.Title.ShouldBe("Me at the zoo");

    [Fact]
    public void ReportsTheDuration() => _audio.DurationSeconds.ShouldBe(19);

    [Fact]
    public void ReportsWhereTheAudioIs() => _audio.AudioPath.ShouldBe(_scratch.File("audio.webm"));

    [Fact]
    public void ReportsTheExtension() => _audio.Extension.ShouldBe("webm");

    [Fact]
    public void ReportsTheFormat() => _audio.FormatId.ShouldBe("251");

    [Fact]
    public void ReportsTheCodec() => _audio.Codec.ShouldBe("opus");

    [Fact]
    public void HadAJavaScriptRuntime() => _audio.JsRuntimeMissing.ShouldBeFalse();

    [Fact]
    public void LeavesOnlyTheAudioInTheDirectory() => _scratch.FileNames().ShouldBe(["audio.webm"]);

    public void Dispose()
    {
        _cancellation.Dispose();
        _scratch.Dispose();
    }
}

public sealed class WhenTheTitleIsNotAscii : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly WebAudio _audio;

    public WhenTheTitleIsNotAscii()
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            FakeYtDlp.Downloads(YtDlpSamples.UnicodeTitleOutput)
        );

        _audio = new YtDlpAudioSource(runner, new SourcesOptions())
            .FetchAudioAsync(VideoRef.Create("youtube", "9bZkp7q19f0"), _scratch.Path)
            .GetAwaiter()
            .GetResult();

        _audio.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsItDecoded() => _audio.Title.ShouldBe(YtDlpSamples.UnicodeTitle);

    [Fact]
    public void ReportsTheDuration() => _audio.DurationSeconds.ShouldBe(252);

    public void Dispose() => _scratch.Dispose();
}

public sealed class WhenTheDurationIsMissing : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly WebAudio _audio;

    public WhenTheDurationIsMissing()
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            FakeYtDlp.Downloads(
                """
                FFN-INFO {"id": "jNQXAC9IVRw", "title": "Me at the zoo", "live_status": "not_live"}
                FFN-FILE {"filepath": "{AUDIO_PATH}", "ext": "webm", "format_id": "251", "acodec": "opus"}

                """
            )
        );

        _audio = new YtDlpAudioSource(runner, new SourcesOptions())
            .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _scratch.Path)
            .GetAwaiter()
            .GetResult();

        _audio.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsNoDurationRatherThanZero() => _audio.DurationSeconds.ShouldBeNull();

    [Fact]
    public void StillReportsTheAudio() => _audio.AudioPath.ShouldBe(_scratch.File("audio.webm"));

    public void Dispose() => _scratch.Dispose();
}

public sealed class WhenThePathsAreConfigured : IDisposable
{
    private const string YtDlp = @"C:\Tools\yt-dlp\yt-dlp.exe";
    private const string Deno = @"C:\Tools\deno\deno.exe";

    private static readonly VideoRef Video = VideoRef.Create("youtube", "jNQXAC9IVRw");

    private readonly ScratchDirectory _scratch = new();
    private readonly FakeProcessRunner _runner = new();

    public WhenThePathsAreConfigured()
    {
        _runner.On(YtDlp, FakeYtDlp.Downloads());

        new YtDlpAudioSource(_runner, new SourcesOptions { YtDlpPath = YtDlp, DenoPath = Deno })
            .FetchAudioAsync(Video, _scratch.Path)
            .GetAwaiter()
            .GetResult()
            .ShouldNotBeNull();
    }

    [Fact]
    public void RunsTheConfiguredYtDlp() => _runner.Calls[0].FileName.ShouldBe(YtDlp);

    [Fact]
    public void HandsYtDlpTheConfiguredDeno() =>
        _runner.Calls[0].Arguments.ShouldBe(YtDlpArguments.ForFetch(Video, _scratch.Path, Deno));

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// Without Deno, current yt-dlp still extracts YouTube and may still download
/// (it did in W01 and here), so this is a success with a flag, not a failure:
/// the audio is in hand. The flag lets the caller warn that formats may go
/// missing next time.
/// </summary>
public sealed class WhenFetchedWithoutAJavaScriptRuntime : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly WebAudio _audio;

    public WhenFetchedWithoutAJavaScriptRuntime()
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            FakeYtDlp.Downloads(standardError: YtDlpSamples.MissingJsRuntimeWarning)
        );

        _audio = new YtDlpAudioSource(runner, new SourcesOptions())
            .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _scratch.Path)
            .GetAwaiter()
            .GetResult();

        _audio.ShouldNotBeNull();
    }

    [Fact]
    public void StillSucceeds() => _audio.AudioPath.ShouldBe(_scratch.File("audio.webm"));

    [Fact]
    public void SaysTheRuntimeWasMissing() => _audio.JsRuntimeMissing.ShouldBeTrue();

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// The captured livestream run: the match filter skipped it, so yt-dlp exited
/// 0, printed the info line and no file line, and wrote nothing to standard
/// error. A clean exit is not a success.
/// </summary>
public sealed class WhenTheVideoIsLive : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly WebAudioException _thrown;

    public WhenTheVideoIsLive()
    {
        var runner = new FakeProcessRunner().On("yt-dlp", 0, YtDlpSamples.LiveSkippedOutput);

        _thrown = Should.Throw<WebAudioException>(() =>
            new YtDlpAudioSource(runner, new SourcesOptions())
                .FetchAudioAsync(VideoRef.Create("youtube", "3PFJ9SETS4M"), _scratch.Path)
                .GetAwaiter()
                .GetResult()
        );

        _thrown.ShouldNotBeNull();
    }

    [Fact]
    public void IsUnsupported() => _thrown.Kind.ShouldBe(WebAudioFailure.Unsupported);

    [Fact]
    public void SaysItIsALivestream() => _thrown.Reason.ShouldContain("live", Case.Insensitive);

    [Fact]
    public void CarriesTheCleanExitCode() => _thrown.ExitCode.ShouldBe(0);

    [Fact]
    public void NamesTheVideoInTheMessage() => _thrown.Message.ShouldContain("youtube-3PFJ9SETS4M");

    [Fact]
    public void KnowsTheVideo() =>
        _thrown.Video.ShouldBe(VideoRef.Create("youtube", "3PFJ9SETS4M"));

    [Fact]
    public void LeavesTheDirectoryEmpty() => _scratch.FileNames().ShouldBeEmpty();

    public void Dispose() => _scratch.Dispose();
}

public sealed class WhenAnUpcomingVideoSlipsPastTheFilter : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly WebAudioException _thrown;

    public WhenAnUpcomingVideoSlipsPastTheFilter()
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            0,
            """
            FFN-INFO {"id": "v03RjDNwG1o", "title": "Launch", "live_status": "is_upcoming"}

            """
        );

        _thrown = Should.Throw<WebAudioException>(() =>
            new YtDlpAudioSource(runner, new SourcesOptions())
                .FetchAudioAsync(VideoRef.Create("youtube", "v03RjDNwG1o"), _scratch.Path)
                .GetAwaiter()
                .GetResult()
        );
    }

    [Fact]
    public void IsUnsupported() => _thrown.Kind.ShouldBe(WebAudioFailure.Unsupported);

    public void Dispose() => _scratch.Dispose();
}

public sealed class WhenYtDlpIsNotInstalled : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly WebAudioException _thrown;

    public WhenYtDlpIsNotInstalled()
    {
        _thrown = Should.Throw<WebAudioException>(() =>
            new YtDlpAudioSource(
                new FakeProcessRunner(),
                new SourcesOptions { YtDlpPath = "yt-dlp-nowhere" }
            )
                .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _scratch.Path)
                .GetAwaiter()
                .GetResult()
        );

        _thrown.ShouldNotBeNull();
    }

    [Fact]
    public void IsNotInstalled() => _thrown.Kind.ShouldBe(WebAudioFailure.NotInstalled);

    [Fact]
    public void NamesTheExecutableItLookedFor() => _thrown.Reason.ShouldContain("yt-dlp-nowhere");

    [Fact]
    public void KeepsTheCause() =>
        _thrown.InnerException.ShouldBeOfType<ProcessNotStartedException>();

    [Fact]
    public void HasNoExitCode() => _thrown.ExitCode.ShouldBe(-1);

    [Fact]
    public void IsNotUnsupported() => _thrown.IsUnsupported.ShouldBeFalse();

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// Every captured failure, run through the whole source: the kind, yt-dlp's own
/// sentence as the reason, and no partial download left behind.
/// </summary>
public sealed class WhenYtDlpFails : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    public static TheoryData<string, WebAudioFailure, string> Samples =>
        new()
        {
            {
                YtDlpSamples.NonexistentVideoError,
                WebAudioFailure.Unavailable,
                "This video is unavailable"
            },
            {
                YtDlpSamples.LiveRecordingGoneError,
                WebAudioFailure.Unavailable,
                "This live stream recording is not available."
            },
            {
                YtDlpSamples.PrivateVideoError,
                WebAudioFailure.Unavailable,
                "Private video. Sign in if you've been granted access to this video. Use --cookies-from-browser or --cookies for the authentication."
            },
            {
                YtDlpSamples.UpcomingError,
                WebAudioFailure.Unsupported,
                "This live event will begin in 27 hours."
            },
            {
                YtDlpSamples.AgeRestrictedError,
                WebAudioFailure.SignInRequired,
                "Sign in to confirm your age."
            },
            {
                YtDlpSamples.ConnectionRefusedError,
                WebAudioFailure.Network,
                "Unable to download API page:"
            },
            {
                YtDlpSamples.NameResolutionError,
                WebAudioFailure.Network,
                "Unable to download API page:"
            },
            {
                YtDlpSamples.NoFormatError,
                WebAudioFailure.Failed,
                "Requested format is not available."
            },
        };

    [Theory]
    [MemberData(nameof(Samples))]
    public void MapsToItsKind(string standardError, WebAudioFailure kind, string reason)
    {
        _ = reason;
        Fetch(1, standardError).Kind.ShouldBe(kind);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void GivesYtDlpsReason(string standardError, WebAudioFailure kind, string reason)
    {
        _ = kind;
        Fetch(1, standardError).Reason.ShouldStartWith(reason);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void CarriesTheExitCode(string standardError, WebAudioFailure kind, string reason)
    {
        _ = (kind, reason);
        Fetch(1, standardError).ExitCode.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void CarriesAllOfStandardError(string standardError, WebAudioFailure kind, string reason)
    {
        _ = (kind, reason);
        Fetch(1, standardError).StandardError.ShouldBe(standardError);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void PutsTheReasonInTheMessage(string standardError, WebAudioFailure kind, string reason)
    {
        _ = kind;
        Fetch(1, standardError).Message.ShouldContain(reason);
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void DeletesThePartialDownload(string standardError, WebAudioFailure kind, string reason)
    {
        _ = (kind, reason);
        Fetch(1, standardError);
        _scratch.FileNames().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(-1)]
    public void TreatsAnyNonZeroExitAsAFailure(int exitCode) =>
        Fetch(exitCode, YtDlpSamples.NonexistentVideoError)
            .Kind.ShouldBe(WebAudioFailure.Unavailable);

    [Fact]
    public void ReportsAUsageErrorAsAFailure() =>
        Fetch(2, YtDlpSamples.UsageError).Kind.ShouldBe(WebAudioFailure.Failed);

    [Fact]
    public void SuggestsUpdatingWhenItRejectsItsArguments() =>
        Fetch(2, YtDlpSamples.UsageError).Message.ShouldContain("update", Case.Insensitive);

    [Fact]
    public void GivesTheExitCodeWhenStandardErrorIsEmpty() =>
        Fetch(1, "").Reason.ShouldContain("exit code 1", Case.Insensitive);

    [Fact]
    public void FailsEvenIfItPrintedAFileBeforeExitingNonZero() =>
        Fetch(
            1,
            YtDlpSamples.NonexistentVideoError,
            YtDlpSamples.WithAudioPath(YtDlpSamples.SuccessOutput, _scratch.File("audio.webm"))
        )
            .Kind.ShouldBe(WebAudioFailure.Unavailable);

    [Fact]
    public void IsUnsupportedOnlyForUnsupported() =>
        Fetch(1, YtDlpSamples.UpcomingError).IsUnsupported.ShouldBeTrue();

    private WebAudioException Fetch(int exitCode, string standardError, string standardOutput = "")
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            FakeYtDlp.FailsAfterAPartialFile(exitCode, standardError, standardOutput)
        );

        return Should.Throw<WebAudioException>(() =>
            new YtDlpAudioSource(runner, new SourcesOptions())
                .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _scratch.Path)
                .GetAwaiter()
                .GetResult()
        );
    }

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// Without Deno, YouTube can hide the formats and <c>bestaudio</c> finds none.
/// The generic "Requested format is not available" is then really a missing
/// runtime, and saying so is the only useful advice.
/// </summary>
public sealed class WhenNoFormatIsFoundWithoutAJavaScriptRuntime : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly WebAudioException _thrown;

    public WhenNoFormatIsFoundWithoutAJavaScriptRuntime()
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            1,
            standardError: YtDlpSamples.MissingJsRuntimeWarning + YtDlpSamples.NoFormatError
        );

        _thrown = Should.Throw<WebAudioException>(() =>
            new YtDlpAudioSource(runner, new SourcesOptions())
                .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _scratch.Path)
                .GetAwaiter()
                .GetResult()
        );
    }

    [Fact]
    public void BlamesTheMissingRuntime() =>
        _thrown.Kind.ShouldBe(WebAudioFailure.JsRuntimeMissing);

    [Fact]
    public void StillGivesYtDlpsReason() =>
        _thrown.Reason.ShouldStartWith("Requested format is not available.");

    [Fact]
    public void MentionsDenoInTheMessage() => _thrown.Message.ShouldContain("Deno");

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// A specific diagnosis beats the runtime warning: a nonexistent video is
/// unavailable whether or not Deno was there.
/// </summary>
public sealed class WhenAKnownFailureHappensWithoutAJavaScriptRuntime : IDisposable
{
    private readonly ScratchDirectory _scratch = new();

    [Fact]
    public void KeepsTheSpecificKind()
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            1,
            standardError: YtDlpSamples.MissingJsRuntimeWarning + YtDlpSamples.NonexistentVideoError
        );

        Should
            .Throw<WebAudioException>(() =>
                new YtDlpAudioSource(runner, new SourcesOptions())
                    .FetchAudioAsync(VideoRef.Create("youtube", "aaaaaaaaaaa"), _scratch.Path)
                    .GetAwaiter()
                    .GetResult()
            )
            .Kind.ShouldBe(WebAudioFailure.Unavailable);
    }

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// Exit code 0 is necessary, not sufficient: the source trusts the file only
/// when yt-dlp reported one, it is where we asked for it, and it is on disk.
/// </summary>
public sealed class WhenACleanExitDoesNotDeliverTheAudio : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly ScratchDirectory _elsewhere = new();

    [Fact]
    public void FailsWhenNothingWasPrinted() => Fetch(0, "").Kind.ShouldBe(WebAudioFailure.Failed);

    [Fact]
    public void FailsWhenOnlyTheInfoWasPrintedForAVideoThatIsNotLive() =>
        Fetch(
            0,
            """
            FFN-INFO {"id": "jNQXAC9IVRw", "title": "Me at the zoo", "duration": 19, "live_status": "not_live"}

            """
        )
            .Kind.ShouldBe(WebAudioFailure.Failed);

    [Fact]
    public void FailsWhenTheReportedFileIsNotThere() =>
        Fetch(
            0,
            YtDlpSamples.WithAudioPath(YtDlpSamples.SuccessOutput, _scratch.File("audio.webm"))
        )
            .Kind.ShouldBe(WebAudioFailure.Failed);

    [Fact]
    public void FailsWhenTheReportedFileIsOutsideTheDirectory()
    {
        var outside = _elsewhere.File("audio.webm");
        File.WriteAllBytes(outside, [1]);

        Fetch(0, YtDlpSamples.WithAudioPath(YtDlpSamples.SuccessOutput, outside))
            .Kind.ShouldBe(WebAudioFailure.Failed);
    }

    [Fact]
    public void LeavesAFileOutsideTheDirectoryAlone()
    {
        var outside = _elsewhere.File("audio.webm");
        File.WriteAllBytes(outside, [1]);

        Fetch(0, YtDlpSamples.WithAudioPath(YtDlpSamples.SuccessOutput, outside));

        File.Exists(outside).ShouldBeTrue();
    }

    [Fact]
    public void FailsWhenTheFileIsReportedWithoutTheInfo()
    {
        var path = _scratch.File("audio.webm");
        File.WriteAllBytes(path, [1]);

        Fetch(
            0,
            YtDlpSamples.WithAudioPath(
                """
                FFN-FILE {"filepath": "{AUDIO_PATH}", "ext": "webm", "format_id": "251", "acodec": "opus"}

                """,
                path
            )
        )
            .Kind.ShouldBe(WebAudioFailure.Failed);
    }

    [Fact]
    public void CleansUpAfterItself()
    {
        Fetch(0, "");
        _scratch.FileNames().ShouldBeEmpty();
    }

    private WebAudioException Fetch(int exitCode, string standardOutput)
    {
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            FakeYtDlp.FailsAfterAPartialFile(exitCode, string.Empty, standardOutput)
        );

        return Should.Throw<WebAudioException>(() =>
            new YtDlpAudioSource(runner, new SourcesOptions())
                .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _scratch.Path)
                .GetAwaiter()
                .GetResult()
        );
    }

    public void Dispose()
    {
        _scratch.Dispose();
        _elsewhere.Dispose();
    }
}

/// <summary>
/// A Watch Session is cancelled when nobody is watching any more. The real
/// runner kills yt-dlp's process tree and throws; the source must then remove
/// what the download left behind, and nothing else.
/// </summary>
public sealed class WhenTheFetchIsCancelled : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly Exception? _thrown;

    public WhenTheFetchIsCancelled()
    {
        File.WriteAllText(_scratch.File("notes.txt"), "not ours");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeProcessRunner().On("yt-dlp", FakeYtDlp.RunsUntilCancelled(started));
        using var cancellation = new CancellationTokenSource();

        var fetch = new YtDlpAudioSource(runner, new SourcesOptions()).FetchAudioAsync(
            VideoRef.Create("youtube", "jNQXAC9IVRw"),
            _scratch.Path,
            cancellation.Token
        );
        started.Task.GetAwaiter().GetResult();
        cancellation.Cancel();

        _thrown = Record.Exception(() => fetch.GetAwaiter().GetResult());
    }

    [Fact]
    public void ThrowsOperationCanceled() =>
        _thrown.ShouldBeAssignableTo<OperationCanceledException>();

    [Fact]
    public void DoesNotDisguiseItAsAFailure() => _thrown.ShouldNotBeOfType<WebAudioException>();

    [Fact]
    public void DeletesThePartialFiles() =>
        _scratch
            .FileNames()
            .ShouldNotContain(n => n.StartsWith("audio.", StringComparison.Ordinal));

    [Fact]
    public void LeavesOtherFilesAlone() => _scratch.FileNames().ShouldBe(["notes.txt"]);

    public void Dispose() => _scratch.Dispose();
}

public sealed class WhenCancelledBeforeTheFetchStarts : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly FakeProcessRunner _runner = new();

    [Fact]
    public void ThrowsWithoutRunningYtDlp()
    {
        _runner.On("yt-dlp", FakeYtDlp.Downloads());

        Should.Throw<OperationCanceledException>(() =>
            new YtDlpAudioSource(_runner, new SourcesOptions())
                .FetchAudioAsync(
                    VideoRef.Create("youtube", "jNQXAC9IVRw"),
                    _scratch.Path,
                    new CancellationToken(canceled: true)
                )
                .GetAwaiter()
                .GetResult()
        );

        _runner.Calls.ShouldBeEmpty();
    }

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// The directory is the source's to write into. A leftover <c>audio.*</c> from
/// an earlier, interrupted fetch would make yt-dlp say "has already been
/// downloaded" and hand back a file that may be half there, so it goes first.
/// </summary>
public sealed class WhenTheDirectoryHasLeftovers : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly List<string> _presentWhenYtDlpStarted = [];

    public WhenTheDirectoryHasLeftovers()
    {
        File.WriteAllBytes(_scratch.File("audio.webm"), [9]);
        File.WriteAllBytes(_scratch.File("audio.m4a.part"), [9]);
        File.WriteAllText(_scratch.File("analysis.wav"), "keep");
        File.WriteAllText(_scratch.File("audiobook.mp3"), "keep");

        var download = FakeYtDlp.Downloads();
        var runner = new FakeProcessRunner().On(
            "yt-dlp",
            (arguments, token) =>
            {
                _presentWhenYtDlpStarted.AddRange(_scratch.FileNames());
                return download(arguments, token);
            }
        );

        new YtDlpAudioSource(runner, new SourcesOptions())
            .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _scratch.Path)
            .GetAwaiter()
            .GetResult()
            .ShouldNotBeNull();
    }

    [Fact]
    public void RemovesThemBeforeYtDlpRuns() =>
        _presentWhenYtDlpStarted.ShouldBe(["analysis.wav", "audiobook.mp3"]);

    [Fact]
    public void KeepsFilesThatAreNotItsOwn() =>
        _scratch.FileNames().ShouldBe(["analysis.wav", "audio.webm", "audiobook.mp3"]);

    public void Dispose() => _scratch.Dispose();
}

public sealed class WhenTheDirectoryDoesNotExistYet : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly string _directory;
    private readonly WebAudio _audio;

    public WhenTheDirectoryDoesNotExistYet()
    {
        _directory = Path.Combine(_scratch.Path, "session", "youtube-jNQXAC9IVRw");
        var runner = new FakeProcessRunner().On("yt-dlp", FakeYtDlp.Downloads());

        _audio = new YtDlpAudioSource(runner, new SourcesOptions())
            .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _directory)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void CreatesIt() => Directory.Exists(_directory).ShouldBeTrue();

    [Fact]
    public void DownloadsIntoIt() =>
        _audio.AudioPath.ShouldBe(Path.Combine(_directory, "audio.webm"));

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// A relative directory would be resolved by yt-dlp against its own working
/// directory; the source makes it absolute first so both agree.
/// </summary>
public sealed class WhenTheDirectoryIsRelative : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly string _relative;

    public WhenTheDirectoryIsRelative()
    {
        _relative = Path.GetRelativePath(Environment.CurrentDirectory, _scratch.Path);
        _runner.On("yt-dlp", FakeYtDlp.Downloads());

        new YtDlpAudioSource(_runner, new SourcesOptions())
            .FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), _relative)
            .GetAwaiter()
            .GetResult()
            .ShouldNotBeNull();
    }

    [Fact]
    public void HandsYtDlpTheFullPath() =>
        FakeYtDlp.DirectoryOf(_runner.Calls[0].Arguments).ShouldBe(_scratch.Path);

    public void Dispose() => _scratch.Dispose();
}

public class WhenTheFetchIsGivenBadArguments
{
    private readonly YtDlpAudioSource _sut = new(new FakeProcessRunner(), new SourcesOptions());

    [Fact]
    public void RejectsANullVideo() =>
        Should.Throw<ArgumentNullException>(() =>
            _sut.FetchAudioAsync(null!, "scratch").GetAwaiter().GetResult()
        );

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RejectsABlankDirectory(string directory) =>
        Should.Throw<ArgumentException>(() =>
            _sut.FetchAudioAsync(VideoRef.Create("youtube", "jNQXAC9IVRw"), directory)
                .GetAwaiter()
                .GetResult()
        );

    [Fact]
    public void RejectsANullRunner() =>
        Should.Throw<ArgumentNullException>(() =>
            new YtDlpAudioSource(null!, new SourcesOptions())
        );

    [Fact]
    public void RejectsNullOptions() =>
        Should.Throw<ArgumentNullException>(() =>
            new YtDlpAudioSource(new FakeProcessRunner(), null!)
        );

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsABlankYtDlpPath(string path) =>
        Should.Throw<ArgumentException>(() =>
            new YtDlpAudioSource(new FakeProcessRunner(), new SourcesOptions { YtDlpPath = path })
        );
}
