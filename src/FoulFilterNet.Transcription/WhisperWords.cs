using FoulFilterNet.Domain;
using Whisper.net;

namespace FoulFilterNet.Transcription;

/// <summary>
/// Turns whisper.cpp's tokens into <see cref="Word"/> records.
/// </summary>
/// <remarks>
/// <para>
/// whisper.cpp has no word API. With token timestamps enabled each
/// <see cref="SegmentData"/> carries <see cref="SegmentData.Tokens"/>, and those
/// are <em>sub-word</em> pieces - "damn" arrives as " da" + "mn", with the
/// leading space on the first piece marking where a word begins. One
/// <see cref="Word"/> per token would therefore end every word where its first
/// syllable ended: plausible-looking timestamps that are systematically early.
/// A word here is the whitespace-delimited word, which is also the unit the Bad
/// Words List is matched against.
/// </para>
/// <para>
/// <strong>Which timestamps.</strong> A token carries two kinds, and ADR-0006
/// measured both against the fixtures' exact spans. The raw <c>t0</c>/<c>t1</c>
/// pair is the old token-timestamp heuristic and is poor: 0.31 s of mean
/// boundary error, 0.90 s at worst, and nine of the fixtures' words come back
/// with <c>t0 == t1</c>. The DTW instant that appears when the model's alignment
/// heads are configured is far better - 0.11 s mean, 0.24 s at worst - and it
/// marks where a token <em>ended</em>. So a word runs from the instant of the
/// token before it to the instant of its own last token, and falls back to
/// <c>t0</c>/<c>t1</c> per word wherever no instant was produced (a model with
/// no heads preset reports -1).
/// </para>
/// <para>
/// Text is lower-cased and trimmed the way <c>Legacy/src/aligner.py</c> emitted
/// it (<c>w["word"].lower().strip()</c>). Punctuation is left attached, since
/// <c>PhraseMatcher.FindHits</c> normalizes before matching.
/// </para>
/// </remarks>
public static class WhisperWords
{
    /// <summary>
    /// Seconds per unit of a token's timestamps. whisper.cpp counts in
    /// centiseconds; reading them as milliseconds would place every word a
    /// hundred times too early.
    /// </summary>
    public const double TokenUnitSeconds = 0.01;

    /// <summary>
    /// The length given to a word whose timestamps collapsed. whisper.cpp
    /// reports <c>t0 == t1</c> often enough to matter, and dropping such a word -
    /// as the Python's aligner did, where it meant alignment had failed - loses
    /// a word that was certainly spoken and might have been a profanity. The hit
    /// padding covers the difference; a missed hit cannot be recovered.
    /// </summary>
    public const double MinimumSpanSeconds = 0.01;

    /// <summary>
    /// Join a token stream into words. Control and timestamp tokens
    /// (<c>[_BEG_]</c>, <c>&lt;|0.00|&gt;</c>) are not speech and are ignored.
    /// </summary>
    /// <remarks>
    /// Takes the whole file's tokens rather than one segment's, because a word's
    /// start comes from the token before it and the first word of a segment's
    /// predecessor is in the segment before.
    /// </remarks>
    public static IReadOnlyList<Word> Join(IReadOnlyList<WhisperToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var spoken = new List<WhisperToken>(tokens.Count);
        var grouped = new List<(string Text, int First, int Last)>();

        // The first piece of all opens a word; so does anything after white
        // space, whether the white space was inside a token, at its edge, or a
        // token of its own.
        var opensWord = true;

        foreach (var token in tokens)
        {
            var text = token.Text;
            if (string.IsNullOrEmpty(text) || IsMarkup(text))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                opensWord = true;
                continue;
            }

            var index = spoken.Count;
            spoken.Add(token);

            // Passing null splits on every kind of white space. A token almost
            // always spells one piece; occasionally it spells two words at once,
            // and the white space is what decides, not the token.
            var pieces = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (var piece = 0; piece < pieces.Length; piece++)
            {
                if (opensWord || piece > 0 || char.IsWhiteSpace(text[0]))
                {
                    grouped.Add((pieces[piece], index, index));
                    opensWord = false;
                }
                else
                {
                    var current = grouped[^1];
                    grouped[^1] = (current.Text + pieces[piece], current.First, index);
                }
            }

            opensWord = char.IsWhiteSpace(text[^1]);
        }

        var words = new List<Word>(grouped.Count);
        foreach (var (text, first, last) in grouped)
        {
            var start = StartOf(spoken, first);
            var end = EndOf(spoken, last);
            if (end <= start)
            {
                end = start + MinimumSpanSeconds;
            }

            words.Add(new Word(text.ToLowerInvariant(), Times.Round(start), Times.Round(end)));
        }

        return words;
    }

    /// <summary>
    /// Where a word began: the DTW instant of the token before it, since those
    /// instants mark token ends. The very first word has no predecessor and
    /// falls back to its own <c>t0</c>.
    /// </summary>
    private static double StartOf(List<WhisperToken> spoken, int index) =>
        index > 0 && spoken[index - 1].DtwTimestamp >= 0
            ? spoken[index - 1].DtwTimestamp * TokenUnitSeconds
            : spoken[index].Start * TokenUnitSeconds;

    /// <summary>Where a word ended: its last token's instant, or <c>t1</c>.</summary>
    private static double EndOf(List<WhisperToken> spoken, int index) =>
        (spoken[index].DtwTimestamp >= 0 ? spoken[index].DtwTimestamp : spoken[index].End)
        * TokenUnitSeconds;

    /// <summary>Control and timestamp tokens, which whisper.cpp spells distinctively.</summary>
    private static bool IsMarkup(string text) =>
        text.StartsWith("[_", StringComparison.Ordinal)
        || text.StartsWith("<|", StringComparison.Ordinal);
}
