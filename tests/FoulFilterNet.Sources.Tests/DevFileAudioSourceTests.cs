using FoulFilterNet.Sources;
using NSubstitute;

namespace FoulFilterNet.Sources.Tests;

/// <summary>A throwaway directory standing in for the dev file directory.</summary>
internal sealed class DevFiles : IDisposable
{
    public DevFiles()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "ffn-devfiles-" + Guid.NewGuid().ToString("N")[..8]
        );
        Media = Path.Combine(Root, "media");
        Scratch = Path.Combine(Root, "scratch", "session");
        Directory.CreateDirectory(Media);
    }

    public string Root { get; }

    /// <summary>The configured directory.</summary>
    public string Media { get; }

    /// <summary>A session's scratch directory (not created).</summary>
    public string Scratch { get; }

    public string Add(string name, string content = "media bytes")
    {
        var path = Path.Combine(Media, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
    }
}

public class WhenListingTheDevFileDirectory : IDisposable
{
    private readonly DevFiles _files = new();
    private readonly DevFileDirectory _directory;

    public WhenListingTheDevFileDirectory()
    {
        _files.Add("sample_video.mp4");
        _files.Add("clean_speech.mp3");
        _files.Add("manifest.json");
        _files.Add(".hidden.mp4");
        _files.Add("-looks-like-an-option.mp4");
        _files.Add("has space.mp3");
        Directory.CreateDirectory(Path.Combine(_files.Media, "nested"));
        File.WriteAllText(Path.Combine(_files.Media, "nested", "deep.mp3"), "x");

        _directory = new DevFileDirectory(_files.Media);
    }

    [Fact]
    public void ListsOnlyTopLevelFilesWithAcceptableNamesInOrdinalOrder() =>
        _directory.Names.ShouldBe(["clean_speech.mp3", "manifest.json", "sample_video.mp4"]);

    [Fact]
    public void ResolvesAListedName() =>
        _directory.TryResolve("sample_video.mp4", out _).ShouldBeTrue();

    [Fact]
    public void ResolvesToTheFileInsideTheDirectory()
    {
        _directory.TryResolve("sample_video.mp4", out var path);
        path.ShouldBe(Path.Combine(Path.GetFullPath(_files.Media), "sample_video.mp4"));
    }

    [Fact]
    public void DoesNotResolveAnUnlistedName() =>
        _directory.TryResolve("missing.mp4", out _).ShouldBeFalse();

    [Fact]
    public void DoesNotResolveAnotherCase() =>
        _directory.TryResolve("SAMPLE_VIDEO.MP4", out _).ShouldBeFalse();

    [Fact]
    public void DoesNotResolveAFileWithAnUnacceptableName() =>
        _directory.TryResolve("-looks-like-an-option.mp4", out _).ShouldBeFalse();

    [Fact]
    public void DoesNotResolveANestedFile() =>
        _directory.TryResolve("nested/deep.mp3", out _).ShouldBeFalse();

    [Fact]
    public void DoesNotResolveTraversal() =>
        _directory.TryResolve("../media/sample_video.mp4", out _).ShouldBeFalse();

    [Fact]
    public void DoesNotResolveNull() => _directory.TryResolve(null, out _).ShouldBeFalse();

    [Fact]
    public void SeesAFileAddedLater()
    {
        _files.Add("later.mp3");
        _directory.TryResolve("later.mp3", out _).ShouldBeTrue();
    }

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenTheDevFileDirectoryDoesNotExist
{
    private readonly DevFileDirectory _directory = new(
        Path.Combine(Path.GetTempPath(), "ffn-missing-" + Guid.NewGuid().ToString("N"))
    );

    [Fact]
    public void ListsNothing() => _directory.Names.ShouldBeEmpty();

    [Fact]
    public void ResolvesNothing() => _directory.TryResolve("a.mp3", out _).ShouldBeFalse();

    [Fact]
    public void SaysItDoesNotExist() => _directory.Exists.ShouldBeFalse();
}

public class WhenTheDevFileDirectoryIsBlank
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ItIsRefused(string directory) =>
        Should.Throw<ArgumentException>(() => new DevFileDirectory(directory));
}

