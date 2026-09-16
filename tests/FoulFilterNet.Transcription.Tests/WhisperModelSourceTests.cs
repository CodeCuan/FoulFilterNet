using Whisper.net.Ggml;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// Weights are large, gitignored, and never committed, so the engine has to be
/// able to find them - and, when it cannot, to say exactly what it looked for.
/// Nothing here reaches the network: the acquisition step is a delegate the
/// caller supplies, which is what lets CI exercise it with bytes it made up.
/// </summary>
public sealed class WhenTheWeightsAreAlreadyOnDisk : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly List<GgmlType> _asked = [];
    private readonly string _expected;
    private readonly string _resolved;

    public WhenTheWeightsAreAlreadyOnDisk()
    {
        _expected = Path.Combine(_directory.Path, "ggml-base.bin");
        File.WriteAllText(_expected, "pretend weights");

        var sut = new WhisperModelSource(_directory.Path, Acquire);
        _resolved = sut.ResolveAsync(ModelNames.Default, TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();

        _resolved.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void HandsBackTheFileItFound() => _resolved.ShouldBe(_expected);

    [Fact]
    public void NeverGoesLookingOnTheNetwork() => _asked.ShouldBeEmpty();

    [Fact]
    public void LeavesTheWeightsExactlyAsTheyWere() =>
        File.ReadAllText(_resolved).ShouldBe("pretend weights");

    private Task<Stream> Acquire(GgmlType type, CancellationToken cancellationToken)
    {
        _asked.Add(type);
        return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    }
}

/// <summary>
/// The offline case: no weights and no way to fetch them. A job must fail with
/// the path an operator can act on rather than with a native load error.
/// </summary>
public sealed class WhenTheWeightsAreMissingAndNothingMayFetchThem : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly FileNotFoundException _thrown;

    public WhenTheWeightsAreMissingAndNothingMayFetchThem()
    {
        var sut = new WhisperModelSource(_directory.Path);

        _thrown = Should.ThrowAsync<FileNotFoundException>(
            () => sut.ResolveAsync(ModelNames.Default, TestContext.Current.CancellationToken))
            .GetAwaiter().GetResult();

        _thrown.ShouldNotBeNull();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void SaysWhichFileItExpected() => _thrown.Message.ShouldContain("ggml-base.bin");

    [Fact]
    public void SaysWhereItLookedForIt() => _thrown.Message.ShouldContain(_directory.Path);

    [Fact]
    public void DownloadsNothingBehindTheOperatorsBack() =>
        Directory.GetFiles(_directory.Path).ShouldBeEmpty();
}

/// <summary>
/// Acquisition, with the download itself stubbed out. The file has to land
/// where the next run will look for it, and it has to land whole - a job killed
/// mid-download must not leave a truncated model that loads and mis-transcribes.
/// </summary>
public sealed class WhenTheWeightsHaveToBeAcquired : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly List<GgmlType> _asked = [];
    private readonly string _resolved;

    public WhenTheWeightsHaveToBeAcquired()
    {
        var sut = new WhisperModelSource(_directory.Path, Acquire);
        _resolved = sut.ResolveAsync("openai/whisper-large-v3-turbo", TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult();

        _resolved.ShouldNotBeNullOrWhiteSpace();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AsksForTheModelThatWasConfigured() => _asked.ShouldBe([GgmlType.LargeV3Turbo]);

    [Fact]
    public void WritesItWhereItWillBeFoundNextTime() =>
        _resolved.ShouldBe(Path.Combine(_directory.Path, "ggml-large-v3-turbo.bin"));

    [Fact]
    public void WritesEveryByteItWasGiven() =>
        File.ReadAllBytes(_resolved).ShouldBe([7, 8, 9]);

    [Fact]
    public void LeavesNoHalfWrittenFileBeside() =>
        Directory.GetFiles(_directory.Path).Length.ShouldBe(1);

    [Fact]
    public async Task CreatesTheDirectoryIfTheMachineHadNone()
    {
        using var parent = new TempDirectory();
        var nested = Path.Combine(parent.Path, "models");

        await new WhisperModelSource(nested, Acquire)
            .ResolveAsync(ModelNames.Default, TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(nested, "ggml-base.bin")).ShouldBeTrue();
    }

    private Task<Stream> Acquire(GgmlType type, CancellationToken cancellationToken)
    {
        _asked.Add(type);
        return Task.FromResult<Stream>(new MemoryStream([7, 8, 9]));
    }
}

/// <summary>
/// A model Whisper.net has no <c>GgmlType</c> for cannot be fetched at all, so
/// saying so beats attempting a download that could never have worked.
/// </summary>
public sealed class WhenAThirdPartyModelIsNotInstalled : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Exception _thrown;

    public WhenAThirdPartyModelIsNotInstalled()
    {
        var sut = new WhisperModelSource(
            _directory.Path,
            (_, _) => Task.FromResult<Stream>(new MemoryStream([1])));

        _thrown = Should.ThrowAsync<InvalidOperationException>(
            () => sut.ResolveAsync("distil-whisper/distil-large-v3", TestContext.Current.CancellationToken))
            .GetAwaiter().GetResult();

        _thrown.ShouldNotBeNull();
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void NamesTheModelItCannotFetch() =>
        _thrown.Message.ShouldContain("distil-whisper/distil-large-v3");

    [Fact]
    public void SaysWhichFileToInstallInstead() => _thrown.Message.ShouldContain("ggml-distil-large-v3.bin");
}

/// <summary>A failed acquisition leaves the machine as it found it.</summary>
public sealed class WhenAcquiringTheWeightsFails : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task LetsTheFailureThrough()
    {
        var sut = new WhisperModelSource(_directory.Path, (_, _) => throw new HttpRequestException("no route"));

        await Should.ThrowAsync<HttpRequestException>(
            () => sut.ResolveAsync(ModelNames.Default, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LeavesNoPartialWeightsBehind()
    {
        var sut = new WhisperModelSource(_directory.Path, (_, _) => throw new HttpRequestException("no route"));

        await Should.ThrowAsync<HttpRequestException>(
            () => sut.ResolveAsync(ModelNames.Default, TestContext.Current.CancellationToken));

        Directory.GetFiles(_directory.Path).ShouldBeEmpty();
    }
}

public sealed class WhenAModelSourceIsBuiltWithoutADirectory
{
    [Fact]
    public void RefusesAMissingDirectory() =>
        Should.Throw<ArgumentException>(() => new WhisperModelSource("   "));
}

/// <summary>
/// Weights are too large to live in the repository, so where they are is
/// configuration - with a default, because the common case is one directory
/// beside the application.
/// </summary>
public sealed class WhenConfigurationSaysNothingAboutWhereWeightsLive
{
    [Fact]
    public void LooksInTheConventionalModelsDirectory() =>
        new TranscriptionOptions().ModelDirectory.ShouldBe(WhisperModelSource.DefaultDirectory);

    [Fact]
    public void UsesWhateverAnOperatorConfiguredInstead() =>
        new TranscriptionOptions { ModelDirectory = "D:/weights" }.ModelDirectory.ShouldBe("D:/weights");

    [Fact]
    public void TreatsABlankSettingAsUnconfigured() =>
        new TranscriptionOptions { ModelDirectory = "  " }.ModelDirectory
            .ShouldBe(WhisperModelSource.DefaultDirectory);
}
