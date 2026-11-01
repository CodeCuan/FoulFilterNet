using FoulFilterNet.Domain;

namespace FoulFilterNet.Books.Tests;

public class TextMatcherTests
{
    private readonly TextMatcher _matcher = new(
        BadWordsList.FromLines(["damn", "ass", "go to hell", "fuckin'"]),
        ["fuck"]
    );

    private List<string> Masked(string text) =>
        [.. _matcher.Find(text).Spans.Select(s => text.Substring(s.Start, s.Length))];

    [Fact]
    public void AnExactWordMatchesInAnyCase() => Masked("Well DAMN it").ShouldBe(["DAMN"]);

    [Fact]
    public void AListedWordInsideAnotherWordDoesNotMatch() =>
        Masked("a classic class").ShouldBeEmpty();

    [Fact]
    public void APhraseMatchesAsOneSpan() => Masked("just go to hell now").ShouldBe(["go to hell"]);

    [Fact]
    public void APartialWordMatchesInsideAnyWord() =>
        Masked("those MOTHERFUCKERS").ShouldBe(["MOTHERFUCKERS"]);

    [Fact]
    public void ACurlyQuotedWordMatchesWithoutItsQuotes() =>
        Masked("he said ‘damn’ twice").ShouldBe(["damn"]);

    [Fact]
    public void ACurlyPossessiveMasksOnlyTheWord() => Masked("the ass’s tail").ShouldBe(["ass"]);

    [Fact]
    public void AListedTrailingApostropheMatchesACurlyOne() =>
        Masked("fuckin’ hell").ShouldBe(["fuckin"]);

    [Fact]
    public void ANearMissContainsAListedWordButIsNotMasked() =>
        _matcher.Find("damned class").NearMisses.ShouldBe([("damned", "damn"), ("class", "ass")]);

    [Fact]
    public void AMaskedWordIsNotANearMiss() =>
        _matcher.Find("damn fucker").NearMisses.ShouldBeEmpty();

    [Fact]
    public void OverlappingPhrasesMergeIntoOneSpan()
    {
        var matcher = new TextMatcher(BadWordsList.FromLines(["a b", "b c"]), []);

        matcher.Find("a b c").Spans.ShouldBe([(0, 5)]);
    }
}
