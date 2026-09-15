using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using NSubstitute;

namespace FoulFilterNet.Media.Tests;

/// <summary>
/// Shared setup for the editor specs: a recording runner, a prober that answers
/// with a fixed duration, and the one hit every scenario censors.
/// </summary>
internal static class EditorFixture
{
    public const double SourceDurationSeconds = 8.388;

    public static IReadOnlyList<Hit> OneHit { get; } = [new Hit("damn", 1.0, 2.0)];

    public static IMediaProber Prober(MediaKind kind = MediaKind.Audio, double duration = SourceDurationSeconds)
    {
        var prober = Substitute.For<IMediaProber>();
        prober.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MediaInfo(kind, duration, 16000)));
        return prober;
    }
}

public class WhenSilencingHitsInAnAudioFile
{
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly IReadOnlyList<string> _arguments;

    public WhenSilencingHitsInAnAudioFile()
    {
        new MediaEditor(_runner, EditorFixture.Prober())
            .CensorAudioAsync("in.mp3", EditorFixture.OneHit, CensorMethod.Silence, "out.mp3")
            .GetAwaiter().GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void RunsTheWholeCommandTheLegacyRendererRan() => _arguments.ShouldBe(
        [
            "-y", "-loglevel", "error",
            "-i", "in.mp3",
            "-filter_complex", "[0:a]volume=enable='between(t,1.0,2.0)':volume=0[aout]",
            "-map", "[aout]",
            "out.mp3",
        ]);

    [Fact]
    public void MapsTheLabelTheSilenceGraphPublishes() =>
        _arguments.SkipWhile(argument => argument != "-map").Skip(1).First().ShouldBe("[aout]");

    [Fact]
    public void WrapsTheUnlabelledChainSoItReadsTheFilesAudioStream() =>
        _arguments.ShouldContain(argument => argument.StartsWith("[0:a]", StringComparison.Ordinal));

    [Fact]
    public void NeverProbesBecauseSilenceNeedsNoDuration() => _runner.FFprobeCalls.ShouldBeEmpty();
}

public class WhenBleepingHitsInAnAudioFile
{
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly IReadOnlyList<string> _arguments;

    public WhenBleepingHitsInAnAudioFile()
    {
        new MediaEditor(_runner, EditorFixture.Prober())
            .CensorAudioAsync("in.mp3", EditorFixture.OneHit, CensorMethod.Bleep, "out.mp3")
            .GetAwaiter().GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void RunsTheWholeCommandTheLegacyRendererRan() => _arguments.ShouldBe(
        [
            "-y", "-loglevel", "error",
            "-i", "in.mp3",
            "-filter_complex", FilterGraph.Bleep(EditorFixture.OneHit),
            "-map", "[out]",
            "out.mp3",
        ]);

    [Fact]
    public void MapsTheLabelTheBleepGraphPublishes() =>
        _arguments.SkipWhile(argument => argument != "-map").Skip(1).First().ShouldBe("[out]");

    [Fact]
    public void RendersInOnePassWithNoIntermediateTrack() => _runner.FFmpegCalls.ShouldHaveSingleItem();
}

public class WhenRemovingHitsFromAnAudioFile
{
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly IMediaProber _prober = EditorFixture.Prober();
    private readonly IReadOnlyList<string> _arguments;

    public WhenRemovingHitsFromAnAudioFile()
    {
        new MediaEditor(_runner, _prober)
            .CensorAudioAsync("in.mp3", EditorFixture.OneHit, CensorMethod.Remove, "out.mp3")
            .GetAwaiter().GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void RunsTheWholeCommandTheLegacyRendererRan() => _arguments.ShouldBe(
        [
            "-y", "-loglevel", "error",
            "-i", "in.mp3",
            "-filter_complex", FilterGraph.Remove(EditorFixture.OneHit, EditorFixture.SourceDurationSeconds),
            "-map", "[out]",
            "out.mp3",
        ]);

    [Fact]
    public void MapsTheLabelTheRemoveGraphPublishes() =>
        _arguments.SkipWhile(argument => argument != "-map").Skip(1).First().ShouldBe("[out]");

    [Fact]
    public async Task AsksTheProberHowLongTheFileIsSoTheTailCanBeJudged() =>
        await _prober.Received().ProbeAsync("in.mp3", Arg.Any<CancellationToken>());

    [Fact]
    public void KeepsTheSpansEitherSideOfTheHit() =>
        _arguments.ShouldContain(argument => argument.Contains("concat=n=2", StringComparison.Ordinal));
}

/// <summary>
/// A duration of zero is FFprobe's "I could not tell", not "this file is empty".
/// Passing it on as a real length would make the remove graph drop the tail and
/// then refuse to build at all.
/// </summary>
public class WhenRemovingHitsFromAFileOfUnknowableLength
{
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly IReadOnlyList<string> _arguments;

