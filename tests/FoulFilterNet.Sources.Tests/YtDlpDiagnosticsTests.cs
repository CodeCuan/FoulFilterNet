namespace FoulFilterNet.Sources.Tests;

/// <summary>
/// yt-dlp exits 1 for nearly everything, so standard error is the only thing
/// that says what went wrong. Every sample here is real output (see
/// <see cref="YtDlpSamples"/>), apart from the few marked as written by hand.
/// </summary>
public class WhenClassifyingAFailedRun
{
    public static TheoryData<string, WebAudioFailure> Samples =>
        new()
        {
            { YtDlpSamples.NonexistentVideoError, WebAudioFailure.Unavailable },
            { YtDlpSamples.LiveRecordingGoneError, WebAudioFailure.Unavailable },
            { YtDlpSamples.PrivateVideoError, WebAudioFailure.Unavailable },
            { YtDlpSamples.TerminatedAccountError, WebAudioFailure.Unavailable },
            { YtDlpSamples.RemovedByUploaderError, WebAudioFailure.Unavailable },
            { YtDlpSamples.GeoBlockedError, WebAudioFailure.Unavailable },
            { YtDlpSamples.TruncatedIdError, WebAudioFailure.Unavailable },
            { YtDlpSamples.AgeRestrictedError, WebAudioFailure.SignInRequired },
            { YtDlpSamples.BotCheckError, WebAudioFailure.SignInRequired },
            { YtDlpSamples.MembersOnlyError, WebAudioFailure.SignInRequired },
            { YtDlpSamples.UpcomingError, WebAudioFailure.Unsupported },
            { YtDlpSamples.PremiereError, WebAudioFailure.Unsupported },
            { YtDlpSamples.ConnectionRefusedError, WebAudioFailure.Network },
            { YtDlpSamples.NameResolutionError, WebAudioFailure.Network },
            { YtDlpSamples.DownloadTimedOutError, WebAudioFailure.Network },
            { YtDlpSamples.NoFormatError, WebAudioFailure.Failed },
            { YtDlpSamples.UsageError, WebAudioFailure.Failed },
            { "", WebAudioFailure.Failed },
            { "something nobody has seen before\n", WebAudioFailure.Failed },
        };

    [Theory]
    [MemberData(nameof(Samples))]
    public void MapsTheSampleToItsKind(string standardError, WebAudioFailure kind) =>
        YtDlpDiagnostics.Classify(standardError).ShouldBe(kind);

    [Theory]
    [MemberData(nameof(Samples))]
    public void IgnoresTheCaseOfTheMessage(string standardError, WebAudioFailure kind) =>
        YtDlpDiagnostics.Classify(standardError.ToUpperInvariant()).ShouldBe(kind);

    [Theory]
    [MemberData(nameof(Samples))]
    public void IgnoresWindowsLineEndings(string standardError, WebAudioFailure kind) =>
        YtDlpDiagnostics.Classify(standardError.ReplaceLineEndings("\r\n")).ShouldBe(kind);

    [Fact]
    public void RejectsNull() =>
        Should.Throw<ArgumentNullException>(() => YtDlpDiagnostics.Classify(null!));
}

/// <summary>
/// The error line decides, not the warnings before it: a warning that retried
/// its way past a network blip must not turn an unavailable video into a
/// network failure.
/// </summary>
public class WhenWarningsAndTheErrorDisagree
{
    [Fact]
    public void TheErrorLineWins() =>
        YtDlpDiagnostics
            .Classify(
                "WARNING: [youtube] x: Unable to download webpage: timed out. Retrying (1/3)...\n"
                    + YtDlpSamples.NonexistentVideoError
            )
            .ShouldBe(WebAudioFailure.Unavailable);

    [Fact]
    public void AMissingRuntimeWarningAloneIsNotAClassification() =>
        YtDlpDiagnostics
            .Classify(YtDlpSamples.MissingJsRuntimeWarning)
            .ShouldBe(WebAudioFailure.Failed);

    [Fact]
    public void WarningsAreReadWhenThereIsNoErrorLine() =>
        YtDlpDiagnostics
            .Classify(
                "WARNING: [youtube] x: Unable to download webpage: The read operation timed out\n"
            )
            .ShouldBe(WebAudioFailure.Network);
}

/// <summary>
/// "Private video. Sign in if you've been granted access" mentions signing in,
/// but the viewer cannot fix it by signing in to their own account: it is
/// unavailable, and has to be checked before the sign-in rules.
/// </summary>
public class WhenAMessageMatchesMoreThanOneRule
{
    [Fact]
    public void APrivateVideoIsUnavailableNotSignInRequired() =>
        YtDlpDiagnostics
            .Classify(YtDlpSamples.PrivateVideoError)
            .ShouldBe(WebAudioFailure.Unavailable);

    [Fact]
    public void AGoneLiveRecordingIsUnavailableNotUnsupported() =>
        YtDlpDiagnostics
            .Classify(YtDlpSamples.LiveRecordingGoneError)
            .ShouldBe(WebAudioFailure.Unavailable);

    [Fact]
    public void ARequestedFormatThatIsNotAvailableIsNotAnUnavailableVideo() =>
        YtDlpDiagnostics.Classify(YtDlpSamples.NoFormatError).ShouldBe(WebAudioFailure.Failed);
}

