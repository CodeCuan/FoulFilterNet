using FoulFilterNet.Sources;

namespace FoulFilterNet.Sources.Tests;

public class WhenCreatingAYouTubeVideoRef
{
    private readonly VideoRef? _video;

    public WhenCreatingAYouTubeVideoRef()
    {
        VideoRef.TryCreate("youtube", "dQw4w9WgXcQ", out _video).ShouldBeTrue();
        _video.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsTheProvider() => _video!.Provider.ShouldBe("youtube");

    [Fact]
    public void KeepsTheIdExactly() => _video!.Id.ShouldBe("dQw4w9WgXcQ");

    [Fact]
    public void HasTheCanonicalKey() => _video!.Key.ShouldBe("youtube-dQw4w9WgXcQ");

    [Fact]
    public void HasTheCanonicalWatchUrl() =>
        _video!.WatchUrl.ShouldBe("https://www.youtube.com/watch?v=dQw4w9WgXcQ");

    [Fact]
    public void PrintsAsItsKey() => _video!.ToString().ShouldBe("youtube-dQw4w9WgXcQ");

    [Fact]
    public void MatchesTheThrowingFactory() =>
        VideoRef.Create("youtube", "dQw4w9WgXcQ").ShouldBe(_video);
}

public class WhenTheProviderIsNotLowerCase
{
    private readonly VideoRef? _video;

    public WhenTheProviderIsNotLowerCase()
    {
        VideoRef.TryCreate("YouTube", "dQw4w9WgXcQ", out _video).ShouldBeTrue();
        _video.ShouldNotBeNull();
    }

    [Fact]
    public void CanonicalisesTheProviderToLowerCase() => _video!.Provider.ShouldBe("youtube");

    [Fact]
    public void UsesTheLowerCaseProviderInTheKey() => _video!.Key.ShouldBe("youtube-dQw4w9WgXcQ");

    [Fact]
    public void EqualsTheLowerCaseRef() =>
        _video.ShouldBe(VideoRef.Create("youtube", "dQw4w9WgXcQ"));

    [Fact]
    public void AcceptsUpperCaseToo() =>
        VideoRef.Create("YOUTUBE", "dQw4w9WgXcQ").Provider.ShouldBe("youtube");
}

public class WhenTheIdIsValid
{
    [Theory]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("-abcdefghij")]
    [InlineData("_abcdefghij")]
    [InlineData("--exec_calc")]
    [InlineData("-----------")]
    [InlineData("___________")]
    [InlineData("00000000000")]
    [InlineData("abcdefghij-")]
    [InlineData("ABCDEFGHIJK")]
    [InlineData("zZ9_-aA0-_z")]
    public void IsAccepted(string id) => VideoRef.TryCreate("youtube", id, out _).ShouldBeTrue();

    [Theory]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("-abcdefghij")]
    [InlineData("_abcdefghij")]
    public void RoundTripsTheId(string id) => VideoRef.Create("youtube", id).Id.ShouldBe(id);

    [Fact]
    public void KeepsALeadingDashInTheWatchUrl() =>
        VideoRef
            .Create("youtube", "-abcdefghij")
            .WatchUrl.ShouldBe("https://www.youtube.com/watch?v=-abcdefghij");

    [Fact]
    public void KeepsALeadingDashInTheKey() =>
        VideoRef.Create("youtube", "-abcdefghij").Key.ShouldBe("youtube--abcdefghij");
}

public class WhenTheIdIsTheWrongLength
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("dQw4w9WgXc")]
    [InlineData("dQw4w9WgXcQQ")]
    [InlineData("dQw4w9WgXcQdQw4w9WgXcQ")]
    public void IsRejected(string id) => VideoRef.TryCreate("youtube", id, out _).ShouldBeFalse();

    [Fact]
    public void GivesNoRef()
    {
        _ = VideoRef.TryCreate("youtube", "dQw4w9WgXc", out var video);

        video.ShouldBeNull();
    }
}

