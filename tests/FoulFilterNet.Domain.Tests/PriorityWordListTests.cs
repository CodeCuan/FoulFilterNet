using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

public class WhenThePriorityWordListIsTheBuiltInDefault
{
    private readonly PriorityWordList _list = PriorityWordList.Default;

    public WhenThePriorityWordListIsTheBuiltInDefault()
    {
        _list.ShouldNotBeNull();
        _list.Words.ShouldAllBe(w => w.Trim() == w);
    }

    [Fact]
    public void HoldsTheFWordFamily() =>
        _list.Words.ShouldBe([
            "fuck",
            "fucks",
            "fucking",
            "fuckin",
            "fucked",
            "fucker",
            "motherfucker",
        ]);

    [Fact]
    public void IsNotEmpty() => _list.IsEmpty.ShouldBeFalse();

    [Fact]
    public void CountsItsWords() => _list.Count.ShouldBe(7);

    [Fact]
    public void PromptsWithTheWordsJoinedByCommas() =>
        _list.Prompt.ShouldBe("fuck, fucks, fucking, fuckin, fucked, fucker, motherfucker");

    [Fact]
    public void ContainsANormalizedToken() => _list.Contains("fucking").ShouldBeTrue();

    [Fact]
    public void DoesNotContainAnUnlistedToken() => _list.Contains("damn").ShouldBeFalse();

    [Fact]
    public void DoesNotContainATokenThatOnlyStartsWithAnEntry() =>
        _list.Contains("fuckery").ShouldBeFalse();

    [Fact]
    public void ContainsOnlyNormalizedTokens() => _list.Contains("Fucking,").ShouldBeFalse();

    [Fact]
    public void MatchesAWordAsWhisperRendersIt() => _list.Matches(" Fucking,").ShouldBeTrue();

    [Fact]
    public void MatchesAnUppercaseWord() => _list.Matches("MOTHERFUCKER!").ShouldBeTrue();

    [Fact]
    public void DoesNotMatchAnUnlistedWord() => _list.Matches("Damn.").ShouldBeFalse();

    [Fact]
    public void DoesNotMatchAWordThatSplitsIntoTwoTokens() =>
        _list.Matches("fucking-A").ShouldBeFalse();

    [Fact]
    public void DoesNotMatchBlankText() => _list.Matches("  ").ShouldBeFalse();

    [Fact]
    public void DoesNotMatchNull() => _list.Matches(null).ShouldBeFalse();
}

public class WhenAPriorityWordListIsReadFromLines
{
    private readonly PriorityWordList _list;

    public WhenAPriorityWordListIsReadFromLines()
    {
        _list = PriorityWordList.FromLines([
            "# the ones that matter",
            "",
            "  Shit  ",
            "Fucking,",
            "go to hell",
            "shit",
            "Don't",
        ]);

        _list.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsSingleWordEntriesNormalizedInFileOrder() =>
        _list.Words.ShouldBe(["shit", "fucking", "don't"]);

    [Fact]
    public void IgnoresMultiWordEntries() => _list.Contains("go to hell").ShouldBeFalse();

    [Fact]
    public void KeepsADuplicateOnce() => _list.Count.ShouldBe(3);

    [Fact]
    public void SkipsCommentLines() => _list.Contains("the").ShouldBeFalse();

    [Fact]
    public void KeepsApostrophes() => _list.Contains("don't").ShouldBeTrue();

    [Fact]
    public void PromptsWithTheNormalizedWords() => _list.Prompt.ShouldBe("shit, fucking, don't");

    [Fact]
    public void DoesNotFallBackToTheDefault() => _list.Contains("fuck").ShouldBeFalse();
}

/// <summary>
/// Unlike the Bad Words List, an empty Priority Word List is not a configuration
/// error: it is how a user turns the Priority Word Pass off from the file.
/// </summary>
public class WhenAPriorityWordListFileHasNoUsableLine
{
    private readonly PriorityWordList _list;

    public WhenAPriorityWordListFileHasNoUsableLine()
    {
        _list = PriorityWordList.FromLines(["", "# nothing to hunt for", "   "]);

        _list.ShouldNotBeNull();
    }

    [Fact]
    public void IsEmpty() => _list.IsEmpty.ShouldBeTrue();

    [Fact]
    public void CountsNothing() => _list.Count.ShouldBe(0);

    [Fact]
    public void HasABlankPrompt() => _list.Prompt.ShouldBe("");

    [Fact]
    public void MatchesNothing() => _list.Matches("fuck").ShouldBeFalse();

    [Fact]
    public void IsEmptyWhenOnlyMultiWordEntriesWereGiven() =>
        PriorityWordList.FromLines(["go to hell"]).IsEmpty.ShouldBeTrue();

    [Fact]
    public void IsEmptyForNoLinesAtAll() => PriorityWordList.FromLines([]).IsEmpty.ShouldBeTrue();

    [Fact]
    public void RejectsANullSequence() =>
        Should.Throw<ArgumentNullException>(() => PriorityWordList.FromLines(null!));
}
