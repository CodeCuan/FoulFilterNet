using FoulFilterNet.Domain;

namespace FoulFilterNet.Books;

/// <summary>
/// The optional list of Partial Words: one word per line, blank lines and <c>#</c>
/// comments skipped. A missing file means none.
/// </summary>
public static class PartialWordFile
{
    /// <exception cref="InvalidDataException">An entry is more than one word.</exception>
    public static IReadOnlyList<string> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var words = new List<string>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var tokens = Tokenizer.Tokenize(line);
            if (tokens.Count != 1)
            {
                throw new InvalidDataException(
                    $"{path}: a Partial Word must be one word; a line holds {tokens.Count}."
                );
            }

            if (!words.Contains(tokens[0]))
            {
                words.Add(tokens[0]);
            }
        }

        return words;
    }
}
