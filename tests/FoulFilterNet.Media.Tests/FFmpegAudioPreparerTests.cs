using FoulFilterNet.Domain;

namespace FoulFilterNet.Media.Tests;

public class WhenExtractingAVideosAudioTrack
{
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly IReadOnlyList<string> _arguments;

    public WhenExtractingAVideosAudioTrack()
    {
        new FFmpegAudioPreparer(_runner)
            .ExtractAudioTrackAsync("clip.mp4", "work/clip.m4a")
            .GetAwaiter()
            .GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void RunsTheWholeCommandTheLegacyPipelineRan() =>
        _arguments.ShouldBe([
            "-y",
            "-loglevel",
            "error",
            "-i",
            "clip.mp4",
            "-vn",
            "-acodec",
            "aac",
            "work/clip.m4a",
        ]);

    [Fact]
    public void DropsTheVideoStream() => _arguments.ShouldContain("-vn");

    [Fact]
    public void EncodesTheTrackAsAac() =>
        _arguments.SkipWhile(argument => argument != "-acodec").Skip(1).First().ShouldBe("aac");

    [Fact]
    public void WritesWhereItWasAsked() => _arguments[^1].ShouldBe("work/clip.m4a");

    [Fact]
    public async Task RefusesAnEmptyOutputPath() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            new FFmpegAudioPreparer(_runner).ExtractAudioTrackAsync(
                "clip.mp4",
                "   ",
                TestContext.Current.CancellationToken
            )
        );
}

public sealed class WhenPaddingAudioForTheRescanPass : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly FFmpegAudioPreparer _preparer;
    private readonly string _padded;
    private readonly IReadOnlyList<string> _arguments;

    public WhenPaddingAudioForTheRescanPass()
    {
        _preparer = new FFmpegAudioPreparer(_runner, _scratch.Path);
        _padded = _preparer
            .PadStartAsync("analysis.m4a", FFmpegAudioPreparer.DefaultRescanOffsetSeconds)
            .GetAwaiter()
            .GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
        _padded.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void RunsTheWholeCommandTheLegacyTranscriberRan() =>
        _arguments.ShouldBe([
            "-y",
            "-loglevel",
            "error",
            "-i",
            "analysis.m4a",
            "-af",
            "adelay=4000|4000",
            "-vn",
            "-ac",
            "1",
            "-ar",
            "16000",
            "-acodec",
            "pcm_s16le",
            _padded,
        ]);

    [Fact]
    public void PrependsTheOffsetAsMillisecondsOnBothChannels() =>
        _arguments.ShouldContain("adelay=4000|4000");

    [Fact]
    public void DefaultsToFourSecondsOfSilence() =>
        FFmpegAudioPreparer.DefaultRescanOffsetSeconds.ShouldBe(4.0);

    [Fact]
    public void WritesAWavSoWhisperNeedsNoDecoder() => _padded.ShouldEndWith(".wav");

    [Fact]
    public void HandsBackAPathInsideTheTemporaryDirectory() =>
        Path.GetDirectoryName(_padded).ShouldBe(_scratch.Path);

    [Fact]
    public async Task ChoosesAFreshNameEachTimeSoConcurrentJobsCannotCollide() =>
        (
            await _preparer.PadStartAsync(
                "analysis.m4a",
                4.0,
                TestContext.Current.CancellationToken
            )
        ).ShouldNotBe(_padded);
}

public sealed class WhenPaddingByAFractionalOffset : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly FFmpegAudioPreparer _preparer;

    public WhenPaddingByAFractionalOffset()
    {
        _preparer = new FFmpegAudioPreparer(_runner, _scratch.Path);
        _preparer.PadStartAsync("analysis.m4a", 4.5).GetAwaiter().GetResult();

        _runner.FFmpegCalls.ShouldHaveSingleItem();
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void RoundsTheDelayDownToWholeMillisecondsAsPythonsIntDid() =>
        _runner.LastFFmpegCall.ShouldContain("adelay=4500|4500");

    [Fact]
    public async Task RendersASubSecondOffsetToo()
    {
        await _preparer.PadStartAsync("analysis.m4a", 0.25, TestContext.Current.CancellationToken);

        _runner.LastFFmpegCall.ShouldContain("adelay=250|250");
    }

    [Fact]
    public async Task RefusesANegativeOffset() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _preparer.PadStartAsync("analysis.m4a", -1.0, TestContext.Current.CancellationToken)
        );
}

