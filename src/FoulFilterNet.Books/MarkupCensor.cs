using System.Net;
using System.Security;
using System.Text;

namespace FoulFilterNet.Books;

/// <summary>What filtering a book came to.</summary>
public sealed class BookTally
{
    public int Masked { get; set; }

    public Dictionary<(string Word, string Contains), int> NearMisses { get; } = [];
}

/// <summary>
/// Masks the text of an (X)HTML or XML document and leaves every other byte alone.
/// </summary>
/// <remarks>
/// This scans rather than parses, deliberately. EPUB chapters carry doctypes and
/// HTML entities a strict XML reader refuses, and reserializing would reformat
/// markup a reader's CSS can depend on, when all that is wanted is a few edits.
/// Text is gathered per block, across inline tags, because books style drop caps as
/// <c>&lt;span&gt;F&lt;/span&gt;irst</c> and a word split that way is still a word.
/// </remarks>
public sealed class MarkupCensor
{
    // Anything not named here ends the block, unknown elements included: a missed
    // match across an unknown tag is rarer than two words wrongly joined by one.
    private static readonly HashSet<string> InlineElements = new(StringComparer.Ordinal)
    {
        "a",
        "abbr",
        "b",
        "bdi",
        "bdo",
        "big",
        "cite",
        "code",
        "del",
        "dfn",
        "em",
        "font",
        "i",
        "ins",
        "kbd",
        "mark",
        "q",
        "s",
        "samp",
        "small",
        "span",
        "strike",
        "strong",
        "sub",
        "sup",
        "tt",
        "u",
        "var",
    };

    private readonly TextMatcher _matcher;
    private readonly string _mask;

    public MarkupCensor(TextMatcher matcher, string mask)
    {
        _matcher = matcher;
        _mask = SecurityElement.Escape(mask);
    }

    /// <summary>The document with its text masked.</summary>
    /// <param name="onlyInside">When given, only text directly in these elements is censored.</param>
    public string Censor(string markup, BookTally tally, IReadOnlySet<string>? onlyInside)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(tally);

        var block = new TextBlock();
        var edits = new List<(int Start, int End, string Replacement)>();
        var position = 0;
        var active = onlyInside is null;

        void Flush()
        {
            block.MaskInto(edits, _matcher, _mask, tally);
            block.Clear();
        }

        while (position < markup.Length)
        {
            if (markup[position] != '<')
            {
                var next = markup.IndexOf('<', position);
                var end = next < 0 ? markup.Length : next;
                if (active)
                {
                    block.Append(markup, position, end);
                }

                position = end;
                continue;
            }

            if (SkipDeclaration(markup, position) is { } afterDeclaration)
            {
                Flush();
                position = afterDeclaration;
                continue;
            }

            var tagEnd = TagEnd(markup, position);
            var (name, closing, selfClosing) = ReadTag(markup, position, tagEnd);

            if (onlyInside is not null)
            {
                Flush();
                active = !closing && !selfClosing && onlyInside.Contains(name);
            }
            else if (!InlineElements.Contains(name))
            {
                Flush();
            }

            position = tagEnd;

            if (!closing && !selfClosing && name is "script" or "style")
            {
                var close = markup.IndexOf(
                    "</" + name,
                    position,
                    StringComparison.OrdinalIgnoreCase
                );
                position = close < 0 ? markup.Length : close;
            }
        }

