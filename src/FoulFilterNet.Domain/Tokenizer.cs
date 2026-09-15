using System.Text.RegularExpressions;

namespace FoulFilterNet.Domain;

/// <summary>
/// The one tokenizer the whole application matches with: <c>[a-z0-9']+</c> over
/// lowercased text.
/// </summary>
/// <remarks>
/// Apostrophes are part of a token and everything else is a separator, which is
/// why matching is over whole tokens and never substrings - "classify the class"
/// must not match <c>ass</c>. Ported from <c>Legacy/src/detector.py</c>.
/// </remarks>
public static partial class Tokenizer
{
    [GeneratedRegex("[a-z0-9']+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>Lowercase <paramref name="text"/> and split it into tokens.</summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var matches = TokenPattern().Matches(Lower(text));
        if (matches.Count == 0)
        {
            return [];
        }

        var tokens = new string[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            tokens[i] = matches[i].Value;
        }

        return tokens;
    }

    /// <summary>
    /// The matchable form of a single piece of text: its tokens, space-joined.
    /// Punctuation and case fall away, so an aligned word rendered "Hell," still
    /// matches the entry <c>hell</c>.
    /// </summary>
    public static string Normalize(string? text) => string.Join(' ', Tokenize(text));

    /// <summary>
    /// The lowercased form token offsets are measured against. Kept in one place
    /// so character spans taken from it always line up with
    /// <see cref="Tokenize(string?)"/>.
    /// </summary>
    internal static string Lower(string text) => text.ToLowerInvariant();
}