public sealed class WhenCroppingASpanOfAudio : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly FFmpegAudioPreparer _preparer;
    private readonly string _cropped;
    private readonly IReadOnlyList<string> _arguments;

    public WhenCroppingASpanOfAudio()
    {
        _preparer = new FFmpegAudioPreparer(_runner, _scratch.Path);
        _cropped = _preparer.CropAsync("analysis.m4a", 12.5, 3.0).GetAwaiter().GetResult();

        _arguments = _runner.LastFFmpegCall;

        _runner.FFmpegCalls.ShouldHaveSingleItem();
        _cropped.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose() => _scratch.Dispose();

    private int PositionOf(string flag) => _arguments.ToList().IndexOf(flag);

    [Fact]
    public void RunsTheWholeCommandTheLegacyAlignerRan() =>
        _arguments.ShouldBe([
            "-y",
            "-loglevel",
            "error",
            "-ss",
            "12.500",
            "-t",
            "3.000",
            "-i",
            "analysis.m4a",
            "-vn",
            "-ac",
            "1",
            "-ar",
            "16000",
            "-acodec",
            "pcm_s16le",
            _cropped,
        ]);

    [Fact]
    public void SeeksBeforeOpeningTheInputSoFFmpegDecodesOnlyTheSpan() =>
        PositionOf("-ss").ShouldBeLessThan(PositionOf("-i"));

    [Fact]
    public void LimitsTheDurationBeforeOpeningTheInputToo() =>
        PositionOf("-t").ShouldBeLessThan(PositionOf("-i"));

    [Fact]
    public void RendersAWholeSecondWithThreeDecimalPlacesRatherThanAsAnInteger() =>
        _arguments.ShouldContain("3.000");

    [Fact]
    public void DownmixesToMono() =>
        _arguments.SkipWhile(argument => argument != "-ac").Skip(1).First().ShouldBe("1");

    [Fact]
    public void ResamplesToSixteenKilohertz() =>
        _arguments.SkipWhile(argument => argument != "-ar").Skip(1).First().ShouldBe("16000");

    [Fact]
    public void WritesUncompressedSixteenBitPcm() =>
        _arguments
            .SkipWhile(argument => argument != "-acodec")
            .Skip(1)
            .First()
            .ShouldBe("pcm_s16le");

    [Fact]
    public async Task RefusesANonPositiveDuration() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _preparer.CropAsync("analysis.m4a", 1.0, 0.0, TestContext.Current.CancellationToken)
        );
}

/// <summary>
/// The temp file becomes the caller's only once it holds audio. A failed render
/// leaves a truncated file behind, and handing that back would make the caller
/// responsible for cleaning up something it never received.
/// </summary>
public sealed class WhenFFmpegFailsWhilePreparingAudio : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly FFmpegAudioPreparer _preparer;

    public WhenFFmpegFailsWhilePreparingAudio()
    {
        _runner.OnFFmpeg = arguments =>
        {
            File.WriteAllText(arguments[^1], "a truncated render");
            throw new FFmpegException("could not seek", 1, "Invalid data found");
        };

        _preparer = new FFmpegAudioPreparer(_runner, _scratch.Path);

        Directory.GetFiles(_scratch.Path).ShouldBeEmpty();
    }

    public void Dispose() => _scratch.Dispose();

    private Task<string> Pad() =>
        _preparer.PadStartAsync("analysis.m4a", 4.0, TestContext.Current.CancellationToken);

    private Task<string> Crop() =>
        _preparer.CropAsync("analysis.m4a", 1.0, 2.0, TestContext.Current.CancellationToken);

    [Fact]
    public async Task PaddingSurfacesTheFailure() => await Should.ThrowAsync<FFmpegException>(Pad);

    [Fact]
    public async Task CroppingSurfacesTheFailure() =>
        await Should.ThrowAsync<FFmpegException>(Crop);

    [Fact]
    public async Task PaddingLeavesNoHalfWrittenFileBehind()
    {
        await Should.ThrowAsync<FFmpegException>(Pad);

        Directory.GetFiles(_scratch.Path).ShouldBeEmpty();
    }

    [Fact]
    public async Task CroppingLeavesNoHalfWrittenFileBehind()
    {
        await Should.ThrowAsync<FFmpegException>(Crop);

        Directory.GetFiles(_scratch.Path).ShouldBeEmpty();
    }
}

/// <summary>
/// One end-to-end pass over a generated fixture with the real FFmpeg. The
/// argument assertions above prove the command; only this proves the command
/// produces audio the transcriber can read.
/// </summary>
public sealed class WhenPreparingTheRealSampleVideosAudio : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly FFmpegAudioPreparer _preparer;
    private readonly FFprobeMediaProber _prober;
    private readonly string _extracted;

    public WhenPreparingTheRealSampleVideosAudio()
    {
        var runner = new FFmpegRunner();
        _preparer = new FFmpegAudioPreparer(runner, _scratch.Path);
        _prober = new FFprobeMediaProber(runner);
        _extracted = _scratch.File("extracted.m4a");

        File.Exists(MediaFixtures.Path("sample_video.mp4")).ShouldBeTrue();

        _preparer
            .ExtractAudioTrackAsync(MediaFixtures.Path("sample_video.mp4"), _extracted)
            .GetAwaiter()
            .GetResult();

        File.Exists(_extracted).ShouldBeTrue();
    }

    public void Dispose() => _scratch.Dispose();

    private Task<MediaInfo> Probe(string path) =>
        _prober.ProbeAsync(path, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ExtractsATrackWithNoPictureInIt() =>
        (await Probe(_extracted)).Kind.ShouldBe(MediaKind.Audio);

    [Fact]
    public async Task CropsAMonoSixteenKilohertzSpan()
    {
        var cropped = await _preparer.CropAsync(
            _extracted,
            1.0,
            2.0,
            TestContext.Current.CancellationToken
        );

        (await Probe(cropped)).AudioSampleRate.ShouldBe(16000);
    }

    [Fact]
    public async Task CropsExactlyTheSpanItWasAskedFor()
    {
        var cropped = await _preparer.CropAsync(
            _extracted,
            1.0,
            2.0,
            TestContext.Current.CancellationToken
        );

        (await Probe(cropped)).DurationSeconds.ShouldBe(2.0, 0.1);
    }

    [Fact]
    public async Task PadsTheTrackByTheWholeOffset()
    {
        var before = await Probe(_extracted);
        var padded = await _preparer.PadStartAsync(
            _extracted,
            4.0,
            TestContext.Current.CancellationToken
        );

        (await Probe(padded)).DurationSeconds.ShouldBe(before.DurationSeconds + 4.0, 0.15);
    }
}
