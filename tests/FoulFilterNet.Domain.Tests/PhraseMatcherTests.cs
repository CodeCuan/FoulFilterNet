using FoulFilterNet.Domain;

namespace FoulFilterNet.Domain.Tests;

/// <summary>
/// Ports <c>test_single_word_match_ignores_case_and_punctuation</c> from
/// <c>Legacy/src/test_pipeline_logic.py</c>.
/// </summary>
public class WhenMatchingASingleWordInASegment
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenMatchingASingleWordInASegment()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [new Segment(0.0, 2.0, "Well, Damn. that's bad")],
            BadWordsList.FromLines(["damn"])
        );

        _candidates.ShouldNotBeNull();
        _candidates.Count.ShouldBe(1);
    }

    [Fact]
    public void IgnoresCaseAndPunctuation() => _candidates[0].Phrase.ShouldBe("damn");

    [Fact]
    public void StartsAfterTheSegmentStart() => _candidates[0].ApproxStart.ShouldBeGreaterThan(0.0);

    [Fact]
    public void EndsAfterItStarts() =>
        _candidates[0].ApproxEnd.ShouldBeGreaterThan(_candidates[0].ApproxStart);

    [Fact]
    public void EndsBeforeTheSegmentEnds() => _candidates[0].ApproxEnd.ShouldBeLessThan(2.0);

    [Fact]
    public void RecordsWhichSegmentItCameFrom() => _candidates[0].SegmentIndex.ShouldBe(0);
}

/// <summary>Ports <c>test_multiword_phrase_matches_across_tokens</c>.</summary>
public class WhenMatchingAMultiWordPhrase
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenMatchingAMultiWordPhrase()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [new Segment(10.0, 14.0, "he can go to hell for all I care")],
            BadWordsList.FromLines(["go to hell"])
        );

        _candidates.ShouldNotBeNull();
        _candidates.Count.ShouldBe(1);
    }

    [Fact]
    public void ReportsTheWholePhrase() => _candidates[0].Phrase.ShouldBe("go to hell");

    [Fact]
    public void StartsInsideTheSegment() => _candidates[0].ApproxStart.ShouldBeGreaterThan(10.0);

    [Fact]
    public void EndsInsideTheSegment() => _candidates[0].ApproxEnd.ShouldBeLessThan(14.0);

    [Fact]
    public void SpansMoreThanASingleWord() =>
        (_candidates[0].ApproxEnd - _candidates[0].ApproxStart).ShouldBeGreaterThan(0.5);
}

/// <summary>Ports <c>test_apostrophes_survive_tokenization</c>.</summary>
public class WhenASegmentContainsAnApostrophe
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenASegmentContainsAnApostrophe()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [new Segment(0.0, 1.0, "you son of a bitch")],
            BadWordsList.FromLines(["bitch", "don't"])
        );

        _candidates.ShouldNotBeNull();
    }

    [Fact]
    public void StillMatchesThePlainWord() => _candidates.Count.ShouldBe(1);

    [Fact]
    public void MatchesAnEntryThatItselfHasAnApostrophe() =>
        PhraseMatcher
            .FindCandidates(
                [new Segment(0.0, 1.0, "Don't you dare")],
                BadWordsList.FromLines(["don't"])
            )
            .Count.ShouldBe(1);
}

/// <summary>Ports <c>test_no_false_positive_substrings</c> - the whole point of token matching.</summary>
public class WhenAnEntryAppearsOnlyAsASubstring
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenAnEntryAppearsOnlyAsASubstring()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [new Segment(0.0, 1.0, "classify the class")],
            BadWordsList.FromLines(["ass"])
        );

        _candidates.ShouldNotBeNull();
    }

    [Fact]
    public void FindsNothing() => _candidates.ShouldBeEmpty();

    [Fact]
    public void StillMatchesTheWholeToken() =>
        PhraseMatcher
            .FindCandidates([new Segment(0.0, 1.0, "what an ass")], BadWordsList.FromLines(["ass"]))
            .Count.ShouldBe(1);
}

/// <summary>
/// The dedupe rule from <c>detector._dedupe</c>: longest first, then anything
/// fully contained in a kept match is dropped.
/// </summary>
public class WhenAShorterEntryIsNestedInsideALongerMatch
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenAShorterEntryIsNestedInsideALongerMatch()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [new Segment(0.0, 4.0, "he can go to hell")],
            BadWordsList.FromLines(["go to hell", "hell"])
        );

        _candidates.ShouldNotBeNull();
        _candidates.ShouldAllBe(c => c.ApproxEnd > c.ApproxStart);
    }

    [Fact]
    public void KeepsOnlyOneMatch() => _candidates.Count.ShouldBe(1);

    [Fact]
    public void KeepsTheLongestPhrase() => _candidates[0].Phrase.ShouldBe("go to hell");

    [Fact]
    public void StillMatchesTheShortEntryWhereItStandsAlone() =>
        PhraseMatcher
            .FindCandidates(
                [new Segment(0.0, 4.0, "what the hell")],
                BadWordsList.FromLines(["go to hell", "hell"])
            )[0]
            .Phrase.ShouldBe("hell");
}

public class WhenTheSameWordOccursSeveralTimesInOneSegment
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenTheSameWordOccursSeveralTimesInOneSegment()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [new Segment(0.0, 6.0, "damn and damn and damn")],
            BadWordsList.FromLines(["damn"])
        );

        _candidates.ShouldNotBeNull();
        _candidates.ShouldAllBe(c => c.Phrase == "damn");
    }

    [Fact]
    public void ReportsEveryOccurrence() => _candidates.Count.ShouldBe(3);

    [Fact]
    public void GivesEachOccurrenceItsOwnWindow() =>
        _candidates.Select(c => c.ApproxStart).Distinct().Count().ShouldBe(3);

    [Fact]
    public void ReportsThemInTextOrder() =>
        _candidates.Select(c => c.ApproxStart).ShouldBeInOrder();
}

