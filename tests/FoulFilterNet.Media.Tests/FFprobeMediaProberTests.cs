using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FoulFilterNet.Media.Tests;

/// <summary>
/// FFprobe output captured from the generated fixtures, trimmed to the fields
/// the prober reads. These are the replacement for libmagic: the question
/// "audio or video?" is now answered by the stream list, not a MIME guess.
/// </summary>
internal static class FFprobeSamples
{
    /// <summary>single_hit.mp3 — one audio stream, nothing else.</summary>
    public const string AudioMp3 = """
        {
          "streams": [
            { "index": 0, "codec_name": "mp3", "codec_type": "audio", "sample_rate": "16000",
              "channels": 1, "duration": "8.388000",
              "disposition": { "default": 1, "attached_pic": 0 } }
          ],
          "format": { "filename": "single_hit.mp3", "nb_streams": 1, "format_name": "mp3",
                      "duration": "8.388000", "size": "67518", "bit_rate": "64400" }
        }
        """;

    /// <summary>sample_video.mp4 — h264 alongside the audio track.</summary>
    public const string VideoMp4 = """
        {
          "streams": [
            { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 320, "height": 240,
              "duration": "5.600000",
              "disposition": { "default": 1, "attached_pic": 0 } },
            { "index": 1, "codec_name": "aac", "codec_type": "audio", "sample_rate": "16000",
              "channels": 1, "duration": "5.649000",
              "disposition": { "default": 1, "attached_pic": 0 } }
          ],
          "format": { "filename": "sample_video.mp4", "nb_streams": 2,
                      "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "5.649000" }
        }
        """;

    /// <summary>
    /// audiobook.m4b — the file the Python short-circuited by extension because
    /// libmagic called it video. FFprobe sees one audio stream and nothing else.
    /// </summary>
    public const string AudiobookM4b = """
        {
          "streams": [
            { "index": 0, "codec_name": "aac", "codec_type": "audio", "sample_rate": "16000",
              "channels": 1, "channel_layout": "mono", "duration": "13.030000",
              "disposition": { "default": 1, "attached_pic": 0 } }
          ],
          "format": { "filename": "audiobook.m4b", "nb_streams": 1,
                      "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "13.030000" }
        }
        """;

    /// <summary>An MP3 carrying cover art: a video stream that is really a still image.</summary>
    public const string AudioWithCoverArt = """
        {
          "streams": [
            { "index": 0, "codec_name": "mp3", "codec_type": "audio", "sample_rate": "44100",
              "channels": 2, "duration": "120.000000",
              "disposition": { "default": 1, "attached_pic": 0 } },
            { "index": 1, "codec_name": "mjpeg", "codec_type": "video", "width": 600, "height": 600,
              "disposition": { "default": 0, "attached_pic": 1 } }
          ],
          "format": { "filename": "tagged.mp3", "nb_streams": 2, "format_name": "mp3",
                      "duration": "120.000000" }
        }
        """;

    /// <summary>What FFprobe prints for a file it parsed but found nothing in.</summary>
    public const string NoStreams = """
        { "streams": [], "format": { "filename": "notes.txt", "nb_streams": 0 } }
        """;

    /// <summary>A container whose format block omits the duration.</summary>
    public const string DurationOnlyOnTheStream = """
        {
          "streams": [
            { "index": 0, "codec_name": "flac", "codec_type": "audio", "sample_rate": "48000",
              "duration": "42.500000", "disposition": { "attached_pic": 0 } }
          ],
          "format": { "filename": "stream.flac", "nb_streams": 1, "format_name": "flac" }
        }
        """;

    /// <summary>A live stream: no duration is knowable, and the sample rate is missing too.</summary>
    public const string NeitherDurationNorSampleRate = """
        {
          "streams": [ { "index": 0, "codec_type": "audio" } ],
          "format": { "filename": "live", "nb_streams": 1 }
        }
        """;
}

public class WhenReadingFFprobeOutputForAnAudioFile
{
    private readonly MediaInfo _info;

    public WhenReadingFFprobeOutputForAnAudioFile()
    {
        _info = FFprobeReport.Parse(FFprobeSamples.AudioMp3);

        _info.ShouldNotBeNull();
    }

    [Fact]
    public void CallsItAudio() => _info.Kind.ShouldBe(MediaKind.Audio);

    [Fact]
    public void ReadsTheDurationFromTheContainer() => _info.DurationSeconds.ShouldBe(8.388);

