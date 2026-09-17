using System.Diagnostics;

namespace FoulFilterNet.Media.Tests;

public class WhenBuildingTheProcessStartInfo
{
    private readonly ProcessStartInfo _startInfo;

    public WhenBuildingTheProcessStartInfo()
    {
        _startInfo = FFmpegProcess.CreateStartInfo("ffmpeg", ["-i", "a file.mp3", "out.mp3"]);

        _startInfo.ShouldNotBeNull();
        _startInfo.FileName.ShouldBe("ffmpeg");
    }

    [Fact]
    public void PassesArgumentsIndividuallySoSpacesNeverNeedQuoting() =>
        _startInfo.ArgumentList.ShouldBe(["-i", "a file.mp3", "out.mp3"]);

    [Fact]
    public void LeavesTheLegacyArgumentStringEmpty() => _startInfo.Arguments.ShouldBeEmpty();

    [Fact]
    public void LaunchesTheExecutableDirectly() => _startInfo.UseShellExecute.ShouldBeFalse();

    [Fact]
    public void CapturesStandardError() => _startInfo.RedirectStandardError.ShouldBeTrue();

    [Fact]
    public void CapturesStandardOutputSoFFprobeJsonCanBeRead() =>
        _startInfo.RedirectStandardOutput.ShouldBeTrue();

    [Fact]
    public void ShowsNoConsoleWindow() => _startInfo.CreateNoWindow.ShouldBeTrue();
}

public class WhenDescribingAnFFmpegCommandForTheLog
{
    private readonly string _description;

    public WhenDescribingAnFFmpegCommandForTheLog()
    {
        _description = FFmpegProcess.Describe(
            "ffmpeg",
            ["-i", "a file.mp3", "-map", "[out]", "out.mp3"]
        );

        _description.ShouldNotBeNullOrWhiteSpace();
        _description.ShouldStartWith("ffmpeg ");
    }

    [Fact]
    public void QuotesArgumentsContainingSpaces() => _description.ShouldContain("\"a file.mp3\"");

    [Fact]
    public void LeavesSimpleArgumentsBare() => _description.ShouldContain(" -map [out] ");

    [Fact]
    public void JoinsEveryArgument() => _description.ShouldEndWith(" out.mp3");
}

public class WhenPreparingTheArgumentsEveryInvocationShares
{
    private readonly IReadOnlyList<string> _prefix;

    public WhenPreparingTheArgumentsEveryInvocationShares()
    {
        _prefix = FFmpegArguments.Quiet();

        _prefix.ShouldNotBeEmpty();
    }

    [Fact]
    public void OverwritesTheOutputWithoutPrompting() => _prefix[0].ShouldBe("-y");

    [Fact]
    public void LogsOnlyErrors() => _prefix.ShouldBe(["-y", "-loglevel", "error"]);

    [Fact]
    public void HandsBackAFreshListEachTimeSoCallersCanAppend() =>
        FFmpegArguments.Quiet().ShouldNotBeSameAs(_prefix);
}

public class WhenAppendingToTheSharedArgumentPrefix
{
    private readonly IReadOnlyList<string> _arguments;

    public WhenAppendingToTheSharedArgumentPrefix()
    {
        _arguments = FFmpegArguments.Quiet("-i", "in.mp3", "out.mp3");

        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void KeepsThePrefixInFront() =>
        _arguments.ShouldBe(["-y", "-loglevel", "error", "-i", "in.mp3", "out.mp3"]);
}

/// <summary>
/// The adapter's own behaviour, exercised against the real binaries. These are
/// the only tests in this file that start a process; everything downstream
/// substitutes <see cref="IFFmpegRunner"/> instead.
/// </summary>
public class WhenFFprobeSucceeds
{
    private readonly FFmpegResult _result;

    public WhenFFprobeSucceeds()
    {
        _result = new FFmpegRunner().RunFFprobeAsync(["-version"]).GetAwaiter().GetResult();

        _result.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsASuccessfulExitCode() => _result.ExitCode.ShouldBe(0);

    [Fact]
    public void HandsBackWhatTheProcessWroteToStandardOutput() =>
        _result.StandardOutput.ShouldContain("ffprobe version");
}

public class WhenFFmpegExitsNonZero
{
    private readonly FFmpegException _thrown;

    public WhenFFmpegExitsNonZero()
    {
        _thrown = Should.Throw<FFmpegException>(() =>
            new FFmpegRunner()
                .RunFFmpegAsync(["-i", "no-such-input-file.mp3", "-f", "null", "-"])
                .GetAwaiter()
                .GetResult()
        );

        _thrown.ShouldNotBeNull();
    }

    [Fact]
    public void CarriesTheExitCode() => _thrown.ExitCode.ShouldNotBe(0);

    [Fact]
    public void SurfacesWhatFFmpegWroteToStandardError() =>
        _thrown.StandardError.ShouldContain("no-such-input-file.mp3");

    [Fact]
    public void PutsTheStandardErrorInTheMessageSoALogLineIsEnough() =>
        _thrown.Message.ShouldContain("no-such-input-file.mp3");

    [Fact]
    public void NamesTheCommandThatFailed() => _thrown.Message.ShouldContain("ffmpeg");
}

public class WhenTheExecutableIsMissing
{
    private readonly FFmpegRunner _runner;

    public WhenTheExecutableIsMissing()
    {
        _runner = new FFmpegRunner(
            new FFmpegOptions
            {
                FFmpegPath = "ffmpeg-that-is-not-installed",
                FFprobePath = "ffprobe-that-is-not-installed",
            }
        );

        _runner.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsItRatherThanLettingWin32ErrorsEscape() =>
        Should.Throw<FFmpegException>(() =>
            _runner.RunFFmpegAsync(["-version"]).GetAwaiter().GetResult()
        );

    [Fact]
    public void NamesTheExecutableItCouldNotStart() =>
        Should
            .Throw<FFmpegException>(() =>
                _runner.RunFFmpegAsync(["-version"]).GetAwaiter().GetResult()
            )
            .Message.ShouldContain("ffmpeg-that-is-not-installed");
}

public class WhenNoOptionsAreSupplied
{
    private readonly FFmpegOptions _options;

    public WhenNoOptionsAreSupplied()
    {
        _options = new FFmpegOptions();

        _options.ShouldNotBeNull();
    }

    [Fact]
    public void LooksForFFmpegOnThePath() => _options.FFmpegPath.ShouldBe("ffmpeg");

    [Fact]
    public void LooksForFFprobeOnThePath() => _options.FFprobePath.ShouldBe("ffprobe");
}
