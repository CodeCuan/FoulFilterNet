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
            NullLogger<WhisperNetEngine>.Instance);

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
            NullLogger<WhisperNetEngine>.Instance);

        _thrown = Should.ThrowAsync<FileNotFoundException>(
            () => sut.TranscribeWavAsync("analysis.wav", TestContext.Current.CancellationToken))
            .GetAwaiter().GetResult();

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
            NullLogger<WhisperNetEngine>.Instance);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.TranscribeWavAsync("analysis.wav", cancelled.Token));

        sut.IsModelLoaded.ShouldBeFalse();
    }
}

public sealed class WhenAnEngineIsBuiltWithoutCollaborators
{
    [Fact]
    public void RefusesMissingOptions() =>
        Should.Throw<ArgumentNullException>(() => new WhisperNetEngine(
            null!, new WhisperModelSource(Path.GetTempPath()), NullLogger<WhisperNetEngine>.Instance));

    [Fact]
    public void RefusesNowhereToFindWeights() =>
        Should.Throw<ArgumentNullException>(() => new WhisperNetEngine(
            new TranscriptionOptions(), null!, NullLogger<WhisperNetEngine>.Instance));
}