public class WhenTheIdHasCharactersOutsideTheAlphabet
{
    [Theory]
    [InlineData(" Qw4w9WgXcQ")] // leading space
    [InlineData("dQw4w9WgXc ")] // trailing space
    [InlineData("dQw4w 9WgXc")] // inner space
    [InlineData("dQw4w9WgXc\t")] // tab
    [InlineData("dQw4w9WgXc\n")] // newline
    [InlineData("dQw4w9WgXc\0")] // NUL
    [InlineData("dQw4w9WgXc\u00A0")] // no-break space
    [InlineData("dQw4w9WgXc%")] // percent-encoding
    [InlineData("dQw4w9WgXc/")] // path
    [InlineData("dQw4w9WgXc\\")] // Windows path
    [InlineData("dQw4w9WgXc?")] // query
    [InlineData("dQw4w9WgXc&")] // query separator
    [InlineData("dQw4w9WgXc=")] // query assignment
    [InlineData("dQw4w9WgXc#")] // fragment
    [InlineData("dQw4w9WgXc.")] // dot
    [InlineData("dQw4w9WgXc:")] // scheme separator
    [InlineData("dQw4w9WgXc+")] // plus
    [InlineData("dQw4w9WgXc'")] // quote
    [InlineData("dQw4w9WgXc\"")] // double quote
    [InlineData("dQw4w9WgXc;")] // shell separator
    [InlineData("dQw4w9WgXc|")] // pipe
    [InlineData("dQw4w9WgXc$")] // shell expansion
    [InlineData("dQw4w9WgXc`")] // backtick
    [InlineData("dQw4w9WgXc*")] // glob
    public void RejectsAsciiOutsideTheAlphabet(string id) =>
        VideoRef.TryCreate("youtube", id, out _).ShouldBeFalse();

    [Theory]
    [InlineData("dQw4w9WgXc\u0430")] // Cyrillic small a
    [InlineData("dQw4w9WgXc\u041E")] // Cyrillic capital O
    [InlineData("dQw4w9WgXc\u03BF")] // Greek omicron
    [InlineData("dQw4w9WgXc\uFF41")] // fullwidth a
    [InlineData("dQw4w9WgXc\uFF10")] // fullwidth zero
    [InlineData("dQw4w9WgXc\u0661")] // Arabic-Indic one
    [InlineData("dQw4w9WgXc\u2010")] // hyphen
    [InlineData("dQw4w9WgXc\u2013")] // en dash
    [InlineData("dQw4w9WgXc\u2212")] // minus sign
    [InlineData("dQw4w9WgXc\uFF3F")] // fullwidth low line
    [InlineData("dQw4w9WgXc\u00E9")] // e acute
    [InlineData("dQw4w9WgXc\u200B")] // zero-width space
    [InlineData("dQw4w9WgX\uD83D\uDE00")] // emoji: 11 UTF-16 units, 10 code points
    public void RejectsUnicodeLookalikes(string id) =>
        VideoRef.TryCreate("youtube", id, out _).ShouldBeFalse();
}

public class WhenTheIdLooksLikeAnOption
{
    [Theory]
    [InlineData("-o")]
    [InlineData("--exec")]
    [InlineData("--exec calc")]
    [InlineData("--exec=calc")]
    [InlineData("-o /tmp/x")]
    [InlineData("--config-locations")]
    [InlineData("--batch-file=-")]
    public void IsRejected(string id) => VideoRef.TryCreate("youtube", id, out _).ShouldBeFalse();

    [Fact]
    public void IsRejectedByTheThrowingFactory() =>
        Should.Throw<ArgumentException>(() => VideoRef.Create("youtube", "--exec"));
}

