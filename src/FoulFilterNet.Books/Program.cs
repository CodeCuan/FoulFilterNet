using System.CommandLine;
using FoulFilterNet.Domain;
using FoulFilterNet.Pipeline;

namespace FoulFilterNet.Books;

/// <summary>
/// <c>foulfilter-books</c>: a folder of EPUBs in, censored copies out. No model and
/// no FFmpeg are involved, so it needs nothing the Bad Words List does not.
/// </summary>
internal static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        var folder = new Argument<DirectoryInfo>("folder")
        {
            Description = "Folder holding the .epub files (not searched recursively)",
        };
        var output = new Option<DirectoryInfo?>("--output")
        {
            Description = "Where the censored copies go (default: <folder>/Filtered)",
        };
        var badWords = new Option<FileInfo?>("--bad-words")
        {
            Description = "Bad Words List (default: bad_words.txt in the data directory)",
        };
        var partialWords = new Option<FileInfo?>("--partial-words")
        {
            Description =
                "Words masked even inside another word (default: partial_words.txt in the data directory, if present)",
        };
        var mask = new Option<string>("--mask")
        {
            Description = "What replaces a word or phrase",
            DefaultValueFactory = _ => "****",
        };

        var command = new RootCommand("Censor the text of every EPUB in a folder.")
        {
            folder,
            output,
            badWords,
            partialWords,
            mask,
        };

        command.SetAction(parsed =>
        {
            var input = parsed.GetRequiredValue(folder);
            if (!input.Exists)
            {
                Console.Error.WriteLine($"No such folder: {input.FullName}");
                return 1;
            }

            var dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR");
            var listPath = DataLocations.BadWordsPath(
                dataDirectory,
                parsed.GetValue(badWords)?.FullName
            );
            if (!File.Exists(listPath))
            {
                Console.Error.WriteLine($"No Bad Words List at {listPath}");
                return 1;
            }

            var matcher = new TextMatcher(
                BadWordsList.FromLines(File.ReadLines(listPath)),
                PartialWordFile.Load(
                    DataLocations.PartialWordsPath(
                        dataDirectory,
                        parsed.GetValue(partialWords)?.FullName
                    )
                )
            );
            var filter = new EpubFilter(new MarkupCensor(matcher, parsed.GetRequiredValue(mask)));

            return new BookFolderRunner(filter, Console.Out).Run(
                input.FullName,
                parsed.GetValue(output)?.FullName ?? Path.Combine(input.FullName, "Filtered")
            );
        });

        return await command
            .Parse(args)
            .InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = true });
    }
}