    public WhenRemovingHitsFromAFileOfUnknowableLength()
    {
        new MediaEditor(_runner, EditorFixture.Prober(duration: 0))
            .CensorAudioAsync("live.mp3", EditorFixture.OneHit, CensorMethod.Remove, "out.mp3")
            .GetAwaiter().GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
    }

    [Fact]
    public void StillKeepsAnOpenEndedTail() =>
        _arguments.ShouldContain(argument => argument.Contains("atrim=start=2.000,", StringComparison.Ordinal));

    [Fact]
    public void BuildsTheSameGraphAsAnUnknownDurationWould() =>
        _arguments.ShouldContain(FilterGraph.Remove(EditorFixture.OneHit));
}

public class WhenSilencingHitsInAVideo
{
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly IReadOnlyList<string> _arguments;

    public WhenSilencingHitsInAVideo()
    {
        new MediaEditor(_runner, EditorFixture.Prober(MediaKind.Video))
            .CensorVideoAsync("in.mp4", EditorFixture.OneHit, CensorMethod.Silence, "out.mp4")
            .GetAwaiter().GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void RunsTheWholeCommandTheLegacyRendererRan() => _arguments.ShouldBe(
        [
            "-y", "-loglevel", "error",
            "-i", "in.mp4",
            "-filter_complex", "[0:a]volume=enable='between(t,1.0,2.0)':volume=0[aout]",
            "-map", "0:v", "-map", "[aout]",
            "-c:v", "copy",
            "out.mp4",
        ]);

    [Fact]
    public void PassesTheVideoStreamThroughUntouched() =>
        _arguments.SkipWhile(argument => argument != "-c:v").Skip(1).First().ShouldBe("copy");

    [Fact]
    public void KeepsTheOriginalVideoStreamAlongsideTheFilteredAudio() =>
        _arguments.Where(argument => argument == "-map").Count().ShouldBe(2);
}

/// <summary>
/// ADR-0004: cutting a video's audio would slide it out of step with the
/// picture, so Remove on video is the silence render, not a refusal.
/// </summary>
public class WhenAskedToRemoveHitsFromAVideo
{
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly IReadOnlyList<string> _arguments;

    public WhenAskedToRemoveHitsFromAVideo()
    {
        new MediaEditor(_runner, EditorFixture.Prober(MediaKind.Video))
            .CensorVideoAsync("in.mp4", EditorFixture.OneHit, CensorMethod.Remove, "out.mp4")
            .GetAwaiter().GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
    }

    [Fact]
    public void FallsBackToSilencingTheHitInstead() =>
        _arguments.ShouldContain("[0:a]volume=enable='between(t,1.0,2.0)':volume=0[aout]");

    [Fact]
    public void NeverCutsTheAudioOut() =>
        _arguments.ShouldNotContain(argument => argument.Contains("atrim", StringComparison.Ordinal));

    [Fact]
    public void StillStreamCopiesThePicture() => _arguments.ShouldContain("copy");
}

public sealed class WhenBleepingHitsInAVideo : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly string _outputPath;
    private readonly string _temporaryTrack;

    public WhenBleepingHitsInAVideo()
    {
        _outputPath = _scratch.File("out.mp4");
        _temporaryTrack = _outputPath + ".bleep_track.m4a";
        _runner.OnFFmpeg = arguments => File.WriteAllText(arguments[^1], "rendered");

        new MediaEditor(_runner, EditorFixture.Prober(MediaKind.Video))
            .CensorVideoAsync("in.mp4", EditorFixture.OneHit, CensorMethod.Bleep, _outputPath)
            .GetAwaiter().GetResult();

        _runner.FFmpegCalls.Count.ShouldBe(2);
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void RendersTheCensoredTrackFirstBecauseBleepNeedsGeneratedSources() =>
        _runner.FFmpegCalls[0].ShouldBe(
            [
                "-y", "-loglevel", "error",
                "-i", "in.mp4", "-vn",
                "-filter_complex", FilterGraph.Bleep(EditorFixture.OneHit),
                "-map", "[out]",
                _temporaryTrack,
            ]);

    [Fact]
    public void MuxesTheTrackBackOverTheUntouchedPicture() =>
        _runner.FFmpegCalls[1].ShouldBe(
            [
                "-y", "-loglevel", "error",
                "-i", "in.mp4",
                "-i", _temporaryTrack,
                "-map", "0:v", "-map", "1:a",
                "-c:v", "copy",
                _outputPath,
            ]);

    [Fact]
    public void DeletesTheIntermediateTrack() => File.Exists(_temporaryTrack).ShouldBeFalse();

    [Fact]
    public void LeavesTheFinishedVideoInPlace() => File.Exists(_outputPath).ShouldBeTrue();
}

public sealed class WhenTheMuxFailsAfterBleepingAVideo : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly MediaEditor _editor;
    private readonly string _outputPath;
    private readonly string _temporaryTrack;

