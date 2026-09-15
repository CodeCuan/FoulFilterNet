using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

/// <summary>
/// Ports <c>test_comments_and_blank_lines_are_skipped</c> from
/// <c>Legacy/src/test_pipeline_logic.py</c>.
/// </summary>
public class WhenNormalizingABadWordsList
{
    private readonly BadWordsList _list;

    public WhenNormalizingABadWordsList()
    {
        _list = BadWordsList.FromLines(["damn", "", "# comment", "hell"]);

        _list.ShouldNotBeNull();
        _list.Phrases.ShouldAllBe(p => p.Trim() == p);
    }

    [Fact]
    public void KeepsOnlyTheRealEntries() => _list.Count.ShouldBe(2);

    [Fact]
    public void HoldsEveryEntryAsNormalizedTokens() => _list.Phrases.ShouldBe(["damn", "hell"], ignoreOrder: true);

    [Fact]
    public void SkipsBlankLines() => _list.Contains("").ShouldBeFalse();

    [Fact]
    public void SkipsCommentLines() => _list.Contains("comment").ShouldBeFalse();
}

public class WhenABadWordsListEntryNeedsNormalizing
{
    private readonly BadWordsList _list;

    public WhenABadWordsListEntryNeedsNormalizing()
    {
        _list = BadWordsList.FromLines(["  Go To Hell  ", "Don't", "MOTHER-LOVER"]);

        _list.ShouldNotBeNull();
        _list.Count.ShouldBe(3);
    }

    [Fact]
    public void TrimsSurroundingWhitespace() => _list.Contains("go to hell").ShouldBeTrue();

    [Fact]
    public void LowercasesTheEntry() => _list.Contains("Go To Hell").ShouldBeFalse();

    [Fact]
    public void KeepsApostrophes() => _list.Contains("don't").ShouldBeTrue();

    [Fact]
    public void SplitsAnEntryOnPunctuation() => _list.Contains("mother lover").ShouldBeTrue();
}

public class WhenABadWordsListEntryIsTooLong
{
    private readonly BadWordsList _list;

    public WhenABadWordsListEntryIsTooLong()
    {
        _list = BadWordsList.FromLines(["hell", "one two three four"]);

        _list.ShouldNotBeNull();
        _list.Count.ShouldBe(1);
    }

    [Fact]
    public void DropsTheOverLongEntrySilently() => _list.Contains("one two three four").ShouldBeFalse();

    [Fact]
    public void KeepsAThreeTokenEntry() =>
        BadWordsList.FromLines(["one two three"]).Contains("one two three").ShouldBeTrue();

    [Fact]
    public void StillKeepsTheEntriesThatFit() => _list.Contains("hell").ShouldBeTrue();

    [Fact]
    public void CapsPhrasesAtThreeWords() => BadWordsList.MaxPhraseWords.ShouldBe(3);
}

public class WhenABadWordsListHasNoUsableLines
{
    private readonly Func<BadWordsList> _fromBlanks;

    public WhenABadWordsListHasNoUsableLines()
    {
        _fromBlanks = () => BadWordsList.FromLines(["", "   ", "# nothing here"]);

        _fromBlanks.ShouldNotBeNull();
    }

    [Fact]
    public void Throws() => Should.Throw<ArgumentException>(_fromBlanks);

    [Fact]
    public void ThrowsForAnEmptyFile() => Should.Throw<ArgumentException>(() => BadWordsList.FromLines([]));

    [Fact]
    public void SaysWhichArgumentWasWrong() =>
        Should.Throw<ArgumentException>(_fromBlanks).ParamName.ShouldBe("lines");
}
