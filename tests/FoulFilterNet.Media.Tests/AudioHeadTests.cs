using System.Buffers.Binary;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Media.Tests;

/// <summary>
/// W17: the head of a video's analysis audio is the whole conversion's
/// command with <c>-t</c> added as an output option, so FFmpeg decodes,
/// resamples and writes exactly as it would for the whole file and simply
/// stops early.
/// </summary>
public sealed class WhenConvertingTheHeadOfTheAnalysisAudio : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly FFmpegAudioPreparer _preparer;
    private readonly string _head;
    private readonly IReadOnlyList<string> _arguments;

    public WhenConvertingTheHeadOfTheAnalysisAudio()
    {
        _preparer = new FFmpegAudioPreparer(_runner, _scratch.Path);
        _head = _preparer
            .ConvertHeadAsync("audio.webm", 120.0, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        _arguments = _runner.LastFFmpegCall;
        _runner.FFmpegCalls.ShouldHaveSingleItem();
    }

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void IsAnAudioHeadPreparer() => _preparer.ShouldBeAssignableTo<IAudioHeadPreparer>();

    [Fact]
    public void RunsTheWholeConversionsCommandStoppingAfterTheHead() =>
        _arguments.ShouldBe([
            "-y",
            "-loglevel",
            "error",
            "-i",
            "audio.webm",
            "-af",
            "adelay=0|0",
            "-vn",
            "-ac",
            "1",
            "-ar",
            "16000",
            "-acodec",
            "pcm_s16le",
            "-t",
            "120.000",
            _head,
        ]);

    [Fact]
    public void IsTheUnpaddedConversionWithOnlyTheLimitAdded() =>
        _arguments
            .Where((_, i) => i != _arguments.Count - 3 && i != _arguments.Count - 2)
            .ShouldBe(FFmpegAudioPreparer.BuildPadArguments("audio.webm", 0.0, _head));

    [Fact]
    public void LimitsTheOutputNotTheInput() =>
        _arguments.ToList().IndexOf("-t").ShouldBeGreaterThan(_arguments.ToList().IndexOf("-i"));

    [Fact]
    public void NeverSeeksTheInput() => _arguments.ShouldNotContain("-ss");

    [Fact]
    public void WritesAWav() => _head.ShouldEndWith(".wav");

    [Fact]
    public void WritesInsideTheTemporaryDirectory() =>
        Path.GetDirectoryName(_head).ShouldBe(_scratch.Path);

    [Fact]
    public void BuildsTheSameArgumentsWhenAskedDirectly() =>
        FFmpegAudioPreparer.BuildHeadArguments("audio.webm", 120.0, _head).ShouldBe(_arguments);

    [Fact]
    public void WritesFractionalSecondsToTheMillisecond() =>
        FFmpegAudioPreparer
            .BuildHeadArguments("audio.webm", 90.25, "head.wav")
            .ShouldContain("90.250");

    [Fact]
    public async Task ChoosesAFreshNameEachTime() =>
        (
            await _preparer.ConvertHeadAsync(
                "audio.webm",
                120.0,
                TestContext.Current.CancellationToken
            )
        ).ShouldNotBe(_head);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task RefusesALengthThatIsNotOne(double seconds) =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _preparer.ConvertHeadAsync("audio.webm", seconds, TestContext.Current.CancellationToken)
        );

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RefusesABlankPath(string path) =>
        await Should.ThrowAsync<ArgumentException>(() =>
            _preparer.ConvertHeadAsync(path, 120.0, TestContext.Current.CancellationToken)
        );
}

public sealed class WhenFFmpegFailsWhileConvertingTheHead : IDisposable
{
    private readonly ScratchDirectory _scratch = new();
    private readonly RecordingFFmpegRunner _runner = new();
    private readonly FFmpegAudioPreparer _preparer;

    public WhenFFmpegFailsWhileConvertingTheHead()
    {
        _runner.OnFFmpeg = arguments =>
        {
            File.WriteAllText(arguments[^1], "a truncated render");
            throw new FFmpegException("could not decode", 1, "Invalid data found");
        };
        _preparer = new FFmpegAudioPreparer(_runner, _scratch.Path);
    }

    public void Dispose() => _scratch.Dispose();

