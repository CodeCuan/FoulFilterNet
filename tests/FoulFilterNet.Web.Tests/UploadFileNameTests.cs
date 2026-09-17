using FoulFilterNet.Web.Uploads;

namespace FoulFilterNet.Web.Tests;

/// <summary>
/// The characters stripped here are not a matter of taste: a colon or a bracket
/// in a filename breaks FFmpeg's filtergraph parser, which is why the Python
/// sanitized at the upload edge rather than at the FFmpeg call.
/// </summary>
public class WhenSanitizingAnAwkwardUploadName
{
    private readonly string _sanitized;

    public WhenSanitizingAnAwkwardUploadName()
    {
        _sanitized = UploadFileName.Sanitize("book: \"chapter [1]\" .mp3");

        _sanitized.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RewritesTheWholeNameTheWayThePythonDid() =>
        _sanitized.ShouldBe("book - chapter 1.mp3");

    [Fact]
    public void TurnsAColonIntoASpacedDash() =>
        UploadFileName.Sanitize("part 1: the end.mp3").ShouldBe("part 1 - the end.mp3");

    [Fact]
    public void StripsBracketsAndQuotes() =>
        UploadFileName.Sanitize("a[b]c\"d'e.mp3").ShouldBe("abcde.mp3");

    [Fact]
    public void TrimsSurroundingWhitespace() =>
        UploadFileName.Sanitize("  spaced  .mp3").ShouldBe("spaced.mp3");

    [Fact]
    public void TrimsSurroundingDots() =>
        UploadFileName.Sanitize("...odd....mp3").ShouldBe("odd.mp3");

    [Fact]
    public void LowercasesTheExtension() =>
        UploadFileName.Sanitize("Book.MP3").ShouldBe("Book.mp3");

    [Fact]
    public void LeavesTheRestOfTheCaseAlone() =>
        UploadFileName.Sanitize("Book.mp3").ShouldBe("Book.mp3");
}

public class WhenSanitizingAnUploadNameThatIsAPath
{
    private readonly string _sanitized;

    public WhenSanitizingAnUploadNameThatIsAPath()
    {
        _sanitized = UploadFileName.Sanitize("../../etc/passwd");

        _sanitized.ShouldNotContain("/");
        _sanitized.ShouldNotContain("..");
    }

    [Fact]
    public void KeepsOnlyTheBaseName() => _sanitized.ShouldBe("passwd");

    [Fact]
    public void AlsoUnwindsWindowsSeparators() =>
        UploadFileName.Sanitize(@"C:\Users\me\book.mp3").ShouldBe("book.mp3");

    [Fact]
    public void RefusesToEscapeTheUploadDirectory() =>
        Path.GetFileName(UploadFileName.Sanitize("../../etc/passwd")).ShouldBe("passwd");
}

public class WhenSanitizingAnUploadNameWithNothingUsableInIt
{
    private readonly string _empty;

    public WhenSanitizingAnUploadNameWithNothingUsableInIt()
    {
        _empty = UploadFileName.Sanitize(string.Empty);

        _empty.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void FallsBackToAPlaceholder() => _empty.ShouldBe("file");

    [Fact]
    public void TreatsADoubleDotAsNothingUsable() => UploadFileName.Sanitize("..").ShouldBe("file");

    [Fact]
    public void TreatsANullTheSameAsEmpty() => UploadFileName.Sanitize(null).ShouldBe("file");

    [Fact]
    public void KeepsTheExtensionWhenOnlyTheStemIsUnusable() =>
        UploadFileName.Sanitize("'[]'.mp3").ShouldBe("file.mp3");
}

public class WhenCheckingAnUploadsExtension
{
    private readonly bool _allowed;

    public WhenCheckingAnUploadsExtension()
    {
        _allowed = UploadFileName.IsAllowedExtension("book.mp3");

        UploadFileName.AllowedExtensions.ShouldNotBeEmpty();
    }

    [Fact]
    public void AcceptsAnAudioFile() => _allowed.ShouldBeTrue();

    [Fact]
    public void AcceptsAnAudiobook() =>
        UploadFileName.IsAllowedExtension("book.m4b").ShouldBeTrue();

    [Fact]
    public void AcceptsAVideoFile() => UploadFileName.IsAllowedExtension("clip.mkv").ShouldBeTrue();

    [Fact]
    public void RejectsSomethingThatIsNotMedia() =>
        UploadFileName.IsAllowedExtension("notes.txt").ShouldBeFalse();

    [Fact]
    public void RejectsAFileWithNoExtensionAtAll() =>
        UploadFileName.IsAllowedExtension("book").ShouldBeFalse();

    [Fact]
    public void IgnoresTheCaseItWasTypedIn() =>
        UploadFileName.IsAllowedExtension("BOOK.MP3").ShouldBeTrue();

    [Fact]
    public void TreatsALeadingDotAsPartOfTheNameRatherThanAnExtension() =>
        UploadFileName.IsAllowedExtension(".mp3").ShouldBeFalse();
}
