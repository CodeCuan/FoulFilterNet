using FoulFilterNet.Domain;
using Whisper.net;

namespace FoulFilterNet.Transcription.Tests;

/// <summary>
/// whisper.cpp emits <em>sub-word</em> tokens, so a spoken word arrives as
/// several tokens whose first one carries the leading space. Turning each token
/// into a <see cref="Word"/> would look plausible and be systematically wrong -
/// every boundary would end where the word's first syllable ended.
/// </summary>
public sealed class WhenJoiningTheTokensOfOneSpokenWord
{
    private readonly IReadOnlyList<Word> _words;

    public WhenJoiningTheTokensOfOneSpokenWord()
    {
        _words = WhisperWords.Join([Tokens.At(" da", 341, 370), Tokens.At("mn", 370, 389)]);

        _words.ShouldNotBeNull();
        _words.ShouldAllBe(w => w.End > w.Start);
    }

    [Fact]
    public void EmitsOneWordRatherThanOnePerToken() => _words.Count.ShouldBe(1);

    [Fact]
    public void SpellsTheWholeWord() => _words[0].Text.ShouldBe("damn");

    [Fact]
    public void StartsWhereTheFirstTokenStarted() => _words[0].Start.ShouldBe(3.41, 0.001);

    [Fact]
    public void EndsWhereTheLastTokenEnded() => _words[0].End.ShouldBe(3.89, 0.001);
}

/// <summary>
/// The whitespace-delimited word is the unit, whether a word took one token or
/// four - this is what the Bad Words List is matched against.
/// </summary>
public sealed class WhenASegmentHoldsSeveralSpokenWords
{
    private readonly IReadOnlyList<Word> _words;

    public WhenASegmentHoldsSeveralSpokenWords()
    {
        _words = WhisperWords.Join([
            Tokens.At(" go", 350, 360),
            Tokens.At(" to", 360, 372),
            Tokens.At(" he", 372, 385),
            Tokens.At("ll", 385, 422),
        ]);

        _words.ShouldNotBeNull();
        _words.Select(w => w.Start).ShouldBeInOrder();
    }

    [Fact]
    public void EmitsOneWordPerWhitespaceDelimitedWord() => _words.Count.ShouldBe(3);

    [Fact]
    public void ReadsBackAsTheSpokenPhrase() =>
        string.Join(' ', _words.Select(w => w.Text)).ShouldBe("go to hell");

    [Fact]
    public void SpansTheMultiTokenWordFromItsFirstTokenToItsLast()
    {
        _words[2].Start.ShouldBe(3.72, 0.001);
        _words[2].End.ShouldBe(4.22, 0.001);
    }

    [Fact]
    public void LeavesTheSingleTokenWordsWhereTheyWere() => _words[0].Start.ShouldBe(3.50, 0.001);
}

/// <summary>
/// A segment's token list also carries timestamp and control tokens
/// (<c>[_BEG_]</c>, <c>&lt;|0.00|&gt;</c>). They are not speech, and a word
/// that absorbed one would carry its time.
/// </summary>
public sealed class WhenTheSegmentCarriesSpecialTokens
{
    private readonly IReadOnlyList<Word> _words;

    public WhenTheSegmentCarriesSpecialTokens()
    {
        _words = WhisperWords.Join([
            Tokens.At("[_BEG_]", 0, 0),
            Tokens.At("<|0.00|>", 0, 0),
            Tokens.At(" damn", 341, 389),
            Tokens.At("<|3.90|>", 390, 390),
            Tokens.At(string.Empty, 400, 410),
        ]);

        _words.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsOnlyTheSpokenWord() => _words.Count.ShouldBe(1);

    [Fact]
    public void NamesItWithoutAnyMarkup() => _words[0].Text.ShouldBe("damn");

    [Fact]
    public void DoesNotStretchItOntoTheTrailingTimestamp() => _words[0].End.ShouldBe(3.89, 0.001);

    [Fact]
    public void DoesNotStartItAtTheLeadingMarker() => _words[0].Start.ShouldBe(3.41, 0.001);
}

/// <summary>
/// Punctuation arrives as its own token with no leading space, so it belongs to
/// the word in front of it. <c>PhraseMatcher.FindHits</c> normalizes before
/// matching, so "damn," still matches - the timestamps are what matter here.
/// </summary>
public sealed class WhenATokenIsTrailingPunctuation
{
    private readonly IReadOnlyList<Word> _words;

