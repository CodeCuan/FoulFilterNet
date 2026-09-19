using Microsoft.Extensions.Logging.Abstractions;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// The three properties T22 requires of a release, checked on the real engine
/// rather than on a substitute - and checkable in CI precisely because nothing
/// is loaded until the first transcription: constructing the engine touches no
/// GPU, no native library and no weights file.
/// </summary>
public sealed class WhenAJobEndsWithoutTheEngineEverLoadingAModel : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly WhisperNetEngine _sut;

    public WhenAJobEndsWithoutTheEngineEverLoadingAModel()
    {
        _sut = new WhisperNetEngine(
            new TranscriptionOptions(),
            new WhisperModelSource(_directory.Path),
            NullLogger<WhisperNetEngine>.Instance
        );

        _sut.ReleaseAsync().AsTask().GetAwaiter().GetResult();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task ReleasesWithoutComplaining() => await _sut.ReleaseAsync();

    [Fact]
    public async Task StaysIdempotentHoweverOftenJobsEnd()
    {
        await _sut.ReleaseAsync();
        await _sut.ReleaseAsync();

        _sut.IsModelLoaded.ShouldBeFalse();
    }

    [Fact]
    public void NeverClaimedToHoldAModel() => _sut.IsModelLoaded.ShouldBeFalse();

    [Fact]
    public void FetchedNoWeightsOnTheWayPast() =>
        Directory.GetFiles(_directory.Path).ShouldBeEmpty();
}

/// <summary>
/// A machine with no weights installed and no way to fetch them: the engine has
/// to fail with the file an operator can go and install, which it can only do
/// before it reaches the native library.
/// </summary>
public sealed class WhenTheConfiguredModelIsNotInstalled : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly FileNotFoundException _thrown;

    public WhenTheConfiguredModelIsNotInstalled()
    {
        var sut = new WhisperNetEngine(
            new TranscriptionOptions { Model = "base" },
            new WhisperModelSource(_directory.Path),
            NullLogger<WhisperNetEngine>.Instance
        );

        _thrown = Should
            .ThrowAsync<FileNotFoundException>(() =>
                sut.TranscribeWavAsync("analysis.wav", TestContext.Current.CancellationToken)
            )
            .GetAwaiter()
            .GetResult();

        _thrown.ShouldNotBeNull();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void NamesTheWeightsFileItWanted() => _thrown.Message.ShouldContain("ggml-base.bin");
}

/// <summary>
/// A job cancelled while it waited for the GPU stops there rather than loading
/// a model it will never use - the same reason T19's store lets cancellation
/// through when everything else is a cache miss.
/// </summary>
public sealed class WhenAJobIsCancelledBeforeTranscriptionBegins : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task StopsInsteadOfLoadingAModel()
    {
        var sut = new WhisperNetEngine(
            new TranscriptionOptions(),
            new WhisperModelSource(_directory.Path),
            NullLogger<WhisperNetEngine>.Instance
        );
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            sut.TranscribeWavAsync("analysis.wav", cancelled.Token)
        );

        sut.IsModelLoaded.ShouldBeFalse();
    }
}

public sealed class WhenAnEngineIsBuiltWithoutCollaborators
{
    [Fact]
    public void RefusesMissingOptions() =>
        Should.Throw<ArgumentNullException>(() =>
            new WhisperNetEngine(
                null!,
                new WhisperModelSource(Path.GetTempPath()),
                NullLogger<WhisperNetEngine>.Instance
            )
        );

    [Fact]
    public void RefusesNowhereToFindWeights() =>
        Should.Throw<ArgumentNullException>(() =>
            new WhisperNetEngine(
                new TranscriptionOptions(),
                null!,
                NullLogger<WhisperNetEngine>.Instance
            )
        );
}

