using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;
using NSubstitute;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// UNLOAD_MODELS_AFTER_JOB is on: the pipeline's unconditional release reaches
/// the engine and the model goes.
/// </summary>
public sealed class WhenUnloadingAfterEveryJobIsRequested
{
    private readonly ITranscriber _engine = Substitute.For<ITranscriber>();
    private readonly ReleasePolicyTranscriber _sut;

    public WhenUnloadingAfterEveryJobIsRequested()
    {
        _sut = new ReleasePolicyTranscriber(
            _engine,
            new TranscriptionOptions { UnloadAfterJob = true }
        );
        _sut.ReleaseAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task DropsTheModel() => await _engine.Received(1).ReleaseAsync();

    [Fact]
    public async Task DropsItAgainWheneverItIsAskedTo()
    {
        await _sut.ReleaseAsync();

        await _engine.Received(2).ReleaseAsync();
    }
}

/// <summary>
/// The flag off is the default, and it is the whole reason this type exists: on
/// a dedicated machine a resident model is faster, so the release the pipeline
/// always performs has to become a no-op here rather than a branch in there.
/// </summary>
public sealed class WhenTheModelIsMeantToStayResident
{
    private readonly ITranscriber _engine = Substitute.For<ITranscriber>();
    private readonly ReleasePolicyTranscriber _sut;

    public WhenTheModelIsMeantToStayResident()
    {
        _sut = new ReleasePolicyTranscriber(_engine, new TranscriptionOptions());

        new TranscriptionOptions().UnloadAfterJob.ShouldBeFalse();
        _sut.ReleaseAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task NeverDropsTheModel() => await _engine.DidNotReceive().ReleaseAsync();

    [Fact]
    public async Task StaysANoOpHoweverOftenTheJobEnds()
    {
        await _sut.ReleaseAsync();
        await _sut.ReleaseAsync();

        await _engine.DidNotReceive().ReleaseAsync();
    }
}

/// <summary>
/// Everything that is not the release is the engine's, verbatim: the policy is
/// a decorator, not a layer with opinions about transcription.
/// </summary>
public sealed class WhenTranscribingThroughTheReleasePolicy
{
    private readonly ITranscriber _engine = Substitute.For<ITranscriber>();
    private readonly TranscriptionResult _heard = new(
        [new Segment(1.0, 1.5, "damn")],
        [new Word("damn", 1.0, 1.5)]
    );

    private readonly ReleasePolicyTranscriber _sut;
    private readonly TranscriptionResult _returned;

    public WhenTranscribingThroughTheReleasePolicy()
    {
        _engine
            .TranscribeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_heard));

        _sut = new ReleasePolicyTranscriber(
            _engine,
            new TranscriptionOptions { UnloadAfterJob = true }
        );
        _returned = _sut.TranscribeAsync("book.wav", TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        _returned.ShouldNotBeNull();
    }

    [Fact]
    public void HandsBackExactlyWhatTheEngineHeard() => _returned.ShouldBeSameAs(_heard);

    [Fact]
    public async Task AsksTheEngineAboutTheAudioItWasGiven() =>
        await _engine.Received(1).TranscribeAsync("book.wav", Arg.Any<CancellationToken>());

    [Fact]
    public async Task ReleasesNothingJustForTranscribing() =>
        await _engine.DidNotReceive().ReleaseAsync();
}

/// <summary>The Rescan Pass goes straight through as well, offset and all.</summary>
public sealed class WhenRescanningThroughTheReleasePolicy
{
    private readonly ITranscriber _engine = Substitute.For<ITranscriber>();
    private readonly TranscriptionResult _heard = new([new Segment(5.0, 5.4, "hell")], []);
    private readonly ReleasePolicyTranscriber _sut;
    private readonly TranscriptionResult _returned;

    public WhenRescanningThroughTheReleasePolicy()
    {
        _engine
            .TranscribeShiftedAsync(
                Arg.Any<string>(),
                Arg.Any<double>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(_heard));

        _sut = new ReleasePolicyTranscriber(_engine, new TranscriptionOptions());
        _returned = _sut.TranscribeShiftedAsync(
                "book.wav",
                RescanPass.DefaultOffsetSeconds,
                TestContext.Current.CancellationToken
            )
            .GetAwaiter()
            .GetResult();

        _returned.ShouldNotBeNull();
    }

    [Fact]
    public void HandsBackExactlyWhatTheSecondPassHeard() => _returned.ShouldBeSameAs(_heard);

    [Fact]
    public async Task PassesTheOffsetThroughUntouched() =>
        await _engine
            .Received(1)
            .TranscribeShiftedAsync(
                "book.wav",
                RescanPass.DefaultOffsetSeconds,
                Arg.Any<CancellationToken>()
            );
}

public sealed class WhenTheReleasePolicyIsBuiltWithoutCollaborators
{
    private readonly ITranscriber _engine = Substitute.For<ITranscriber>();

    [Fact]
    public void RefusesToWrapNothing() =>
        Should.Throw<ArgumentNullException>(() =>
            new ReleasePolicyTranscriber(null!, new TranscriptionOptions())
        );

    [Fact]
    public void RefusesToGuessThePolicy() =>
        Should.Throw<ArgumentNullException>(() => new ReleasePolicyTranscriber(_engine, null!));
}