public class WhenTheIdIsAUrl
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]
    [InlineData("www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("watch?v=dQw4w9WgXcQ")]
    [InlineData("v=dQw4w9WgXcQ")]
    [InlineData("file:///etc")]
    [InlineData("http://a.b")] // 10 characters
    [InlineData("http://a.bc")] // exactly 11 characters
    [InlineData("ftp://x.com")] // exactly 11 characters
    public void IsRejected(string id) => VideoRef.TryCreate("youtube", id, out _).ShouldBeFalse();
}

public class WhenTheIdIsNull
{
    [Fact]
    public void IsRejected() => VideoRef.TryCreate("youtube", null, out _).ShouldBeFalse();

    [Fact]
    public void IsRejectedByTheThrowingFactory() =>
        Should.Throw<ArgumentException>(() => VideoRef.Create("youtube", null!));
}

public class WhenTheIdDiffersOnlyInCase
{
    private readonly VideoRef _lower;
    private readonly VideoRef _mixed;

    public WhenTheIdDiffersOnlyInCase()
    {
        _lower = VideoRef.Create("youtube", "dqw4w9wgxcq");
        _mixed = VideoRef.Create("youtube", "dQw4w9WgXcQ");
    }

    [Fact]
    public void IsADifferentVideo() => _lower.ShouldNotBe(_mixed);

    [Fact]
    public void KeepsTheCaseInTheId() => _lower.Id.ShouldBe("dqw4w9wgxcq");

    [Fact]
    public void HasADifferentKey() => _lower.Key.ShouldNotBe(_mixed.Key);
}

public class WhenTheProviderIsNotSupported
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("vimeo")]
    [InlineData("twitch")]
    [InlineData("file")]
    [InlineData("youtu.be")]
    [InlineData("youtube.com")]
    [InlineData("you tube")]
    [InlineData("youtube-")]
    [InlineData("youtub")]
    [InlineData("youtubes")]
    [InlineData(" youtube")]
    [InlineData("youtube ")]
    [InlineData("youtube\n")]
    [InlineData("yout\u0443be")] // Cyrillic u
    [InlineData("YOUTUBE\u0130")] // dotted capital I appended
    [InlineData("\uFF59outube")] // fullwidth y
    public void IsRejected(string provider) =>
        VideoRef.TryCreate(provider, "dQw4w9WgXcQ", out _).ShouldBeFalse();

    [Fact]
    public void NullIsRejected() => VideoRef.TryCreate(null, "dQw4w9WgXcQ", out _).ShouldBeFalse();

    [Fact]
    public void GivesNoRef()
    {
        _ = VideoRef.TryCreate("vimeo", "dQw4w9WgXcQ", out var video);

        video.ShouldBeNull();
    }

    [Fact]
    public void IsRejectedByTheThrowingFactory() =>
        Should.Throw<ArgumentException>(() => VideoRef.Create("vimeo", "dQw4w9WgXcQ"));

    [Fact]
    public void NullIsRejectedByTheThrowingFactory() =>
        Should.Throw<ArgumentException>(() => VideoRef.Create(null!, "dQw4w9WgXcQ"));
}

public class WhenTheThrowingFactoryRejectsAnId
{
    private readonly ArgumentException _error;

    public WhenTheThrowingFactoryRejectsAnId()
    {
        _error = Should.Throw<ArgumentException>(() => VideoRef.Create("youtube", "bad"));
    }

    [Fact]
    public void NamesTheIdParameter() => _error.ParamName.ShouldBe("id");

    [Fact]
    public void SaysWhatAnIdMustLookLike() => _error.Message.ShouldContain("11");
}

public class WhenTheThrowingFactoryRejectsAProvider
{
    private readonly ArgumentException _error;

    public WhenTheThrowingFactoryRejectsAProvider()
    {
        _error = Should.Throw<ArgumentException>(() => VideoRef.Create("vimeo", "dQw4w9WgXcQ"));
    }

    [Fact]
    public void NamesTheProviderParameter() => _error.ParamName.ShouldBe("provider");

    [Fact]
    public void NamesTheSupportedProvider() => _error.Message.ShouldContain("youtube");
}

public class WhenParsingACanonicalKey
{
    private readonly VideoRef? _video;

    public WhenParsingACanonicalKey()
    {
        VideoRef.TryParseKey("youtube-dQw4w9WgXcQ", out _video).ShouldBeTrue();
        _video.ShouldNotBeNull();
    }

    [Fact]
    public void RecoversTheProvider() => _video!.Provider.ShouldBe("youtube");

    [Fact]
    public void RecoversTheId() => _video!.Id.ShouldBe("dQw4w9WgXcQ");

    [Fact]
    public void EqualsTheRefItCameFrom() =>
        _video.ShouldBe(VideoRef.Create("youtube", "dQw4w9WgXcQ"));

    [Fact]
    public void MatchesTheThrowingParse() =>
        VideoRef.ParseKey("youtube-dQw4w9WgXcQ").ShouldBe(_video);
}

public class WhenRoundTrippingAKey
{
    [Theory]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("-abcdefghij")]
    [InlineData("--abcdefghi")]
    [InlineData("_abcdefghij")]
    [InlineData("abcdefghij-")]
    [InlineData("-----------")]
    public void GivesBackTheSameRef(string id)
    {
        var video = VideoRef.Create("youtube", id);

        VideoRef.ParseKey(video.Key).ShouldBe(video);
    }

    [Theory]
    [InlineData("youtube-dQw4w9WgXcQ")]
    [InlineData("youtube--abcdefghij")]
    [InlineData("youtube-___________")]
    public void GivesBackTheSameKey(string key) => VideoRef.ParseKey(key).Key.ShouldBe(key);

    [Fact]
    public void SplitsALeadingDashIdAtTheFirstDash() =>
        VideoRef.ParseKey("youtube--abcdefghij").Id.ShouldBe("-abcdefghij");
}

