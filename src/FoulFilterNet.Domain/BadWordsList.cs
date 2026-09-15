namespace FoulFilterNet.Domain;

/// <summary>
/// The Bad Words List, normalized: every entry reduced to between one and
/// <see cref="MaxPhraseWords"/> tokens, space-joined.
/// </summary>
/// <remarks>
/// Ported from <c>normalize_entries</c> and <c>load_bad_words</c> in the Python.
/// Blank lines and <c>#</c> comments are skipped; an entry longer than three
/// tokens is dropped silently, because a longer phrase cannot be matched by the
/// n-gram search and quietly ignoring it is what the original does.
/// </remarks>
public sealed class BadWordsList
{
    /// <summary>The longest phrase that can be matched, in tokens.</summary>
    public const int MaxPhraseWords = 3;

    private readonly HashSet<string> _phrases;

    private BadWordsList(HashSet<string> phrases) => _phrases = phrases;

    /// <summary>Normalize raw list lines.</summary>
    /// <exception cref="ArgumentException">
    /// Every line was blank or a comment. An unusable list is a configuration
    /// error, not a job that silently censors nothing.
    /// </exception>
    public static BadWordsList FromLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var phrases = new HashSet<string>(StringComparer.Ordinal);
        var sawUsableLine = false;

        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? string.Empty;
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            sawUsableLine = true;

            var tokens = Tokenizer.Tokenize(line);
            if (tokens.Count > 0 && tokens.Count <= MaxPhraseWords)
            {
                phrases.Add(string.Join(' ', tokens));
            }
        }

        if (!sawUsableLine)
        {
            throw new ArgumentException("Bad Words List is empty.", nameof(lines));
        }

        return new BadWordsList(phrases);
    }

    /// <summary>How many distinct phrases the list holds.</summary>
    public int Count => _phrases.Count;

    /// <summary>The normalized phrases, each one to three space-joined tokens.</summary>
    public IReadOnlyCollection<string> Phrases => _phrases;

    /// <summary>True when <paramref name="phrase"/> is on the list verbatim, already normalized.</summary>
    public bool Contains(string phrase) => _phrases.Contains(phrase);
}
