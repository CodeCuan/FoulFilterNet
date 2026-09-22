namespace FoulFilterNet.Domain;

/// <summary>
/// The Priority Word List: the short list of single words the Priority Word
/// Pass re-hears each window for, normalized the way the Bad Words List is.
/// </summary>
/// <remarks>
/// <para>
/// Lines are read in the <c>bad_words.txt</c> format: blank lines and <c>#</c>
/// comments are skipped, and each entry is reduced to its tokens by
/// <see cref="Tokenizer"/>, so <c>Fucking,</c> becomes <c>fucking</c>.
/// </para>
/// <para>
/// <b>Only single-word entries are kept.</b> The pass keeps words, one at a
/// time, out of what the short prompted sub-windows heard; a phrase could not be
/// matched that way, and a phrase in the prompt was not what was measured. A
/// multi-word entry is ignored silently, as the Bad Words List ignores a phrase
/// longer than it can match. Phrases stay on the Bad Words List, where the
/// primary pass finds them.
/// </para>
/// <para>
/// <b>An empty list is allowed</b>, unlike the Bad Words List: a file with no
/// usable single word is how a user turns the pass off without touching
/// configuration.
/// </para>
/// </remarks>
public sealed class PriorityWordList
{
    private readonly string[] _words;
    private readonly HashSet<string> _set;

    private PriorityWordList(string[] words)
    {
        _words = words;
        _set = new HashSet<string>(words, StringComparer.Ordinal);
        Prompt = string.Join(", ", words);
    }

    /// <summary>
    /// The built-in list, used when no <c>priority_words.txt</c> exists: the
    /// F-word family, which is what crosstalk most often hides.
    /// </summary>
    public static PriorityWordList Default { get; } =
        new(["fuck", "fucks", "fucking", "fuckin", "fucked", "fucker", "motherfucker"]);

    /// <summary>Normalize raw list lines, keeping single-word entries in the order given.</summary>
    public static PriorityWordList FromLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw?.Trim() ?? string.Empty;
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var tokens = Tokenizer.Tokenize(line);
            if (tokens.Count == 1 && seen.Add(tokens[0]))
            {
                words.Add(tokens[0]);
            }
        }

        return new PriorityWordList([.. words]);
    }

    /// <summary>The normalized words, in list order.</summary>
    public IReadOnlyList<string> Words => _words;

    /// <summary>How many distinct words the list holds.</summary>
    public int Count => _words.Length;

    /// <summary>True when there is nothing to hunt for, which turns the pass off.</summary>
    public bool IsEmpty => _words.Length == 0;

    /// <summary>
    /// The initial prompt for the prompted sub-window processor: the bare words
    /// joined with <c>", "</c>. Measured better than a natural sentence
    /// (docs/05-crosstalk-plan.md). Empty for an empty list.
    /// </summary>
    public string Prompt { get; }

    /// <summary>True when <paramref name="token"/> is on the list verbatim, already normalized.</summary>
    public bool Contains(string token) => _set.Contains(token);

    /// <summary>
    /// True when <paramref name="wordText"/>, as a transcriber rendered it
    /// (" Fucking,"), normalizes to a single token on the list.
    /// </summary>
    public bool Matches(string? wordText) => _set.Contains(Tokenizer.Normalize(wordText));
}