    public WhenATokenIsTrailingPunctuation()
    {
        _words = WhisperWords.Join([Tokens.At(" damn", 100, 150), Tokens.At(",", 150, 152)]);

        _words.ShouldNotBeNull();
    }

    [Fact]
    public void DoesNotBecomeAWordOfItsOwn() => _words.Count.ShouldBe(1);

    [Fact]
    public void StaysAttachedToTheWordItFollows() => _words[0].Text.ShouldBe("damn,");

    [Fact]
    public void CarriesTheWordsEndWithIt() => _words[0].End.ShouldBe(1.52, 0.001);
}

/// <summary>
/// A single token occasionally spells more than one word. The rule is the
/// whitespace, not the token, so it becomes two words sharing the token's span
/// rather than one word with a space in it - a phrase in
/// <see cref="Word.Text"/> would never match the list.
/// </summary>
public sealed class WhenATokenSpellsTwoWordsAtOnce
{
    private readonly IReadOnlyList<Word> _words;

    public WhenATokenSpellsTwoWordsAtOnce()
    {
        _words = WhisperWords.Join([Tokens.At(" all right", 200, 260)]);

        _words.ShouldNotBeNull();
    }

    [Fact]
    public void SplitsOnTheWhitespaceInsideIt() => _words.Count.ShouldBe(2);

    [Fact]
    public void NamesBothWords() => _words.Select(w => w.Text).ShouldBe(["all", "right"]);

    [Fact]
    public void GivesEachOfThemTheTokensSpan() =>
        _words.ShouldAllBe(w => w.Start == 2.0 && w.End == 2.6);
}

/// <summary>
/// whisper.cpp regularly reports a word with no length at all -
/// <c>' The' t0=398 t1=398</c> - and nine of the fixtures' words come back that
/// way (ADR-0006). <c>Legacy/src/aligner.py</c> dropped such a word, because
/// for a forced aligner it meant alignment had failed; here it means only that
/// the heuristic was vague about a word that was certainly spoken, and a dropped
/// word cannot be matched against the Bad Words List at all. Detection is worth
/// more than a boundary, so the word survives with a minimal span and the hit
/// padding covers the rest.
/// </summary>
public sealed class WhenATokensTimestampsCollapse
{
    private readonly IReadOnlyList<Word> _words = WhisperWords.Join([Tokens.At(" damn", 300, 300)]);

    public WhenATokensTimestampsCollapse() => _words.ShouldNotBeNull();

    [Fact]
    public void KeepsTheWordRatherThanLosingTheHit() =>
        _words.ShouldHaveSingleItem().Text.ShouldBe("damn");

    [Fact]
    public void PlacesItWhereTheTokenSaidItWas() => _words[0].Start.ShouldBe(3.0, 0.001);

    [Fact]
    public void GivesItEnoughLengthToCensor() => _words[0].End.ShouldBeGreaterThan(_words[0].Start);

    [Fact]
    public void KeepsEvenAWordWhoseEndPrecedesItsStart() =>
        WhisperWords
            .Join([Tokens.At(" damn", 300, 280)])
            .ShouldHaveSingleItem()
            .Text.ShouldBe("damn");

    [Fact]
    public void HearsNoWordsInNoTokens() => WhisperWords.Join([]).ShouldBeEmpty();

    [Fact]
    public void RefusesAMissingTokenList() =>
        Should.Throw<ArgumentNullException>(() => WhisperWords.Join(null!));
}

/// <summary>
/// With DTW alignment heads configured, each token also carries a single DTW
/// instant, and measuring it against the fixtures' exact spans (ADR-0006) showed
/// two things: the instant is far more reliable than <c>t0</c>/<c>t1</c>, and it
/// marks where a token <em>ended</em>, not where it began. A word therefore runs
/// from the instant of the token before it to the instant of its own last token.
/// Reading the instants as starts instead puts every word about 0.3 s late.
/// </summary>
/// <remarks>
/// The tokens below are what the model actually returned for
/// <c>single_hit.mp3</c>: "Well, damn, that ...".
/// </remarks>
public sealed class WhenTheModelSuppliesDtwTimestamps
{
    private readonly IReadOnlyList<Word> _words = WhisperWords.Join([
        Tokens.Dtw(" Well", 303, 312, 324),
        Tokens.Dtw(",", 312, 337, 348),
        Tokens.Dtw(" damn", 337, 389, 366),
        Tokens.Dtw(",", 395, 402, 394),
        Tokens.Dtw(" that", 402, 421, 406),
    ]);