    [Fact]
    public void ReadsTheAudioSampleRate() => _info.AudioSampleRate.ShouldBe(16000);
}

public class WhenReadingFFprobeOutputForAVideoFile
{
    private readonly MediaInfo _info;

    public WhenReadingFFprobeOutputForAVideoFile()
    {
        _info = FFprobeReport.Parse(FFprobeSamples.VideoMp4);

        _info.ShouldNotBeNull();
    }

    [Fact]
    public void CallsItVideoBecauseAVideoStreamIsPresent() => _info.Kind.ShouldBe(MediaKind.Video);

    [Fact]
    public void PrefersTheContainerDurationOverEitherStreams() =>
        _info.DurationSeconds.ShouldBe(5.649);

    [Fact]
    public void StillReportsTheAudioSampleRate() => _info.AudioSampleRate.ShouldBe(16000);
}

/// <summary>
/// The case the Python short-circuited by extension. libmagic was unreliable on
/// MPEG-4 audiobooks, so <c>.m4b</c> and <c>.m4a</c> never reached it. FFprobe
/// needs no such hack, and these assertions are what says so.
/// </summary>
public class WhenReadingFFprobeOutputForAnAudiobook
{
    private readonly MediaInfo _info;

    public WhenReadingFFprobeOutputForAnAudiobook()
    {
        _info = FFprobeReport.Parse(FFprobeSamples.AudiobookM4b);

        _info.ShouldNotBeNull();
    }

    [Fact]
    public void CallsItAudioWithoutNeedingTheExtensionHack() =>
        _info.Kind.ShouldBe(MediaKind.Audio);

    [Fact]
    public void ReadsItsDuration() => _info.DurationSeconds.ShouldBe(13.03);

    [Fact]
    public void ReadsItsSampleRate() => _info.AudioSampleRate.ShouldBe(16000);
}

public class WhenAudioCarriesCoverArt
{
    private readonly MediaInfo _info;

    public WhenAudioCarriesCoverArt()
    {
        _info = FFprobeReport.Parse(FFprobeSamples.AudioWithCoverArt);

        _info.ShouldNotBeNull();
    }

    [Fact]
    public void IgnoresTheAttachedPictureAndCallsItAudio() => _info.Kind.ShouldBe(MediaKind.Audio);

    [Fact]
    public void WouldOtherwiseHaveTriedToStreamCopyAStillImage() =>
        _info.Kind.ShouldNotBe(MediaKind.Video);

    [Fact]
    public void ReadsTheRealAudioStreamsSampleRate() => _info.AudioSampleRate.ShouldBe(44100);
}

public class WhenFFprobeFindsNothingUsable
{
    private readonly MediaInfo _info;

    public WhenFFprobeFindsNothingUsable()
    {
        _info = FFprobeReport.Parse(FFprobeSamples.NoStreams);

        _info.ShouldNotBeNull();
    }

    [Fact]
    public void CallsItUnknown() => _info.Kind.ShouldBe(MediaKind.Unknown);

    [Fact]
    public void ReportsNoDuration() => _info.DurationSeconds.ShouldBe(0);

    [Fact]
    public void ReportsNoSampleRate() => _info.AudioSampleRate.ShouldBeNull();

    [Fact]
    public void TreatsMalformedJsonTheSameWay() =>
        FFprobeReport.Parse("not json at all").Kind.ShouldBe(MediaKind.Unknown);

    [Fact]
    public void TreatsEmptyOutputTheSameWay() =>
        FFprobeReport.Parse(string.Empty).Kind.ShouldBe(MediaKind.Unknown);
}

public class WhenTheContainerOmitsFieldsTheProberWants
{
    private readonly MediaInfo _fromStream;

    public WhenTheContainerOmitsFieldsTheProberWants()
    {
        _fromStream = FFprobeReport.Parse(FFprobeSamples.DurationOnlyOnTheStream);

        _fromStream.Kind.ShouldBe(MediaKind.Audio);
    }

    [Fact]
    public void FallsBackToTheStreamDuration() => _fromStream.DurationSeconds.ShouldBe(42.5);

    [Fact]
    public void ReportsZeroWhenNobodyKnowsTheDuration() =>
        FFprobeReport
            .Parse(FFprobeSamples.NeitherDurationNorSampleRate)
            .DurationSeconds.ShouldBe(0);

