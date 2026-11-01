using FoulFilterNet.Domain;

namespace FoulFilterNet.Books;

/// <summary>What one run of text held: the spans to mask, and the near misses.</summary>
public sealed record TextMatches(
    IReadOnlyList<(int Start, int Length)> Spans,
    IReadOnlyList<(string Word, string Contains)> NearMisses
);

/// <summary>
/// Finds what to mask in prose: Bad Words List entries as whole tokens, as audio
/// does, plus Partial Words anywhere inside a token.
/// </summary>
/// <remarks>
/// Prose needs apostrophes handled in a way transcripts do not. Books typeset them
/// curly, which the tokenizer would treat as a separator, so they are straightened
/// first - and then a quote mark is part of the token, so <c>'damn'</c> is trimmed
/// back to <c>damn</c> and <c>ass's</c> falls back to <c>ass</c>.
/// </remarks>
public sealed class TextMatcher
{
    private readonly HashSet<string> _phrases;
    private readonly IReadOnlyCollection<string> _partialWords;
    private readonly List<string> _listedWords;

    public TextMatcher(BadWordsList badWords, IReadOnlyCollection<string> partialWords)
    {
        ArgumentNullException.ThrowIfNull(badWords);

        _phrases = new HashSet<string>(
            badWords.Phrases.Select(p => string.Join(' ', p.Split(' ').Select(TrimQuotes))),
            StringComparer.Ordinal
        );
        _partialWords = partialWords;
        _listedWords =
        [
            .. _phrases.Where(p => p.Length > 0 && !p.Contains(' ', StringComparison.Ordinal)),
        ];
    }

    public TextMatches Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var straightened = text.Replace('’', '\'').Replace('‘', '\'').Replace('ʼ', '\'');
        var tokens = TrimmedTokens(straightened);
        var masked = new List<(int Start, int End)>();
        var covered = new bool[tokens.Count];

        for (var size = BadWordsList.MaxPhraseWords; size > 1; size--)
        {
            for (var start = 0; start + size <= tokens.Count; start++)
            {
                var gram = string.Join(' ', tokens.Skip(start).Take(size).Select(t => t.Text));
                if (_phrases.Contains(gram))
                {
                    masked.Add((tokens[start].Start, tokens[start + size - 1].End));
                    Array.Fill(covered, true, start, size);
                }
            }
        }

        var nearMisses = new List<(string, string)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (_partialWords.Any(p => token.Text.Contains(p, StringComparison.Ordinal)))
            {
                masked.Add((token.Start, token.End));
            }
            else if (_phrases.Contains(token.Text))
            {
                masked.Add((token.Start, token.End));
            }
            else if (
                token.Text.EndsWith("'s", StringComparison.Ordinal)
                && _phrases.Contains(token.Text[..^2])
            )
            {
                masked.Add((token.Start, token.End - 2));
            }
            else if (!covered[i])
            {
                var contained = _listedWords.Find(w =>
                    token.Text.Contains(w, StringComparison.Ordinal)
                );
                if (contained is not null)
                {
                    nearMisses.Add((token.Text, contained));
                }
            }
        }

        return new TextMatches(Merge(masked), nearMisses);
    }

    private static List<(string Text, int Start, int End)> TrimmedTokens(string text)
    {
        var lowered = text.ToLowerInvariant();
        var tokens = new List<(string, int, int)>();

        foreach (var (start, length) in Tokenizer.TokenSpans(text))
        {
            var token = lowered.Substring(start, length);
            var trimmed = TrimQuotes(token);
            if (trimmed.Length > 0)
            {
                var offset = token.IndexOf(trimmed, StringComparison.Ordinal);
                tokens.Add((trimmed, start + offset, start + offset + trimmed.Length));
            }
        }

        return tokens;
    }

    private static string TrimQuotes(string token) => token.Trim('\'');

    private static List<(int Start, int Length)> Merge(List<(int Start, int End)> spans)
    {
        var merged = new List<(int Start, int Length)>();
        var end = -1;

        foreach (var span in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start < end)
            {
                end = Math.Max(end, span.End);
                merged[^1] = (merged[^1].Start, end - merged[^1].Start);
            }
            else
            {
                merged.Add((span.Start, span.End - span.Start));
                end = span.End;
            }
        }

        return merged;
    }
}