public class WhenParsingAMalformedKey
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("youtube")]
    [InlineData("youtube-")]
    [InlineData("-dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ")]
    [InlineData("youtubedQw4w9WgXcQ")]
    [InlineData("youtube_dQw4w9WgXcQ")]
    [InlineData("youtube:dQw4w9WgXcQ")]
    [InlineData("youtube/dQw4w9WgXcQ")]
    [InlineData("youtube-dQw4w9WgXc")]
    [InlineData("youtube-dQw4w9WgXcQQ")]
    [InlineData("youtube-dQw4w9WgXc ")]
    [InlineData(" youtube-dQw4w9WgXcQ")]
    [InlineData("youtube-dQw4w9WgXcQ ")]
    [InlineData("youtube-dQw4w9WgXcQ\n")]
    [InlineData("youtube-dQw4w9WgXc%")]
    [InlineData("youtube-dQw4w9WgX\u0430Q")]
    [InlineData("YouTube-dQw4w9WgXcQ")] // not canonical: keys are lower case
    [InlineData("YOUTUBE-dQw4w9WgXcQ")]
    [InlineData("vimeo-dQw4w9WgXcQ")]
    [InlineData("vimeo-12345678901")]
    [InlineData("youtube-https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("youtube--exec")]
    [InlineData("youtube-youtube-dQw4w9WgXcQ")]
    public void IsRejected(string key) => VideoRef.TryParseKey(key, out _).ShouldBeFalse();

    [Fact]
    public void NullIsRejected() => VideoRef.TryParseKey(null, out _).ShouldBeFalse();

    [Fact]
    public void GivesNoRef()
    {
        _ = VideoRef.TryParseKey("youtube-bad", out var video);

        video.ShouldBeNull();
    }

    [Fact]
    public void IsRejectedByTheThrowingParse() =>
        Should.Throw<ArgumentException>(() => VideoRef.ParseKey("youtube-bad"));

    [Fact]
    public void NullIsRejectedByTheThrowingParse() =>
        Should.Throw<ArgumentException>(() => VideoRef.ParseKey(null!));
}

public class WhenComparingVideoRefs
{
    private readonly VideoRef _first;
    private readonly VideoRef _same;
    private readonly VideoRef _other;

    public WhenComparingVideoRefs()
    {
        _first = VideoRef.Create("youtube", "dQw4w9WgXcQ");
        _same = VideoRef.Create("YouTube", "dQw4w9WgXcQ");
        _other = VideoRef.Create("youtube", "jNQXAC9IVRw");

        _first.ShouldNotBeSameAs(_same);
    }

    [Fact]
    public void EqualsARefWithTheSameProviderAndId() => _first.Equals(_same).ShouldBeTrue();

    [Fact]
    public void EqualsWithTheOperator() => (_first == _same).ShouldBeTrue();

    [Fact]
    public void SharesAHashCode() => _first.GetHashCode().ShouldBe(_same.GetHashCode());

    [Fact]
    public void DiffersFromAnotherId() => _first.ShouldNotBe(_other);

    [Fact]
    public void DiffersWithTheOperator() => (_first != _other).ShouldBeTrue();

    [Fact]
    public void DoesNotEqualNull() => _first.Equals(null).ShouldBeFalse();

    [Fact]
    public void FindsItsTwinInAHashSet() =>
        new HashSet<VideoRef> { _first }
            .Contains(_same)
            .ShouldBeTrue();

    [Fact]
    public void CollapsesTwinsAsDictionaryKeys()
    {
        var sessions = new Dictionary<VideoRef, int> { [_first] = 1 };
        sessions[_same] = 2;

        sessions.Count.ShouldBe(1);
    }
}

public class WhenReadingTheSupportedProvider
{
    [Fact]
    public void IsYouTube() => VideoRef.YouTubeProvider.ShouldBe("youtube");

    [Fact]
    public void HasElevenCharacterIds() => VideoRef.YouTubeIdLength.ShouldBe(11);
}