        Flush();
        return Apply(markup, edits);
    }

    private static int? SkipDeclaration(string markup, int position)
    {
        static int After(string markup, string terminator, int from)
        {
            var found = markup.IndexOf(terminator, from, StringComparison.Ordinal);
            return found < 0 ? markup.Length : found + terminator.Length;
        }

        if (string.CompareOrdinal(markup, position, "<!--", 0, 4) == 0)
        {
            return After(markup, "-->", position);
        }

        if (string.CompareOrdinal(markup, position, "<![CDATA[", 0, 9) == 0)
        {
            return After(markup, "]]>", position);
        }

        if (string.CompareOrdinal(markup, position, "<?", 0, 2) == 0)
        {
            return After(markup, "?>", position);
        }

        if (string.CompareOrdinal(markup, position, "<!", 0, 2) == 0)
        {
            var close = After(markup, ">", position);
            var subset = markup.IndexOf('[', position, close - position);
            return subset < 0 ? close : After(markup, "]>", subset);
        }

        return null;
    }

    private static int TagEnd(string markup, int position)
    {
        char? quote = null;
        for (var i = position + 1; i < markup.Length; i++)
        {
            var c = markup[i];
            if (quote is not null)
            {
                quote = c == quote ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }

        return markup.Length;
    }

    private static (string Name, bool Closing, bool SelfClosing) ReadTag(
        string markup,
        int start,
        int end
    )
    {
        var closing = start + 1 < end && markup[start + 1] == '/';
        var nameStart = start + (closing ? 2 : 1);
        var nameEnd = nameStart;
        while (nameEnd < end && markup[nameEnd] is not (' ' or '\t' or '\r' or '\n' or '/' or '>'))
        {
            nameEnd++;
        }

#pragma warning disable CA1308 // Element names are compared lowercased; nothing is displayed.
        var name = markup[nameStart..nameEnd].ToLowerInvariant();
#pragma warning restore CA1308
        return (name, closing, end >= 2 && markup[end - 2] == '/');
    }

    private static string Apply(string markup, List<(int Start, int End, string Replacement)> edits)
    {
        if (edits.Count == 0)
        {
            return markup;
        }

        var result = new StringBuilder(markup.Length);
        var position = 0;
        foreach (var (start, end, replacement) in edits.OrderBy(e => e.Start))
        {
            result.Append(markup, position, start - position).Append(replacement);
            position = end;
        }

        return result.Append(markup, position, markup.Length - position).ToString();
    }

    /// <summary>A block's decoded text, each character knowing the source it came from.</summary>
    private sealed class TextBlock
    {
        private readonly StringBuilder _text = new();
        private readonly List<int> _starts = [];
        private readonly List<int> _ends = [];

        public void Clear()
        {
            _text.Clear();
            _starts.Clear();
            _ends.Clear();
        }

        public void Append(string markup, int start, int end)
        {
            var i = start;
            while (i < end)
            {
                if (markup[i] == '&')
                {
                    var semicolon = markup.IndexOf(';', i, Math.Min(12, end - i));
                    if (semicolon > 0)
                    {
                        var entity = markup[i..(semicolon + 1)];
                        var decoded = WebUtility.HtmlDecode(entity);
                        if (decoded != entity)
                        {
                            foreach (var c in decoded)
                            {
                                Add(c, i, semicolon + 1);
                            }

                            i = semicolon + 1;
                            continue;
                        }
                    }
                }

                Add(markup[i], i, i + 1);
                i++;
            }
        }

        private void Add(char c, int start, int end)
        {
            // A soft hyphen or zero-width character splits nothing on the page, so
            // it must not split a token. It joins the character before it, and a
            // mask over that character takes it too.
            if (c is '­' or '​' or '‌' or '‍' or '⁠' or '﻿')
            {
                if (_ends.Count > 0 && _ends[^1] == start)
                {
                    _ends[^1] = end;
                }

                return;
            }

            _text.Append(c);
            _starts.Add(start);
            _ends.Add(end);
        }

        public void MaskInto(
            List<(int Start, int End, string Replacement)> edits,
            TextMatcher matcher,
            string mask,
            BookTally tally
        )
        {
            if (_text.Length == 0)
            {
                return;
            }

            var matches = matcher.Find(_text.ToString());
            tally.Masked += matches.Spans.Count;
            foreach (var nearMiss in matches.NearMisses)
            {
                tally.NearMisses[nearMiss] = tally.NearMisses.GetValueOrDefault(nearMiss) + 1;
            }

            foreach (var (start, length) in matches.Spans)
            {
                // A span crossing inline tags becomes one mask in its first piece,
                // and the tags stay balanced because only text between them goes.
                var replacement = mask;
                var pieceStart = _starts[start];
                for (var i = start; i < start + length; i++)
                {
                    var last = i == start + length - 1;
                    if (last || _ends[i] != _starts[i + 1])
                    {
                        edits.Add((pieceStart, _ends[i], replacement));
                        replacement = string.Empty;
                        if (!last)
                        {
                            pieceStart = _starts[i + 1];
                        }
                    }
                }
            }
        }
    }
}