    public WhenTheModelSuppliesDtwTimestamps() => _words.Count.ShouldBe(3);

    [Fact]
    public void StartsAWordWhereTheTokenBeforeItEnded() => _words[1].Start.ShouldBe(3.48, 0.001);

    [Fact]
    public void EndsAWordAtItsOwnLastTokensInstant() => _words[1].End.ShouldBe(3.94, 0.001);

    [Fact]
    public void LeavesNoGapBetweenOneWordAndTheNext() => _words[2].Start.ShouldBe(_words[1].End);

    [Fact]
    public void FallsBackToTheRawStartForTheVeryFirstWord() =>
        _words[0].Start.ShouldBe(3.03, 0.001);

    [Fact]
    public void StillSpellsTheWordsTheSameWay() =>
        _words.Select(w => w.Text).ShouldBe(["well,", "damn,", "that"]);
}

/// <summary>
/// A model with no alignment-heads preset gets no DTW instants at all, and
/// whisper.cpp reports that as -1. Those words fall back to the token
/// timestamps rather than landing at negative times.
/// </summary>
public sealed class WhenOnlySomeTokensCarryADtwInstant
{
    private readonly IReadOnlyList<Word> _words = WhisperWords.Join([
        Tokens.Dtw(" well", 303, 312, 324),
        Tokens.At(" damn", 337, 389),
    ]);

    public WhenOnlySomeTokensCarryADtwInstant() => _words.Count.ShouldBe(2);

    [Fact]
    public void UsesTheInstantWhereThereIsOne() => _words[1].Start.ShouldBe(3.24, 0.001);

    [Fact]
    public void UsesTheTokenTimestampWhereThereIsNot() => _words[1].End.ShouldBe(3.89, 0.001);
}

/// <summary>
/// <c>aligner.py</c> emitted <c>w["word"].lower().strip()</c> and the Bad Words
/// List is lower case, so the engine that replaces it emits the same spelling.
/// </summary>
public sealed class WhenTheModelCapitalisesAWord
{
    private readonly IReadOnlyList<Word> _words;

    public WhenTheModelCapitalisesAWord()
    {
        _words = WhisperWords.Join([Tokens.At(" Damn", 341, 389)]);

        _words.Count.ShouldBe(1);
    }

    [Fact]
    public void LowercasesIt() => _words[0].Text.ShouldBe("damn");
}

/// <summary>
/// Segment times are <see cref="TimeSpan"/> in Whisper.net, but token times are
/// the raw whisper.cpp <c>t0</c>/<c>t1</c> - centiseconds. Reading them as
/// milliseconds would place every word a hundred times too early.
/// </summary>
public sealed class WhenReadingTokenTimestampsAsSeconds
{
    [Fact]
    public void TreatsThemAsCentiseconds() =>
        WhisperWords.TokenUnitSeconds.ShouldBe(0.01, 0.0000001);

    [Fact]
    public void PlacesAWordAtTheSecondItWasSpoken() =>
        WhisperWords.Join([Tokens.At(" damn", 1234, 1290)])[0].Start.ShouldBe(12.34, 0.001);
}

/// <summary>Builds the tokens whisper.cpp hands back, without a model.</summary>
internal static class Tokens
{
    /// <summary>
    /// A token spanning <paramref name="start"/>..<paramref name="end"/> in
    /// centiseconds, with no DTW instant - which is what a model without an
    /// alignment-heads preset reports.
    /// </summary>
    public static WhisperToken At(string text, long start, long end) =>
        new()
        {
            Text = text,
            Start = start,
            End = end,
            DtwTimestamp = -1,
        };

    /// <summary>The same, with the DTW instant a model with alignment heads adds.</summary>
    public static WhisperToken Dtw(string text, long start, long end, long dtw) =>
        new()
        {
            Text = text,
            Start = start,
            End = end,
            DtwTimestamp = dtw,
        };
}
