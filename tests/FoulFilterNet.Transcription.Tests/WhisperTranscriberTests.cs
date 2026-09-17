using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using NSubstitute;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// whisper.cpp reads 16 kHz mono PCM and nothing else, while a job arrives as an
/// mp3, an m4b or a video's extracted AAC track. The conversion is FFmpeg's job
/// (T10), which the transcriber borrows rather than reimplements - and the
/// converted file is a temporary one it owns and must clean up.
/// </summary>
public sealed class WhenTranscribingAMediaFile : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly IAudioPreparer _audio = Substitute.For<IAudioPreparer>();
    private readonly IWhisperEngine _engine = Substitute.For<IWhisperEngine>();
    private readonly TranscriptionResult _heard = new(
        [new Segment(3.41, 3.897, "Damn.")],
        [new Word("damn", 3.41, 3.897)]
    );

    private readonly string _converted;
    private readonly TranscriptionResult _returned;

    public WhenTranscribingAMediaFile()
    {
        _converted = Path.Combine(_directory.Path, "analysis.wav");
        File.WriteAllText(_converted, "riff");

        _audio
            .PadStartAsync("book.m4b", Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_converted));
        _engine
            .TranscribeWavAsync(_converted, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_heard));

        var sut = new WhisperTranscriber(_engine, _audio);
        _returned = sut.TranscribeAsync("book.m4b", TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        _returned.ShouldNotBeNull();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task ConvertsTheAudioWithoutShiftingIt() =>
        await _audio.Received(1).PadStartAsync("book.m4b", 0.0, Arg.Any<CancellationToken>());

    [Fact]
    public async Task HandsTheConvertedAudioToTheEngine() =>
        await _engine.Received(1).TranscribeWavAsync(_converted, Arg.Any<CancellationToken>());

    [Fact]
    public void ReportsExactlyWhatTheEngineHeard() => _returned.ShouldBeSameAs(_heard);

    [Fact]
    public void DeletesTheConvertedAudioAfterwards() => File.Exists(_converted).ShouldBeFalse();
}

/// <summary>
/// A failed inference must not also leak a multi-hundred-megabyte WAV into the
/// temp directory of a machine that processes audiobooks all day.
/// </summary>
public sealed class WhenTheEngineFailsMidTranscription : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly IAudioPreparer _audio = Substitute.For<IAudioPreparer>();
    private readonly IWhisperEngine _engine = Substitute.For<IWhisperEngine>();
    private readonly string _converted;
    private readonly Exception _thrown;

    public WhenTheEngineFailsMidTranscription()
    {
        _converted = Path.Combine(_directory.Path, "analysis.wav");
        File.WriteAllText(_converted, "riff");

        _audio
            .PadStartAsync(Arg.Any<string>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_converted));
        _engine
            .TranscribeWavAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<TranscriptionResult>>(_ =>
                throw new InvalidOperationException("cuda oom")
            );

        var sut = new WhisperTranscriber(_engine, _audio);
        _thrown = Should
            .ThrowAsync<InvalidOperationException>(() =>
                sut.TranscribeAsync("book.m4b", TestContext.Current.CancellationToken)
            )
            .GetAwaiter()
            .GetResult();

        _thrown.ShouldNotBeNull();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void ReportsTheFailureTheJobNeedsToSee() => _thrown.Message.ShouldBe("cuda oom");

    [Fact]
    public void StillDeletesTheConvertedAudio() => File.Exists(_converted).ShouldBeFalse();
}

