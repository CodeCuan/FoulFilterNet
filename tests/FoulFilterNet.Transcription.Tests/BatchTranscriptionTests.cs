using FoulFilterNet.Domain;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// The batch path on the windowed contract: open the WAV once, hear every
/// window in order at Normal priority, and stitch - which is exactly what the
/// engine used to do inside one call, so a Job's transcript does not change.
/// </summary>
/// <remarks>
/// A minute of audio is three windows, [0, 28], [22, 50] and [32, 60], whose
/// shares meet at 25 and 41. Window 0 and window 1 both hear "both" at 26.2 s;
/// only window 1, whose share holds it, may keep it.
/// </remarks>
public sealed class WhenAWholeWavIsTranscribedInBatch : IAsyncDisposable
{
    private readonly IWhisperEngine _engine = Substitute.For<IWhisperEngine>();
    private readonly ScriptedAudio _audio;
    private readonly TranscriptionResult _result;
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    public WhenAWholeWavIsTranscribedInBatch()
    {
        _audio = new ScriptedAudio(
            60.0,
            index =>
                index switch
                {
                    0 => Join(
                        ScriptedAudio.Said("first", 1.0, 1.5),
                        ScriptedAudio.Said("both", 26.0, 26.4)
                    ),
                    1 => Join(
                        ScriptedAudio.Said("both", 4.0, 4.4),
                        ScriptedAudio.Said("second", 10.0, 10.5)
                    ),
                    _ => ScriptedAudio.Said("third", 20.0, 20.5),
                }
        );
        _engine
            .OpenAsync("analysis.wav", Arg.Any<InferencePriority>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAnalysisAudio>(_audio));

        _result = _engine.TranscribeWavAsync("analysis.wav", _token).Result;

        _result.ShouldNotBeNull();
    }

    public ValueTask DisposeAsync() => _audio.DisposeAsync();

    [Fact]
    public async Task OpensTheWavOnce() =>
        await _engine
            .Received(1)
            .OpenAsync(
                Arg.Any<string>(),
                Arg.Any<InferencePriority>(),
                Arg.Any<CancellationToken>()
            );

    [Fact]
    public async Task OpensItAtNormalPriority() =>
        await _engine
            .Received(1)
            .OpenAsync("analysis.wav", InferencePriority.Normal, Arg.Any<CancellationToken>());

    [Fact]
    public async Task OpensItWithTheCallersToken() =>
        await _engine.Received(1).OpenAsync("analysis.wav", Arg.Any<InferencePriority>(), _token);

    [Fact]
    public void HearsEveryWindowInOrder() => _audio.Asked.ShouldBe([0, 1, 2]);

    [Fact]
    public void HearsEachWindowWithTheCallersToken() => _audio.Tokens.ShouldAllBe(t => t == _token);

    [Fact]
    public void ClosesTheAudio() => _audio.Disposals.ShouldBe(1);

    [Fact]
    public void PutsEveryWordOnTheFilesTimeline() =>
        _result
            .Words.Select(w => (w.Text, w.Start))
            .ShouldBe([("first", 1.0), ("both", 26.0), ("second", 32.0), ("third", 52.0)]);

    [Fact]
    public void PutsEverySegmentOnTheFilesTimelineToo() =>
        _result.Segments.Select(s => s.Start).ShouldBe([1.0, 26.0, 32.0, 52.0]);

    [Fact]
    public void KeepsAWordTwoWindowsHeardOnlyOnce() =>
        _result.Words.Count(w => w.Text == "both").ShouldBe(1);

    [Fact]
    public void ProducesExactlyWhatStitchingTheWindowsGives() =>
        _result.Words.ShouldBe(
            TranscriptionWindows.Stitch([.. _audio.Windows.Select((w, i) => (w, Heard(i)))]).Words
        );

    private static TranscriptionResult Heard(int index) =>
        index switch
        {
            0 => Join(
                ScriptedAudio.Said("first", 1.0, 1.5),
                ScriptedAudio.Said("both", 26.0, 26.4)
            ),
            1 => Join(
                ScriptedAudio.Said("both", 4.0, 4.4),
                ScriptedAudio.Said("second", 10.0, 10.5)
            ),
            _ => ScriptedAudio.Said("third", 20.0, 20.5),
        };

    private static TranscriptionResult Join(TranscriptionResult a, TranscriptionResult b) =>
        new([.. a.Segments, .. b.Segments], [.. a.Words, .. b.Words]);
}

/// <summary>A clip no longer than a window is one window on an unshifted timeline.</summary>
public sealed class WhenAShortWavIsTranscribedInBatch : IAsyncDisposable
{
    private readonly ScriptedAudio _audio = new(8.0, _ => ScriptedAudio.Said("damn", 3.41, 3.897));
    private readonly TranscriptionResult _result;

    public WhenAShortWavIsTranscribedInBatch()
    {
        var engine = Substitute.For<IWhisperEngine>();
        engine
            .OpenAsync(
                Arg.Any<string>(),
                Arg.Any<InferencePriority>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IAnalysisAudio>(_audio));

        _result = engine
            .TranscribeWavAsync("clip.wav", TestContext.Current.CancellationToken)
            .Result;
    }