/// <summary>
/// Opening is where the model loads, so everything that used to stop a
/// transcription before the GPU was touched stops an open the same way.
/// </summary>
public sealed class WhenAudioIsOpenedWithoutAUsableModel : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly WhisperNetEngine _sut;

    public WhenAudioIsOpenedWithoutAUsableModel() =>
        _sut = new WhisperNetEngine(
            new TranscriptionOptions { Model = "base" },
            new WhisperModelSource(_directory.Path),
            NullLogger<WhisperNetEngine>.Instance
        );

    public void Dispose()
    {
        _sut.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task NamesTheWeightsFileItWanted() =>
        (
            await Should.ThrowAsync<FileNotFoundException>(() =>
                _sut.OpenAsync(
                    "analysis.wav",
                    InferencePriority.High,
                    TestContext.Current.CancellationToken
                )
            )
        ).Message.ShouldContain("ggml-base.bin");

    [Fact]
    public async Task StopsBeforeLoadingWhenAlreadyCancelled() =>
        await Should.ThrowAsync<OperationCanceledException>(() =>
            _sut.OpenAsync(
                "analysis.wav",
                InferencePriority.Normal,
                new CancellationToken(canceled: true)
            )
        );

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RefusesABlankPath(string path) =>
        await Should.ThrowAsync<ArgumentException>(() =>
            _sut.OpenAsync(path, InferencePriority.Normal, TestContext.Current.CancellationToken)
        );

    [Fact]
    public async Task RefusesAnUnknownPriority() =>
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.OpenAsync(
                "analysis.wav",
                (InferencePriority)7,
                TestContext.Current.CancellationToken
            )
        );

    [Fact]
    public async Task StillReleasesCleanlyAfterAFailedOpen()
    {
        await Should.ThrowAsync<FileNotFoundException>(() =>
            _sut.OpenAsync(
                "analysis.wav",
                InferencePriority.Normal,
                TestContext.Current.CancellationToken
            )
        );

        await _sut.ReleaseAsync();

        _sut.IsModelLoaded.ShouldBeFalse();
    }
}

/// <summary>
/// A warm-up is the open's model load on its own (W17): it fails the same way
/// an open would, and stops before loading when it is already cancelled.
/// </summary>
public sealed class WhenTheModelIsWarmedUpWithoutUsableWeights : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly WhisperNetEngine _sut;

    public WhenTheModelIsWarmedUpWithoutUsableWeights() =>
        _sut = new WhisperNetEngine(
            new TranscriptionOptions { Model = "base" },
            new WhisperModelSource(_directory.Path),
            NullLogger<WhisperNetEngine>.Instance
        );

    public void Dispose()
    {
        _sut.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task NamesTheWeightsFileItWanted() =>
        (
            await Should.ThrowAsync<FileNotFoundException>(() =>
                _sut.WarmUpAsync(TestContext.Current.CancellationToken)
            )
        ).Message.ShouldContain("ggml-base.bin");

    [Fact]
    public async Task StopsBeforeLoadingWhenAlreadyCancelled() =>
        await Should.ThrowAsync<OperationCanceledException>(() =>
            _sut.WarmUpAsync(new CancellationToken(canceled: true))
        );

    [Fact]
    public async Task LeavesNoModelBehindWhenCancelled()
    {
        await Should.ThrowAsync<OperationCanceledException>(() =>
            _sut.WarmUpAsync(new CancellationToken(canceled: true))
        );

        _sut.IsModelLoaded.ShouldBeFalse();
    }

    [Fact]
    public async Task LeavesNoModelBehindWhenTheWeightsAreMissing()
    {
        await Should.ThrowAsync<FileNotFoundException>(() =>
            _sut.WarmUpAsync(TestContext.Current.CancellationToken)
        );

        _sut.IsModelLoaded.ShouldBeFalse();
    }

    [Fact]
    public async Task StillReleasesCleanlyAfterAFailedWarmUp()
    {
        await Should.ThrowAsync<FileNotFoundException>(() =>
            _sut.WarmUpAsync(TestContext.Current.CancellationToken)
        );

        await _sut.ReleaseAsync();

        _sut.IsModelLoaded.ShouldBeFalse();
    }

    [Fact]
    public async Task LetsAnOpenFailTheSameWayAfterwards()
    {
        await Should.ThrowAsync<FileNotFoundException>(() =>
            _sut.WarmUpAsync(TestContext.Current.CancellationToken)
        );

        await Should.ThrowAsync<FileNotFoundException>(() =>
            _sut.OpenAsync(
                "analysis.wav",
                InferencePriority.High,
                TestContext.Current.CancellationToken
            )
        );
    }
}
