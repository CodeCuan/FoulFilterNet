namespace FoulFilterNet.Domain;

/// <summary>
/// Finds Bad Words List entries in transcribed text. Ported from
/// <c>Legacy/src/detector.py</c>.
/// </summary>
/// <remarks>
/// The same n-gram search serves both stages: over segment text it yields
/// <see cref="Candidate"/>s whose times are interpolated by character offset,
/// and over aligned words it yields <see cref="Hit"/>s with exact times.
/// </remarks>
public static class PhraseMatcher
{
    /// <summary>Locate Candidates in segment text, with approximate times.</summary>
    /// <remarks>
    /// Times are interpolated linearly across the segment by <em>character</em>
    /// offset - the only signal available before alignment runs.
    /// </remarks>
    public static IReadOnlyList<Candidate> FindCandidates(
        IReadOnlyList<Segment> segments,
        BadWordsList badWords
    )
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(badWords);

        var candidates = new List<Candidate>();

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var text = segment.Text ?? string.Empty;
            var tokens = Tokenizer.Tokenize(text);
            if (tokens.Count == 0)
            {
                continue;
            }

            var spans = CharacterSpans(text, tokens);
            var duration = segment.End - segment.Start;
            var totalChars = Math.Max(1, text.Length);

            foreach (var match in Match(tokens, badWords))
            {
                var firstChar = spans[match.Start].Start;
                var lastChar = spans[match.End - 1].End;

                candidates.Add(
                    new Candidate(
                        match.Phrase,
                        index,
                        Times.Round(segment.Start + (duration * firstChar / totalChars)),
                        Times.Round(segment.Start + (duration * lastChar / totalChars))
                    )
                );
            }
        }

        return candidates;
    }

    /// <summary>The same matching over aligned words, with exact times.</summary>
    public static IReadOnlyList<Hit> FindHits(IReadOnlyList<Word> words, BadWordsList badWords)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(badWords);

        if (words.Count == 0)
        {
            return [];
        }

        // An aligned word may still carry punctuation or case ("Hell,"), so it is
        // normalized to its tokens before being compared with a list entry.
        var tokens = new string[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            tokens[i] = Tokenizer.Normalize(words[i].Text);
        }

        var hits = new List<Hit>();
        foreach (var match in Match(tokens, badWords))
        {
            hits.Add(
                new Hit(
                    match.Phrase,
                    words[match.Start].Start,
                    words[match.End - 1].End,
                    match.Start
                )
            );
        }

        return hits;
    }

    /// <summary>
    /// Every n-gram of one to <see cref="BadWordsList.MaxPhraseWords"/> tokens
    /// that is on the list, longest first and then deduped.
    /// </summary>
    private static List<PhraseMatch> Match(IReadOnlyList<string> tokens, BadWordsList badWords)
    {
        var matches = new List<PhraseMatch>();
        var longest = Math.Min(BadWordsList.MaxPhraseWords, tokens.Count);

        for (var size = longest; size > 0; size--)
        {
            for (var start = 0; start + size <= tokens.Count; start++)
            {
                var gram =
                    size == 1 ? tokens[start] : string.Join(' ', tokens.Skip(start).Take(size));
                if (gram.Length == 0)
                {
                    continue;
                }

                if (badWords.Contains(gram))
                {
                    matches.Add(new PhraseMatch(start, start + size, gram));
                }
            }
        }

        return Dedupe(matches);
    }

    /// <summary>
    /// Sort by start ascending then length descending, and drop any match fully
    /// contained in one already kept - so "go to hell" beats the "hell" inside it.
    /// </summary>
    private static List<PhraseMatch> Dedupe(List<PhraseMatch> matches)
    {
        var ordered = matches.OrderBy(m => m.Start).ThenByDescending(m => m.End - m.Start).ToList();

        var kept = new List<PhraseMatch>();
        foreach (var match in ordered)
        {
            var contained = kept.Exists(k => match.Start >= k.Start && match.End <= k.End);
            if (!contained)
            {
                kept.Add(match);
            }
        }

        return kept;
    }

    /// <summary>
    /// Where each token sits in the lowercased segment text, scanning forward so
    /// repeated tokens get successive positions rather than all finding the first.
    /// </summary>
    private static (int Start, int End)[] CharacterSpans(string text, IReadOnlyList<string> tokens)
    {
        var lowered = Tokenizer.Lower(text);
        var spans = new (int Start, int End)[tokens.Count];
        var position = 0;

        for (var i = 0; i < tokens.Count; i++)
        {
            var found =
                position <= lowered.Length
                    ? lowered.IndexOf(tokens[i], position, StringComparison.Ordinal)
                    : -1;

            if (found < 0)
            {
                found = position;
            }

            spans[i] = (found, found + tokens[i].Length);
            position = found + tokens[i].Length;
        }

        return spans;
    }

    /// <summary>One n-gram hit over a token list; <c>End</c> is exclusive.</summary>
    private readonly record struct PhraseMatch(int Start, int End, string Phrase);
}
