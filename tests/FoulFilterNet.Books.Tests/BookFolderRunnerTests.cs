using System.IO.Compression;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Books.Tests;

public sealed class BookFolderRunnerTests : IDisposable
{
    private readonly string _folder = Directory
        .CreateTempSubdirectory("foulfilter-books-")
        .FullName;
    private readonly StringWriter _out = new();
    private readonly BookFolderRunner _runner;

    public BookFolderRunnerTests() =>
        _runner = new BookFolderRunner(
            new EpubFilter(
                new MarkupCensor(new TextMatcher(BadWordsList.FromLines(["damn"]), []), "****")
            ),
            _out
        );

    private string Filtered => Path.Combine(_folder, "Filtered");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void WriteBook(string name, string chapter)
    {
        using var zip = ZipFile.Open(Path.Combine(_folder, name), ZipArchiveMode.Create);
        using (var mimetype = zip.CreateEntry("mimetype").Open())
        {
            mimetype.Write("application/epub+zip"u8);
        }

        using var stream = new StreamWriter(zip.CreateEntry("c.xhtml").Open());
        stream.Write(chapter);
    }

    [Fact]
    public void EveryBookLandsInTheOutputFolderUnderItsOwnName()
    {
        WriteBook("a.epub", "<p>damn</p>");
        WriteBook("b.epub", "<p>x</p>");

        _runner.Run(_folder, Filtered).ShouldBe(0);

        Directory
            .GetFiles(Filtered, "*.epub")
            .Select(Path.GetFileName)
            .Order()
            .ShouldBe(["a.epub", "b.epub"]);
    }

    [Fact]
    public void TheOriginalIsLeftAlone()
    {
        WriteBook("a.epub", "<p>damn</p>");
        var before = File.ReadAllBytes(Path.Combine(_folder, "a.epub"));

        _runner.Run(_folder, Filtered);

        File.ReadAllBytes(Path.Combine(_folder, "a.epub")).ShouldBe(before);
    }

    [Fact]
    public void ABadFileFailsTheRunButNotTheOtherBooks()
    {
        File.WriteAllText(Path.Combine(_folder, "a.epub"), "not a book");
        WriteBook("b.epub", "<p>damn</p>");

        _runner.Run(_folder, Filtered).ShouldBe(1);

        File.Exists(Path.Combine(Filtered, "a.epub")).ShouldBeFalse();
        File.Exists(Path.Combine(Filtered, "b.epub")).ShouldBeTrue();
    }

    [Fact]
    public void ARerunReplacesEarlierOutput()
    {
        WriteBook("a.epub", "<p>damn</p>");
        _runner.Run(_folder, Filtered);

        _runner.Run(_folder, Filtered).ShouldBe(0);
    }

    [Fact]
    public void NearMissesAreWrittenToAReportWithCountsAcrossBooks()
    {
        WriteBook("a.epub", "<p>damned damned</p>");
        WriteBook("b.epub", "<p>damned goddamn</p>");

        _runner.Run(_folder, Filtered);

        File.ReadAllLines(Path.Combine(Filtered, BookFolderRunner.NearMissReportName))
            .ShouldBe(["3\tdamned\tdamn", "1\tgoddamn\tdamn"]);
    }

    [Fact]
    public void ConsoleOutputNeverNamesAWord()
    {
        WriteBook("a.epub", "<p>damn damned</p>");

        _runner.Run(_folder, Filtered);

        _out.ToString().ShouldNotContain("damn");
    }

    [Fact]
    public void AFolderWithNoBooksIsAnError() => _runner.Run(_folder, Filtered).ShouldBe(1);

    [Fact]
    public void WritingOverTheInputFolderIsRefused()
    {
        WriteBook("a.epub", "<p>damn</p>");

        _runner.Run(_folder, _folder + Path.DirectorySeparatorChar).ShouldBe(1);

        File.ReadAllBytes(Path.Combine(_folder, "a.epub")).Length.ShouldBeGreaterThan(0);
    }
}