    public ValueTask DisposeAsync() => _audio.DisposeAsync();

    [Fact]
    public void HearsTheOneWindow() => _audio.Asked.ShouldBe([0]);

    [Fact]
    public void ReportsTheWordWhereItWasHeard() =>
        _result.Words.ShouldHaveSingleItem().ShouldBe(new Word("damn", 3.41, 3.897));

    [Fact]
    public void ReportsTheSegmentWhereItWasHeard() =>
        _result.Segments.ShouldHaveSingleItem().ShouldBe(new Segment(3.41, 3.897, "damn"));
}

/// <summary>
/// A window that fails fails the batch - and the audio is still closed, so the
/// WAV it holds open can be deleted.
/// </summary>
public sealed class WhenABatchWindowFails : IAsyncDisposable
{
    private readonly ScriptedAudio _audio = new(
        60.0,
        index =>
            index == 1
                ? throw new InvalidOperationException("cuda oom")
                : ScriptedAudio.Said("x", 1.0, 1.5)
    );
    private readonly Exception? _thrown;

    public WhenABatchWindowFails()
    {
        var engine = Substitute.For<IWhisperEngine>();
        engine
            .OpenAsync(
                Arg.Any<string>(),
                Arg.Any<InferencePriority>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IAnalysisAudio>(_audio));

        _thrown = Record.Exception(() =>
            engine
                .TranscribeWavAsync("analysis.wav", TestContext.Current.CancellationToken)
                .GetAwaiter()
                .GetResult()
        );
    }

    public ValueTask DisposeAsync() => _audio.DisposeAsync();

    [Fact]
    public void ReportsTheFailure() => _thrown.ShouldBeOfType<InvalidOperationException>();

    [Fact]
    public void StopsAtTheFailedWindow() => _audio.Asked.ShouldBe([0, 1]);

    [Fact]
    public void StillClosesTheAudio() => _audio.Disposals.ShouldBe(1);
}

/// <summary>A Job cancelled between windows stops there and closes the audio.</summary>
public sealed class WhenABatchIsCancelledBetweenWindows : IAsyncDisposable
{
    private readonly ScriptedAudio _audio;
    private readonly Exception? _thrown;

    public WhenABatchIsCancelledBetweenWindows()
    {
        using var abandon = new CancellationTokenSource();
        _audio = new ScriptedAudio(
            60.0,
            index =>
            {
                if (index == 0)
                {
                    abandon.Cancel();
                }

                return ScriptedAudio.Said("x", 1.0, 1.5);
            }
        );
        var engine = Substitute.For<IWhisperEngine>();
        engine
            .OpenAsync(
                Arg.Any<string>(),
                Arg.Any<InferencePriority>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<IAnalysisAudio>(_audio));

        _thrown = Record.Exception(() =>
            engine.TranscribeWavAsync("analysis.wav", abandon.Token).GetAwaiter().GetResult()
        );
    }

    public ValueTask DisposeAsync() => _audio.DisposeAsync();

    [Fact]
    public void ThrowsOperationCanceled() =>
        _thrown.ShouldBeAssignableTo<OperationCanceledException>();

    [Fact]
    public void AsksForNoFurtherWindow() => _audio.Asked.ShouldBe([0]);

    [Fact]
    public void StillClosesTheAudio() => _audio.Disposals.ShouldBe(1);
}

/// <summary>
/// The engine could not open the WAV (the model is not installed, the file is
/// not 16-bit): that is the batch's failure, with nothing opened to close.
/// </summary>
public sealed class WhenTheBatchCannotOpenTheWav
{
    [Fact]
    public async Task ReportsWhyItCouldNot()
    {
        var engine = Substitute.For<IWhisperEngine>();
        engine
            .OpenAsync(
                Arg.Any<string>(),
                Arg.Any<InferencePriority>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(new FileNotFoundException("ggml-base.bin"));

        (
            await Should.ThrowAsync<FileNotFoundException>(() =>
                engine.TranscribeWavAsync("analysis.wav", TestContext.Current.CancellationToken)
            )
        ).Message.ShouldBe("ggml-base.bin");
    }
}

public sealed class WhenABatchIsAskedForWithoutEnoughToGoOn
{
    private readonly IWhisperEngine _engine = Substitute.For<IWhisperEngine>();

    [Fact]
    public async Task RefusesAMissingEngine() =>
        await Should.ThrowAsync<ArgumentNullException>(() =>
            ((IWhisperEngine)null!).TranscribeWavAsync(
                "analysis.wav",
                TestContext.Current.CancellationToken
            )
        );

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RefusesABlankPath(string path) =>
        await Should.ThrowAsync<ArgumentException>(() =>
            _engine.TranscribeWavAsync(path, TestContext.Current.CancellationToken)
        );

    [Fact]
    public async Task OpensNothingForABlankPath()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            _engine.TranscribeWavAsync(" ", TestContext.Current.CancellationToken)
        );

        await _engine
            .DidNotReceiveWithAnyArgs()
            .OpenAsync(default!, default, TestContext.Current.CancellationToken);
    }
}
