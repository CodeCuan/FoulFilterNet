using System.Diagnostics;
using NSubstitute;

namespace FoulFilterNet.Media.Tests;

/// <summary>
/// The general-purpose process adapter that <see cref="FFmpegRunner"/> is built
/// on and that the yt-dlp audio source reuses. Unlike the FFmpeg runner it
/// never judges an exit code: yt-dlp exits 1 for a private video and for a
/// network failure alike, and only its standard error tells them apart, so the
/// caller gets everything back and decides. These facts start real processes
/// (FFmpeg, which the Media tests already require on every machine and in CI).
/// </summary>
public class WhenAProcessSucceeds
{
    private readonly ProcessResult _result;

    public WhenAProcessSucceeds()
    {
        _result = new ProcessRunner().RunAsync("ffprobe", ["-version"]).GetAwaiter().GetResult();

        _result.ShouldNotBeNull();
    }

    [Fact]
    public void ReportsTheExitCode() => _result.ExitCode.ShouldBe(0);

    [Fact]
    public void HandsBackStandardOutput() =>
        _result.StandardOutput.ShouldContain("ffprobe version");

    [Fact]
    public void HandsBackStandardErrorSeparately() =>
        _result.StandardError.ShouldNotContain("ffprobe version");
}

public class WhenAProcessExitsNonZero
{
    private readonly ProcessResult _result;

    public WhenAProcessExitsNonZero()
    {
        _result = new ProcessRunner()
            .RunAsync("ffmpeg", ["-i", "no-such-input-file.mp3", "-f", "null", "-"])
            .GetAwaiter()
            .GetResult();

        _result.ShouldNotBeNull();
    }

    [Fact]
    public void ReturnsRatherThanThrowing() => _result.ExitCode.ShouldNotBe(0);

    [Fact]
    public void HandsBackWhatItWroteToStandardError() =>
        _result.StandardError.ShouldContain("no-such-input-file.mp3");
}

public class WhenAProcessCannotBeStarted
{
    private readonly ProcessNotStartedException _thrown;

    public WhenAProcessCannotBeStarted()
    {
        _thrown = Should.Throw<ProcessNotStartedException>(() =>
            new ProcessRunner()
                .RunAsync("an-executable-that-is-not-installed", ["--version"])
                .GetAwaiter()
                .GetResult()
        );

        _thrown.ShouldNotBeNull();
    }

    [Fact]
    public void NamesTheExecutable() =>
        _thrown.FileName.ShouldBe("an-executable-that-is-not-installed");

    [Fact]
    public void SaysWhichExecutableInTheMessage() =>
        _thrown.Message.ShouldContain("an-executable-that-is-not-installed");

    [Fact]
    public void KeepsTheOperatingSystemsReason() => _thrown.InnerException.ShouldNotBeNull();
}

/// <summary>
/// Cancelling must not leave the process running: yt-dlp keeps downloading
/// (and holding the partial file open) until it is killed, and its PyInstaller
/// executable is a bootloader with a child, so the whole tree has to go.
/// FFmpeg reading a real-time source for a minute stands in for it.
/// </summary>
public class WhenARunningProcessIsCancelled
{
    private readonly Exception? _thrown;
    private readonly TimeSpan _elapsed;

    public WhenARunningProcessIsCancelled()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var stopwatch = Stopwatch.StartNew();

        _thrown = Record.Exception(() =>
            new ProcessRunner()
                .RunAsync(
                    "ffmpeg",
                    ["-re", "-f", "lavfi", "-i", "anullsrc", "-t", "60", "-f", "null", "-"],
                    cancellation.Token
                )
                .GetAwaiter()
                .GetResult()
        );

        _elapsed = stopwatch.Elapsed;
    }

    [Fact]
    public void ThrowsOperationCanceled() =>
        _thrown.ShouldBeAssignableTo<OperationCanceledException>();

    [Fact]
    public void ReturnsLongBeforeTheProcessWouldHaveFinished() =>
        _elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
}

public class WhenCancelledBeforeStarting
{
    [Fact]
    public void ThrowsWithoutRunningAnything() =>
        Should.Throw<OperationCanceledException>(() =>
            new ProcessRunner()
                .RunAsync("ffprobe", ["-version"], new CancellationToken(canceled: true))
                .GetAwaiter()
                .GetResult()
        );
}

public class WhenTheRunnerIsGivenBadArguments
{
    private readonly ProcessRunner _runner = new();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RejectsABlankExecutable(string fileName) =>
        Should.Throw<ArgumentException>(() =>
            _runner.RunAsync(fileName, []).GetAwaiter().GetResult()
        );

    [Fact]
    public void RejectsANullArgumentList() =>
        Should.Throw<ArgumentNullException>(() =>
            _runner.RunAsync("ffprobe", null!).GetAwaiter().GetResult()
        );
}

/// <summary>
/// <see cref="FFmpegRunner"/> is now FFmpeg's exit-code policy over the general
/// runner. Its real-process behaviour is pinned by <c>FFmpegRunnerTests</c>,
/// unchanged; these pin the mapping with the runner substituted.
/// </summary>
public class WhenFFmpegRunsOverASubstitutedProcessRunner
{
    private readonly IProcessRunner _processes = Substitute.For<IProcessRunner>();
    private readonly FFmpegRunner _sut;

    public WhenFFmpegRunsOverASubstitutedProcessRunner()
    {
        _sut = new FFmpegRunner(
            new FFmpegOptions { FFmpegPath = "my-ffmpeg", FFprobePath = "my-ffprobe" },
            _processes
        );
    }

    [Fact]
    public async Task RunsTheConfiguredFFmpeg()
    {
        _processes
            .RunAsync("my-ffmpeg", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "out", "err"));

        (
            await _sut.RunFFmpegAsync(["-version"], TestContext.Current.CancellationToken)
        ).StandardOutput.ShouldBe("out");
    }

    [Fact]
    public async Task RunsTheConfiguredFFprobe()
    {
        _processes
            .RunAsync("my-ffprobe", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "{}", ""));

        (
            await _sut.RunFFprobeAsync(["-version"], TestContext.Current.CancellationToken)
        ).StandardOutput.ShouldBe("{}");
    }

    [Fact]
    public void TurnsANonZeroExitIntoAnFFmpegException()
    {
        _processes
            .RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new ProcessResult(3, "", "boom"));

        Should
            .Throw<FFmpegException>(() => _sut.RunFFmpegAsync(["-x"]).GetAwaiter().GetResult())
            .ExitCode.ShouldBe(3);
    }

    [Fact]
    public void KeepsTheOperatingSystemsErrorAsTheCauseWhenItCannotStart()
    {
        _processes
            .RunAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<ProcessResult>(_ =>
                throw new ProcessNotStartedException(
                    "my-ffmpeg",
                    new System.ComponentModel.Win32Exception(2)
                )
            );

        Should
            .Throw<FFmpegException>(() => _sut.RunFFmpegAsync(["-x"]).GetAwaiter().GetResult())
            .InnerException.ShouldBeOfType<System.ComponentModel.Win32Exception>();
    }

    [Fact]
    public void RejectsANullProcessRunner() =>
        Should.Throw<ArgumentNullException>(() => new FFmpegRunner(new FFmpegOptions(), null!));
}