    public WhenTheMuxFailsAfterBleepingAVideo()
    {
        _outputPath = _scratch.File("out.mp4");
        _temporaryTrack = _outputPath + ".bleep_track.m4a";
        _runner.OnFFmpeg = arguments =>
        {
            File.WriteAllText(arguments[^1], "rendered");
            if (arguments[^1] == _outputPath)
            {
                throw new FFmpegException("cannot stream copy", 1, "Invalid data found");
            }
        };

        _editor = new MediaEditor(_runner, EditorFixture.Prober(MediaKind.Video));

        File.Exists(_temporaryTrack).ShouldBeFalse();
    }

    public void Dispose() => _scratch.Dispose();

    private Task Bleep() => _editor.CensorVideoAsync(
        "in.mp4", EditorFixture.OneHit, CensorMethod.Bleep, _outputPath, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SurfacesTheFailure() => await Should.ThrowAsync<FFmpegException>(Bleep);

    [Fact]
    public async Task StillDeletesTheIntermediateTrack()
    {
        await Should.ThrowAsync<FFmpegException>(Bleep);

        File.Exists(_temporaryTrack).ShouldBeFalse();
    }
}

public sealed class WhenNoOutputPathIsSupplied : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();

    public WhenNoOutputPathIsSupplied()
    {
        new MediaEditor(_runner, EditorFixture.Prober())
            .CensorAudioAsync(_scratch.File("podcast.mp3"), EditorFixture.OneHit, CensorMethod.Silence, null!)
            .GetAwaiter().GetResult();

        _runner.FFmpegCalls.ShouldHaveSingleItem();
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void MarksTheOutputCensoredBesideTheInput() =>
        _runner.LastFFmpegCall[^1].ShouldBe(_scratch.File("podcast_CENSORED.mp3"));

    [Fact]
    public void KeepsTheOriginalExtension() =>
        MediaEditor.DefaultOutputPath("clip.mp4").ShouldEndWith(".mp4");

    [Fact]
    public void NamesAFileWithNoDirectoryInPlace() =>
        MediaEditor.DefaultOutputPath("clip.mp4").ShouldBe("clip_CENSORED.mp4");

    [Fact]
    public void LeavesAnExtensionlessFileExtensionless() =>
        MediaEditor.DefaultOutputPath("recording").ShouldBe("recording_CENSORED");

    [Fact]
    public void TreatsABlankOutputPathAsNoneSupplied() =>
        MediaEditor.ResolveOutputPath("clip.mp4", "   ").ShouldBe("clip_CENSORED.mp4");

    [Fact]
    public void UsesAnOutputPathWhenOneIsGiven() =>
        MediaEditor.ResolveOutputPath("clip.mp4", "chosen.mp4").ShouldBe("chosen.mp4");
}

/// <summary>
/// One end-to-end render with the real FFmpeg. The argument assertions above
/// prove the command; only this proves the command produces a playable file
/// whose picture survived untouched.
/// </summary>
public sealed class WhenRenderingTheRealSampleVideo : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly FFmpegRunner _runner = new();
    private readonly FFprobeMediaProber _prober;
    private readonly string _source = MediaFixtures.Path("sample_video.mp4");
    private readonly string _outputPath;

    public WhenRenderingTheRealSampleVideo()
    {
        _prober = new FFprobeMediaProber(_runner);
        _outputPath = _scratch.File("censored.mp4");

        File.Exists(_source).ShouldBeTrue();

        new MediaEditor(_runner, _prober)
            .CensorVideoAsync(_source, [new Hit("damn", 1.0, 2.0)], CensorMethod.Silence, _outputPath)
            .GetAwaiter().GetResult();

        File.Exists(_outputPath).ShouldBeTrue();
    }

    public void Dispose() => _scratch.Dispose();

    /// <summary>The checksum of a file's video stream, copied rather than re-encoded.</summary>
    private async Task<string> VideoStreamDigest(string path) =>
        (await _runner.RunFFmpegAsync(
            ["-v", "error", "-i", path, "-map", "0:v", "-c", "copy", "-f", "md5", "-"],
            TestContext.Current.CancellationToken)).StandardOutput.Trim();

    [Fact]
    public async Task ProducesAFileFFprobeStillCallsAVideo() =>
        (await _prober.ProbeAsync(_outputPath, TestContext.Current.CancellationToken))
            .Kind.ShouldBe(MediaKind.Video);

    [Fact]
    public async Task KeepsTheRunningTime() =>
        (await _prober.ProbeAsync(_outputPath, TestContext.Current.CancellationToken))
            .DurationSeconds.ShouldBe(5.649, 0.1);

    [Fact]
    public async Task LeavesEveryVideoFrameBitIdentical() =>
        (await VideoStreamDigest(_outputPath)).ShouldBe(await VideoStreamDigest(_source));
}
