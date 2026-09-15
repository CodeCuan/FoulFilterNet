using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

/// <summary>
/// Ports <c>test_apostrophes_survive_tokenization</c> and the tokenizer half of
/// <c>test_no_false_positive_substrings</c> from
/// <c>Legacy/src/test_pipeline_logic.py</c>.
/// </summary>
public class WhenTokenizingText
{
    private readonly IReadOnlyList<string> _tokens;

    public WhenTokenizingText()
    {
        _tokens = Tokenizer.Tokenize("Well, Don't! You 42 fools?");

        _tokens.ShouldNotBeEmpty();
        _tokens.ShouldAllBe(t => t.Length > 0);
    }

    [Fact]
    public void SplitsOnEveryNonTokenCharacter() => _tokens.Count.ShouldBe(5);

    [Fact]
    public void LowercasesEveryToken() => _tokens.ShouldBe(["well", "don't", "you", "42", "fools"]);

    [Fact]
    public void KeepsApostrophesInsideAToken() => _tokens.ShouldContain("don't");

    [Fact]
    public void KeepsDigits() => _tokens.ShouldContain("42");

    [Fact]
    public void ReturnsNothingForTextWithoutWordCharacters() => Tokenizer.Tokenize("-- !! --").ShouldBeEmpty();

    [Fact]
    public void ReturnsNothingForEmptyText() => Tokenizer.Tokenize("").ShouldBeEmpty();

    [Fact]
    public void TreatsHyphensAsSeparators() => Tokenizer.Tokenize("go-to").ShouldBe(["go", "to"]);
}

public class WhenNormalizingAWordForMatching
{
    private readonly string _normalized;

    public WhenNormalizingAWordForMatching()
    {
        _normalized = Tokenizer.Normalize("Hell,");

        _normalized.ShouldNotBeNull();
    }

    [Fact]
    public void StripsPunctuationAndCase() => _normalized.ShouldBe("hell");

    [Fact]
    public void LeavesAnAlreadyNormalizedWordAlone() => Tokenizer.Normalize("damn").ShouldBe("damn");

    [Fact]
    public void JoinsSeveralTokensWithASingleSpace() => Tokenizer.Normalize(" Go   To! ").ShouldBe("go to");

    [Fact]
    public void YieldsAnEmptyStringForPurePunctuation() => Tokenizer.Normalize("...").ShouldBe("");
}