    [Fact]
    public void ReportsNoSampleRateWhenTheStreamOmitsIt() =>
        FFprobeReport
            .Parse(FFprobeSamples.NeitherDurationNorSampleRate)
            .AudioSampleRate.ShouldBeNull();

    [Fact]
    public void StillRecognisesTheStreamAsAudio() =>
        FFprobeReport
            .Parse(FFprobeSamples.NeitherDurationNorSampleRate)
            .Kind.ShouldBe(MediaKind.Audio);
}

public class WhenProbingAFile
{
    private readonly IFFmpegRunner _runner;
    private readonly MediaInfo _info;
    private readonly IReadOnlyList<string> _arguments;

    public WhenProbingAFile()
    {
        IReadOnlyList<string> captured = [];
        _runner = Substitute.For<IFFmpegRunner>();
        _runner
            .RunFFprobeAsync(
                Arg.Do<IReadOnlyList<string>>(arguments => captured = arguments),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new FFmpegResult(0, FFprobeSamples.AudioMp3, string.Empty)));

        _info = new FFprobeMediaProber(_runner).ProbeAsync("clip.mp3").GetAwaiter().GetResult();
        _arguments = captured;

        _info.ShouldNotBeNull();
        _arguments.ShouldNotBeEmpty();
    }

    [Fact]
    public void AsksFFprobeForJson() =>
        _arguments.ShouldBe([
            "-v",
            "quiet",
            "-print_format",
            "json",
            "-show_format",
            "-show_streams",
            "clip.mp3",
        ]);

    [Fact]
    public void PutsThePathLastSoItIsNeverReadAsAFlag() => _arguments[^1].ShouldBe("clip.mp3");

    [Fact]
    public void ReturnsWhatTheOutputDescribed() => _info.Kind.ShouldBe(MediaKind.Audio);
}

public class WhenFFprobeRefusesTheFile
{
    private readonly MediaInfo _info;

    public WhenFFprobeRefusesTheFile()
    {
        var runner = Substitute.For<IFFmpegRunner>();
        runner
            .RunFFprobeAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new FFmpegException("invalid data", 1, "Invalid data found"));

        _info = new FFprobeMediaProber(runner).ProbeAsync("notes.txt").GetAwaiter().GetResult();

        _info.ShouldNotBeNull();
    }

    [Fact]
    public void DegradesToUnknownRatherThanFailingTheJob() =>
        _info.Kind.ShouldBe(MediaKind.Unknown);

    [Fact]
    public void ReportsNoDuration() => _info.DurationSeconds.ShouldBe(0);
}

/// <summary>
/// End-to-end against the real FFprobe and the generated fixtures. Few and
/// fast, but they are the only thing that proves the captured JSON above still
/// resembles what FFprobe 7 actually prints.
/// </summary>
public class WhenProbingTheRealFixtures
{
    private readonly FFprobeMediaProber _prober;

    public WhenProbingTheRealFixtures()
    {
        _prober = new FFprobeMediaProber(new FFmpegRunner());

        File.Exists(MediaFixtures.Path("sample_video.mp4")).ShouldBeTrue();
        File.Exists(MediaFixtures.Path("audiobook.m4b")).ShouldBeTrue();
    }

    private Task<MediaInfo> Probe(string fileName) =>
        _prober.ProbeAsync(MediaFixtures.Path(fileName), TestContext.Current.CancellationToken);

    [Fact]
    public async Task RecognisesTheSampleVideo() =>
        (await Probe("sample_video.mp4")).Kind.ShouldBe(MediaKind.Video);

    [Fact]
    public async Task ReadsTheSampleVideosDurationAsTheManifestRecordsIt() =>
        (await Probe("sample_video.mp4")).DurationSeconds.ShouldBe(5.649, 0.01);

    [Fact]
    public async Task RecognisesTheAudiobookAsAudioWithoutTheExtensionHack() =>
        (await Probe("audiobook.m4b")).Kind.ShouldBe(MediaKind.Audio);

    [Fact]
    public async Task ReadsTheAudiobooksDuration() =>
        (await Probe("audiobook.m4b")).DurationSeconds.ShouldBe(13.03, 0.01);

    [Fact]
    public async Task RecognisesAPlainMp3() =>
        (await Probe("single_hit.mp3")).Kind.ShouldBe(MediaKind.Audio);

    [Fact]
    public async Task CallsAJsonFileUnknown() =>
        (await Probe("manifest.json")).Kind.ShouldBe(MediaKind.Unknown);
}
