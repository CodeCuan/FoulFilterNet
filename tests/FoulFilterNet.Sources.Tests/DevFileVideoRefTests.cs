using FoulFilterNet.Sources;

namespace FoulFilterNet.Sources.Tests;

// W16: the development-only `file` provider. A file video is a separate door
// (TryCreateFile) that the Web host opens only when the dev file provider is
// switched on; TryCreate - the one the endpoints use by default - never
// accepts it.

public class WhenCreatingAFileVideoRef
{
    private readonly VideoRef? _video;

    public WhenCreatingAFileVideoRef()
    {
        VideoRef.TryCreateFile("sample_video.mp4", out _video).ShouldBeTrue();
        _video.ShouldNotBeNull();
    }

    [Fact]
    public void IsTheFileProvider() => _video!.Provider.ShouldBe("file");

    [Fact]
    public void KeepsTheNameExactly() => _video!.Id.ShouldBe("sample_video.mp4");

    [Fact]
    public void IsKeyedByProviderAndName() => _video!.Key.ShouldBe("file-sample_video.mp4");

    [Fact]
    public void IsNotYouTube() => _video!.IsYouTube.ShouldBeFalse();

    [Fact]
    public void HasNoWatchUrl() => Should.Throw<InvalidOperationException>(() => _video!.WatchUrl);

    [Fact]
    public void PrintsAsItsKey() => _video!.ToString().ShouldBe("file-sample_video.mp4");

    [Fact]
    public void EqualsTheSameName()
    {
        VideoRef.TryCreateFile("sample_video.mp4", out var again).ShouldBeTrue();
        again.ShouldBe(_video);
    }

    [Fact]
    public void DiffersFromAnotherCase()
    {
        VideoRef.TryCreateFile("Sample_video.mp4", out var other).ShouldBeTrue();
        other.ShouldNotBe(_video);
    }

    [Fact]
    public void IsNeverAcceptedByTheNetworkFactory() =>
        VideoRef.TryCreate("file", "sample_video.mp4", out _).ShouldBeFalse();

    [Fact]
    public void IsNotAKeyTheParserReadsBack() =>
        VideoRef.TryParseKey("file-sample_video.mp4", out _).ShouldBeFalse();
}

public class WhenAYouTubeRefIsAskedWhetherItIsYouTube
{
    [Fact]
    public void ItIs() => VideoRef.Create("youtube", "dQw4w9WgXcQ").IsYouTube.ShouldBeTrue();
}

public class WhenAFileNameIsAcceptable
{
    [Theory]
    [InlineData("a")]
    [InlineData("sample_video.mp4")]
    [InlineData("clean-speech.mp3")]
    [InlineData("_hidden_but_not_dotted.wav")]
    [InlineData("0.mp3")]
    [InlineData("audio.book.m4b")]
    [InlineData("ABC_def-123.webm")]
    public void ItIsAccepted(string name) => VideoRef.TryCreateFile(name, out _).ShouldBeTrue();

    [Fact]
    public void TheLongestNameIsAccepted() =>
        VideoRef.TryCreateFile(new string('a', VideoRef.MaxFileNameLength), out _).ShouldBeTrue();
}

public class WhenAFileNameIsNotAcceptable
{
    [Theory]
    [InlineData("")]
    [InlineData("-o")]
    [InlineData("--exec")]
    [InlineData("-sample.mp4")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden.mp4")]
    [InlineData("../secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("a/b.mp4")]
    [InlineData("a\\b.mp4")]
    [InlineData("a..b.mp4")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("c:win.ini")]
    [InlineData("/etc/passwd")]
    [InlineData("name with space.mp4")]
    [InlineData("name%2e%2e.mp4")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a|b")]
    [InlineData("tab\there")]
    [InlineData("new\nline")]
    [InlineData("nul\0l")]
    [InlineData("caf\u00e9.mp3")]
    [InlineData("\uff41.mp3")]
    [InlineData(" a.mp3")]
    [InlineData("a.mp3 ")]
    public void ItIsRefused(string name) => VideoRef.TryCreateFile(name, out _).ShouldBeFalse();

    [Fact]
    public void NullIsRefused() => VideoRef.TryCreateFile(null, out _).ShouldBeFalse();

    [Fact]
    public void ATooLongNameIsRefused() =>
        VideoRef
            .TryCreateFile(new string('a', VideoRef.MaxFileNameLength + 1), out _)
            .ShouldBeFalse();

    [Fact]
    public void TheRefusalLeavesNoVideo()
    {
        _ = VideoRef.TryCreateFile("../x", out var video);
        video.ShouldBeNull();
    }
}