public class WhenMatchingAcrossSeveralSegments
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenMatchingAcrossSeveralSegments()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [
                new Segment(0.0, 2.0, "nothing to see"),
                new Segment(2.0, 4.0, "oh damn"),
                new Segment(4.0, 6.0, "hell no"),
            ],
            BadWordsList.FromLines(["damn", "hell"])
        );

        _candidates.ShouldNotBeNull();
        _candidates.Count.ShouldBe(2);
    }

    [Fact]
    public void TagsEachCandidateWithItsSegmentIndex() =>
        _candidates.Select(c => c.SegmentIndex).ShouldBe([1, 2]);

    [Fact]
    public void InterpolatesTimesWithinTheOwningSegment() =>
        _candidates[0].ApproxStart.ShouldBeGreaterThanOrEqualTo(2.0);

    [Fact]
    public void KeepsCandidatesInsideTheirSegment() =>
        _candidates[1].ApproxEnd.ShouldBeLessThanOrEqualTo(6.0);

    [Fact]
    public void RoundsApproximateTimesToThreePlaces() =>
        _candidates.ShouldAllBe(c => c.ApproxStart == Times.Round(c.ApproxStart));
}

public class WhenASegmentHoldsNoMatch
{
    private readonly IReadOnlyList<Candidate> _candidates;

    public WhenASegmentHoldsNoMatch()
    {
        _candidates = PhraseMatcher.FindCandidates(
            [new Segment(0.0, 1.0, ""), new Segment(1.0, 2.0, "all quite polite")],
            BadWordsList.FromLines(["damn"])
        );

        _candidates.ShouldNotBeNull();
    }

    [Fact]
    public void FindsNothing() => _candidates.ShouldBeEmpty();

    [Fact]
    public void SurvivesAnEmptySegmentList() =>
        PhraseMatcher.FindCandidates([], BadWordsList.FromLines(["damn"])).ShouldBeEmpty();
}

/// <summary>Ports <c>test_find_hits_returns_word_spans</c>.</summary>
public class WhenMatchingOverAlignedWords
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenMatchingOverAlignedWords()
    {
        _hits = PhraseMatcher.FindHits(
            [
                new Word("go", 1.0, 1.2),
                new Word("to", 1.2, 1.4),
                new Word("hell", 1.4, 1.9),
                new Word("friend", 1.9, 2.4),
            ],
            BadWordsList.FromLines(["hell"])
        );

        _hits.ShouldNotBeNull();
        _hits.Count.ShouldBe(1);
    }

    [Fact]
    public void ReportsThePhrase() => _hits[0].Phrase.ShouldBe("hell");

    [Fact]
    public void RecordsTheWordIndex() => _hits[0].WordIndex.ShouldBe(2);

    [Fact]
    public void TakesTheStartOfTheFirstWord() => _hits[0].Start.ShouldBe(1.4);

    [Fact]
    public void TakesTheEndOfTheLastWord() => _hits[0].End.ShouldBe(1.9);
}

public class WhenAPhraseSpansSeveralAlignedWords
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenAPhraseSpansSeveralAlignedWords()
    {
        _hits = PhraseMatcher.FindHits(
            [
                new Word("just", 5.0, 5.2),
                new Word("go", 5.6, 5.8),
                new Word("to", 5.8, 5.9),
                new Word("hell", 5.9, 6.2),
            ],
            BadWordsList.FromLines(["go to hell", "hell"])
        );

        _hits.ShouldNotBeNull();
        _hits.Count.ShouldBe(1);
    }

    [Fact]
    public void KeepsTheLongestPhrase() => _hits[0].Phrase.ShouldBe("go to hell");

    [Fact]
    public void StartsAtTheFirstWordOfThePhrase() => _hits[0].Start.ShouldBe(5.6);

    [Fact]
    public void EndsAtTheLastWordOfThePhrase() => _hits[0].End.ShouldBe(6.2);

    [Fact]
    public void IndexesThePhraseByItsFirstWord() => _hits[0].WordIndex.ShouldBe(1);
}

public class WhenAlignedWordsCarryPunctuationAndCase
{
    private readonly IReadOnlyList<Hit> _hits;

    public WhenAlignedWordsCarryPunctuationAndCase()
    {
        _hits = PhraseMatcher.FindHits(
            [new Word("Well,", 0.0, 0.3), new Word("Damn!", 0.3, 0.8)],
            BadWordsList.FromLines(["damn"])
        );

        _hits.ShouldNotBeNull();
        _hits.Count.ShouldBe(1);
    }

    [Fact]
    public void NormalizesTheWordBeforeMatching() => _hits[0].Phrase.ShouldBe("damn");

    [Fact]
    public void KeepsTheAlignedTimestamps() => _hits[0].End.ShouldBe(0.8);

    [Fact]
    public void IgnoresAWordWithNoTokensAtAll() =>
        PhraseMatcher
            .FindHits([new Word("...", 0.0, 0.1)], BadWordsList.FromLines(["damn"]))
            .ShouldBeEmpty();

    [Fact]
    public void SurvivesAnEmptyWordList() =>
        PhraseMatcher.FindHits([], BadWordsList.FromLines(["damn"])).ShouldBeEmpty();
}