/// <summary>
/// The Rescan Pass. The padding and the arithmetic both already exist - T10's
/// <c>PadStartAsync</c> and T12's <see cref="RescanPass"/> - so this stage is
/// only responsible for putting them in the right order and handing back
/// timestamps on the original timeline.
/// </summary>
public sealed class WhenRescanningWithShiftedChunkBoundaries : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly IAudioPreparer _audio = Substitute.For<IAudioPreparer>();
    private readonly IWhisperEngine _engine = Substitute.For<IWhisperEngine>();
    private readonly string _padded;
    private readonly TranscriptionResult _returned;

    public WhenRescanningWithShiftedChunkBoundaries()
    {
        _padded = Path.Combine(_directory.Path, "rescan.wav");
        File.WriteAllText(_padded, "riff");

        // What the engine hears on the padded timeline: one segment inside the
        // four seconds of silence, one real one four seconds late.
        _audio
            .PadStartAsync(
                "book.m4b",
                RescanPass.DefaultOffsetSeconds,
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(_padded));
        _engine
            .TranscribeWavAsync(_padded, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    new TranscriptionResult(
                        [new Segment(0.5, 1.5, "(silence)"), new Segment(7.41, 7.897, "Damn.")],
                        [new Word("damn", 7.41, 7.897)]
                    )
                )
            );

        var sut = new WhisperTranscriber(_engine, _audio);
        _returned = sut.TranscribeShiftedAsync(
                "book.m4b",
                RescanPass.DefaultOffsetSeconds,
                TestContext.Current.CancellationToken
            )
            .GetAwaiter()
            .GetResult();

        _returned.ShouldNotBeNull();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task PrependsSilenceOfExactlyTheOffset() =>
        await _audio
            .Received(1)
            .PadStartAsync(
                "book.m4b",
                RescanPass.DefaultOffsetSeconds,
                Arg.Any<CancellationToken>()
            );

    [Fact]
    public void RebasesSegmentsOntoTheOriginalTimeline() =>
        _returned.Segments.ShouldHaveSingleItem().Start.ShouldBe(3.41, 0.001);

    [Fact]
    public void RebasesWordsOntoItToo() =>
        _returned.Words.ShouldHaveSingleItem().Start.ShouldBe(3.41, 0.001);

    [Fact]
    public void DropsWhatWasOnlyHeardInsideThePadding() =>
        _returned.Segments.ShouldNotContain(s => s.Text == "(silence)");

    [Fact]
    public void DeletesThePaddedAudio() => File.Exists(_padded).ShouldBeFalse();
}

/// <summary>
/// The release the pipeline performs after every job. The policy - whether it
/// happens at all - belongs to <see cref="ReleasePolicyTranscriber"/>; this type
/// only forwards.
/// </summary>
public sealed class WhenAJobEndsAndTheModelIsToBeDropped
{
    private readonly IWhisperEngine _engine = Substitute.For<IWhisperEngine>();
    private readonly WhisperTranscriber _sut;

    public WhenAJobEndsAndTheModelIsToBeDropped()
    {
        _sut = new WhisperTranscriber(_engine, Substitute.For<IAudioPreparer>());
        _sut.ReleaseAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task HandsTheVramBack() => await _engine.Received(1).ReleaseAsync();

    [Fact]
    public async Task StaysHappyBeingAskedTwice()
    {
        await _sut.ReleaseAsync();

        await _engine.Received(2).ReleaseAsync();
    }
}

public sealed class WhenATranscriberIsBuiltWithoutCollaborators
{
    private readonly IWhisperEngine _engine = Substitute.For<IWhisperEngine>();
    private readonly IAudioPreparer _audio = Substitute.For<IAudioPreparer>();

    [Fact]
    public void RefusesToRunWithoutAnEngine() =>
        Should.Throw<ArgumentNullException>(() => new WhisperTranscriber(null!, _audio));

    [Fact]
    public void RefusesToRunWithoutAWayToConvertAudio() =>
        Should.Throw<ArgumentNullException>(() => new WhisperTranscriber(_engine, null!));

    [Fact]
    public async Task RefusesABlankAudioPath() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            new WhisperTranscriber(_engine, _audio).TranscribeAsync(
                "  ",
                TestContext.Current.CancellationToken
            )
        );
}