public class WhenFetchingADevFile : IDisposable
{
    private readonly DevFiles _files = new();
    private readonly IWebAudioSource _inner = Substitute.For<IWebAudioSource>();
    private readonly VideoRef _video;
    private readonly WebAudio _audio;

    public WhenFetchingADevFile()
    {
        _files.Add("sample_video.mp4", "the fixture");
        VideoRef.TryCreateFile("sample_video.mp4", out var video).ShouldBeTrue();
        _video = video!;

        var source = new DevFileAudioSource(_inner, new DevFileDirectory(_files.Media));
        _audio = source
            .FetchAudioAsync(_video, _files.Scratch, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void IsForTheVideo() => _audio.Video.ShouldBe(_video);

    [Fact]
    public void CopiesTheFileIntoTheScratchDirectoryAsAudio() =>
        _audio.AudioPath.ShouldBe(Path.Combine(_files.Scratch, "audio.mp4"));

    [Fact]
    public void CopiesTheBytes() => File.ReadAllText(_audio.AudioPath).ShouldBe("the fixture");

    [Fact]
    public void LeavesTheOriginalInPlace() =>
        File.Exists(Path.Combine(_files.Media, "sample_video.mp4")).ShouldBeTrue();

    [Fact]
    public void ReportsTheExtension() => _audio.Extension.ShouldBe("mp4");

    [Fact]
    public void IsTitledWithTheName() => _audio.Title.ShouldBe("sample_video.mp4");

    [Fact]
    public void LeavesTheDurationToTheWav() => _audio.DurationSeconds.ShouldBeNull();

    [Fact]
    public void NeverWarnsAboutAJavaScriptRuntime() => _audio.JsRuntimeMissing.ShouldBeFalse();

    [Fact]
    public void NeverAsksYtDlp() => _inner.ReceivedCalls().ShouldBeEmpty();

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenFetchingADevFileIntoADirectoryWithOldAudio : IDisposable
{
    private readonly DevFiles _files = new();
    private readonly WebAudio _audio;

    public WhenFetchingADevFileIntoADirectoryWithOldAudio()
    {
        _files.Add("clip.mp3", "new");
        Directory.CreateDirectory(_files.Scratch);
        File.WriteAllText(Path.Combine(_files.Scratch, "audio.webm"), "old");
        File.WriteAllText(Path.Combine(_files.Scratch, "keep.txt"), "other");
        VideoRef.TryCreateFile("clip.mp3", out var video);

        _audio = new DevFileAudioSource(
            Substitute.For<IWebAudioSource>(),
            new DevFileDirectory(_files.Media)
        )
            .FetchAudioAsync(video!, _files.Scratch, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void ReplacesEarlierAudio() =>
        File.Exists(Path.Combine(_files.Scratch, "audio.webm")).ShouldBeFalse();

    [Fact]
    public void LeavesOtherFilesAlone() =>
        File.Exists(Path.Combine(_files.Scratch, "keep.txt")).ShouldBeTrue();

    [Fact]
    public void WritesTheNewAudio() => File.ReadAllText(_audio.AudioPath).ShouldBe("new");

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenFetchingADevFileWithNoExtension : IDisposable
{
    private readonly DevFiles _files = new();
    private readonly WebAudio _audio;

    public WhenFetchingADevFileWithNoExtension()
    {
        _files.Add("noext");
        VideoRef.TryCreateFile("noext", out var video);

        _audio = new DevFileAudioSource(
            Substitute.For<IWebAudioSource>(),
            new DevFileDirectory(_files.Media)
        )
            .FetchAudioAsync(video!, _files.Scratch, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void NamesItAudioWithABinExtension() =>
        _audio.AudioPath.ShouldBe(Path.Combine(_files.Scratch, "audio.bin"));

    [Fact]
    public void ReportsTheBinExtension() => _audio.Extension.ShouldBe("bin");

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenTheDevFileIsNotInTheDirectory : IDisposable
{
    private readonly DevFiles _files = new();
    private readonly VideoRef _video;
    private readonly WebAudioException _error;

    public WhenTheDevFileIsNotInTheDirectory()
    {
        VideoRef.TryCreateFile("missing.mp4", out var video);
        _video = video!;

        _error = Should.Throw<WebAudioException>(() =>
            new DevFileAudioSource(
                Substitute.For<IWebAudioSource>(),
                new DevFileDirectory(_files.Media)
            ).FetchAudioAsync(_video, _files.Scratch, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public void IsUnavailable() => _error.Kind.ShouldBe(WebAudioFailure.Unavailable);

    [Fact]
    public void NamesTheVideo() => _error.Video.ShouldBe(_video);

    [Fact]
    public void SaysWhichFileInTheReason() => _error.Reason.ShouldContain("missing.mp4");

    [Fact]
    public void DoesNotRevealTheDirectoryInTheReason() =>
        _error.Reason.ShouldNotContain(_files.Media);

    [Fact]
    public void WritesNothing() => Directory.Exists(_files.Scratch).ShouldBeFalse();

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenFetchingADevFileIsCancelled : IDisposable
{
    private readonly DevFiles _files = new();

    public WhenFetchingADevFileIsCancelled()
    {
        _files.Add("clip.mp3", new string('x', 1024));
        VideoRef.TryCreateFile("clip.mp3", out var video);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Should.Throw<OperationCanceledException>(() =>
            new DevFileAudioSource(
                Substitute.For<IWebAudioSource>(),
                new DevFileDirectory(_files.Media)
            ).FetchAudioAsync(video!, _files.Scratch, cancelled.Token)
        );
    }

    [Fact]
    public void LeavesNoAudioBehind() =>
        (
            Directory.Exists(_files.Scratch) ? Directory.GetFiles(_files.Scratch, "audio.*") : []
        ).ShouldBeEmpty();

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenTheDevFileSourceIsAskedForAYouTubeVideo : IDisposable
{
    private readonly DevFiles _files = new();
    private readonly IWebAudioSource _inner = Substitute.For<IWebAudioSource>();
    private readonly VideoRef _video = VideoRef.Create("youtube", "jNQXAC9IVRw");
    private readonly WebAudio _expected;
    private readonly WebAudio _audio;

    public WhenTheDevFileSourceIsAskedForAYouTubeVideo()
    {
        _expected = new WebAudio(
            _video,
            "Me at the zoo",
            19,
            "x/audio.webm",
            "webm",
            "251",
            "opus",
            false
        );
        _inner.FetchAudioAsync(_video, "scratch", Arg.Any<CancellationToken>()).Returns(_expected);

        _audio = new DevFileAudioSource(_inner, new DevFileDirectory(_files.Media))
            .FetchAudioAsync(_video, "scratch", TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    [Fact]
    public void HandsItToTheRealSource() => _audio.ShouldBeSameAs(_expected);

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenTheDevFileSourceIsAskedWhetherWebVideoIsAvailable : IDisposable
{
    private readonly DevFiles _files = new();
    private readonly WebVideoAvailability _expected = new("2026.09.01", null);
    private readonly WebVideoAvailability _availability;

    public WhenTheDevFileSourceIsAskedWhetherWebVideoIsAvailable()
    {
        var inner = Substitute.For<IWebAudioSource>();
        inner.CheckAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(_expected);

        _availability = new DevFileAudioSource(inner, new DevFileDirectory(_files.Media))
            .CheckAvailabilityAsync(TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// <c>/config</c> keeps describing YouTube: the dev provider is not a
    /// reason to say web video works.
    /// </summary>
    [Fact]
    public void AnswersWithTheRealSourcesAvailability() => _availability.ShouldBeSameAs(_expected);

    public void Dispose()
    {
        _files.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenYtDlpIsAskedForAFileVideo
{
    private readonly WebAudioException _error;

    public WhenYtDlpIsAskedForAFileVideo()
    {
        VideoRef.TryCreateFile("clip.mp3", out var video);
        var runner = new FakeProcessRunner();

        _error = Should.Throw<WebAudioException>(() =>
            new YtDlpAudioSource(runner, new SourcesOptions()).FetchAudioAsync(
                video!,
                Path.Combine(Path.GetTempPath(), "ffn-never-" + Guid.NewGuid().ToString("N")),
                TestContext.Current.CancellationToken
            )
        );
        runner.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void RefusesItAsUnsupported() => _error.Kind.ShouldBe(WebAudioFailure.Unsupported);

    [Fact]
    public void SaysWhy() => _error.Reason.ShouldContain("YouTube");
}