public class WhenLookingForTheMissingRuntimeWarning
{
    [Fact]
    public void FindsItInTheRealWarning() =>
        YtDlpDiagnostics
            .ReportsMissingJsRuntime(YtDlpSamples.MissingJsRuntimeWarning)
            .ShouldBeTrue();

    [Fact]
    public void FindsItAmongOtherLines() =>
        YtDlpDiagnostics
            .ReportsMissingJsRuntime(
                YtDlpSamples.MissingJsRuntimeWarning + YtDlpSamples.NoFormatError
            )
            .ShouldBeTrue();

    [Fact]
    public void FindsItWithWindowsLineEndings() =>
        YtDlpDiagnostics
            .ReportsMissingJsRuntime(
                YtDlpSamples.MissingJsRuntimeWarning.ReplaceLineEndings("\r\n")
            )
            .ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(YtDlpSamples.NonexistentVideoError)]
    [InlineData(YtDlpSamples.NoFormatError)]
    [InlineData(YtDlpSamples.ConnectionRefusedError)]
    [InlineData("[debug] JS runtimes: deno-2.9.6\n")]
    public void DoesNotFindItWhereItIsNot(string standardError) =>
        YtDlpDiagnostics.ReportsMissingJsRuntime(standardError).ShouldBeFalse();

    [Fact]
    public void RejectsNull() =>
        Should.Throw<ArgumentNullException>(() => YtDlpDiagnostics.ReportsMissingJsRuntime(null!));
}

public class WhenExtractingTheReason
{
    [Fact]
    public void DropsTheSeverityExtractorAndIdPrefix() =>
        YtDlpDiagnostics
            .Reason(YtDlpSamples.NonexistentVideoError)
            .ShouldBe("This video is unavailable");

    [Fact]
    public void DropsAPrefixWithAColonInTheExtractorName() =>
        YtDlpDiagnostics
            .Reason(YtDlpSamples.TruncatedIdError)
            .ShouldBe(
                "Incomplete YouTube ID jNQXAC9IVR. URL https://www.youtube.com/watch?v=jNQXAC9IVR looks truncated."
            );

    [Fact]
    public void KeepsTheWholeOfYtDlpsSentence() =>
        YtDlpDiagnostics
            .Reason(YtDlpSamples.UpcomingError)
            .ShouldBe("This live event will begin in 27 hours.");

    [Fact]
    public void UsesTheErrorLineRatherThanTheWarnings() =>
        YtDlpDiagnostics
            .Reason(YtDlpSamples.ConnectionRefusedError)
            .ShouldStartWith("Unable to download API page:");

    [Fact]
    public void DropsJustTheSeverityWhenThereIsNoExtractor() =>
        YtDlpDiagnostics
            .Reason(YtDlpSamples.DownloadTimedOutError)
            .ShouldBe("unable to download video data: The read operation timed out");

    [Fact]
    public void FallsBackToAUsageErrorLine() =>
        YtDlpDiagnostics
            .Reason(YtDlpSamples.UsageError)
            .ShouldBe("yt-dlp.exe: error: no such option: --bogus-option");

    [Fact]
    public void FallsBackToTheLastLineWhenNothingIsMarked() =>
        YtDlpDiagnostics.Reason("first\nsecond\n\n").ShouldBe("second");

    [Fact]
    public void HasNoReasonForEmptyOutput() => YtDlpDiagnostics.Reason("").ShouldBeNull();

    [Fact]
    public void HasNoReasonForBlankOutput() => YtDlpDiagnostics.Reason(" \r\n \n").ShouldBeNull();

    [Fact]
    public void TrimsTheCarriageReturn() =>
        YtDlpDiagnostics
            .Reason(YtDlpSamples.NonexistentVideoError.ReplaceLineEndings("\r\n"))
            .ShouldBe("This video is unavailable");

    [Fact]
    public void KeepsTheLastErrorWhenThereAreSeveral() =>
        YtDlpDiagnostics
            .Reason("ERROR: [youtube] a: first\nERROR: [youtube] b: second\n")
            .ShouldBe("second");

    [Fact]
    public void RejectsNull() =>
        Should.Throw<ArgumentNullException>(() => YtDlpDiagnostics.Reason(null!));
}

public class WhenReadingAVersion
{
    [Fact]
    public void ReadsYtDlpsVersion() =>
        YtDlpDiagnostics.YtDlpVersion(YtDlpSamples.YtDlpVersionOutput).ShouldBe("2026.08.19");

    [Fact]
    public void ReadsDenosVersionFromItsFirstLine() =>
        YtDlpDiagnostics.DenoVersion(YtDlpSamples.DenoVersionOutput).ShouldBe("2.9.6");

    [Fact]
    public void ReadsDenosVersionWithWindowsLineEndings() =>
        YtDlpDiagnostics
            .DenoVersion(YtDlpSamples.DenoVersionOutput.ReplaceLineEndings("\r\n"))
            .ShouldBe("2.9.6");

    [Fact]
    public void ReadsABareDenoVersionLine() =>
        YtDlpDiagnostics.DenoVersion("deno 3.0.0\n").ShouldBe("3.0.0");

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void HasNoYtDlpVersionForEmptyOutput(string output) =>
        YtDlpDiagnostics.YtDlpVersion(output).ShouldBeNull();

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("node v22.16.0\n")]
    [InlineData("deno\n")]
    public void HasNoDenoVersionForAnythingElse(string output) =>
        YtDlpDiagnostics.DenoVersion(output).ShouldBeNull();
}
