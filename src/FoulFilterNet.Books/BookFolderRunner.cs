using System.Globalization;

namespace FoulFilterNet.Books;

/// <summary>Filters every EPUB directly inside a folder, and reports on the run.</summary>
/// <remarks>
/// The terminal only ever gets counts. The list is personal, so words go in the
/// report file, where seeing them is the point.
/// </remarks>
public sealed class BookFolderRunner
{
    /// <summary>The report's name in the output folder: count, word, listed word it contains.</summary>
    public const string NearMissReportName = "near-misses.txt";

    private readonly EpubFilter _filter;
    private readonly TextWriter _out;

    public BookFolderRunner(EpubFilter filter, TextWriter output)
    {
        _filter = filter;
        _out = output;
    }

    /// <returns>0 when every book was filtered; otherwise 1.</returns>
    public int Run(string folder, string outputFolder)
    {
        if (SamePath(folder, outputFolder))
        {
            _out.WriteLine(
                "The output folder must not be the input folder: originals would be lost."
            );
            return 1;
        }

        var books = Directory.GetFiles(folder, "*.epub").Order(StringComparer.Ordinal).ToList();
        if (books.Count == 0)
        {
            _out.WriteLine($"No .epub files in {folder}.");
            return 1;
        }

        Directory.CreateDirectory(outputFolder);

        var failures = 0;
        var nearMisses = new Dictionary<(string Word, string Contains), int>();
        foreach (var book in books)
        {
            var name = Path.GetFileName(book);
            var tally = new BookTally();
            try
            {
                _filter.Filter(book, Path.Combine(outputFolder, name), tally);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException)
            {
                failures++;
                _out.WriteLine($"FAILED  {name}: {e.Message}");
                continue;
            }

            foreach (var (key, count) in tally.NearMisses)
            {
                nearMisses[key] = nearMisses.GetValueOrDefault(key) + count;
            }

            _out.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tally.Masked, 6} masked, {tally.NearMisses.Values.Sum(), 5} near misses  {name}"
                )
            );
        }

        var report = Path.Combine(outputFolder, NearMissReportName);
        File.WriteAllLines(
            report,
            nearMisses
                .OrderByDescending(n => n.Value)
                .ThenBy(n => n.Key.Word, StringComparer.Ordinal)
                .Select(n =>
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{n.Value}\t{n.Key.Word}\t{n.Key.Contains}"
                    )
                )
        );

        _out.WriteLine(
            $"{books.Count - failures} of {books.Count} books filtered into {outputFolder}. Near misses: {report}"
        );
        return failures == 0 ? 0 : 1;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase
        );
}