    private Task<string> Head() =>
        _preparer.ConvertHeadAsync("audio.webm", 120.0, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SurfacesTheFailure() => await Should.ThrowAsync<FFmpegException>(Head);

    [Fact]
    public async Task LeavesNoHalfWrittenFileBehind()
    {
        await Should.ThrowAsync<FFmpegException>(Head);

        Directory.GetFiles(_scratch.Path).ShouldBeEmpty();
    }
}

/// <summary>
/// The promise the Watch Session's head relies on, checked with the real
/// FFmpeg over the kinds of audio yt-dlp hands back (Opus in WebM, AAC in MP4)
/// and two more: every sample of the head is the whole conversion's sample at
/// the same place, up to its very last one.
/// </summary>
public sealed class WhenTheRealFFmpegConvertsAHead : IDisposable
{
    private const int Rate = 16000;
    private const double SourceSeconds = 150.0;
    private const double HeadSeconds = 120.0;

    private readonly ScratchDirectory _scratch = new();
    private readonly FFmpegRunner _runner = new();
    private readonly FFmpegAudioPreparer _preparer;

    public WhenTheRealFFmpegConvertsAHead() =>
        _preparer = new FFmpegAudioPreparer(_runner, _scratch.Path);

    public void Dispose() => _scratch.Dispose();

    public static TheoryData<string, string, string> Sources =>
        new()
        {
            { "webm", "libopus", "48000" },
            { "m4a", "aac", "44100" },
            { "mp3", "libmp3lame", "44100" },
            { "wav", "pcm_s16le", "22050" },
        };

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task WritesExactlyTheWholeConversionsFirstSamples(
        string extension,
        string codec,
        string rate
    )
    {
        var source = await LongSourceAsync(extension, codec, rate);

        var whole = await _preparer.PadStartAsync(
            source,
            0.0,
            TestContext.Current.CancellationToken
        );
        var head = await _preparer.ConvertHeadAsync(
            source,
            HeadSeconds,
            TestContext.Current.CancellationToken
        );

        var wholeData = PcmData(whole);
        var headData = PcmData(head);

        // -t counts the output's timestamps, and a looped source has gaps in
        // them, so the head can hold a little less than 120 s of samples. What
        // matters is that whatever it holds is the whole file's first samples.
        headData.Length.ShouldBeGreaterThan((int)((HeadSeconds - 2.0) * Rate) * sizeof(short));
        headData.Length.ShouldBeLessThanOrEqualTo((int)(HeadSeconds * Rate) * sizeof(short));
        headData.AsSpan().SequenceEqual(wholeData.AsSpan(0, headData.Length)).ShouldBeTrue();
    }

    [Fact]
    public async Task WritesTheWholeConversionWhenTheSourceIsShorter()
    {
        var source = MediaFixtures.Path("audiobook.m4b");

        var whole = await _preparer.PadStartAsync(
            source,
            0.0,
            TestContext.Current.CancellationToken
        );
        var head = await _preparer.ConvertHeadAsync(
            source,
            HeadSeconds,
            TestContext.Current.CancellationToken
        );

        PcmData(head).ShouldBe(PcmData(whole));
    }

    /// <summary>A 150 s source made by looping a fixture, encoded as asked.</summary>
    private async Task<string> LongSourceAsync(string extension, string codec, string rate)
    {
        var path = _scratch.File($"long.{extension}");
        await _runner.RunFFmpegAsync(
            FFmpegArguments.Quiet(
                "-stream_loop",
                "-1",
                "-i",
                MediaFixtures.Path("audiobook.m4b"),
                "-t",
                Times.ToFixed(SourceSeconds),
                "-vn",
                "-ac",
                "2",
                "-ar",
                rate,
                "-acodec",
                codec,
                path
            ),
            TestContext.Current.CancellationToken
        );
        return path;
    }

    /// <summary>The bytes of a WAV's <c>data</c> chunk, wherever it starts.</summary>
    private static byte[] PcmData(string wav)
    {
        var bytes = File.ReadAllBytes(wav);
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 4));
            if (id == "data")
            {
                return bytes[(position + 8)..Math.Min(bytes.Length, position + 8 + size)];
            }

            position += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"No data chunk in {wav}.");
    }
}
