using System.IO.Compression;
using System.Text;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Books.Tests;

public sealed class EpubFilterTests : IDisposable
{
    private readonly string _directory = Directory
        .CreateTempSubdirectory("foulfilter-books-")
        .FullName;
    private readonly EpubFilter _filter = new(
        new MarkupCensor(new TextMatcher(BadWordsList.FromLines(["damn"]), []), "****")
    );
    private readonly BookTally _tally = new();

    private string Input => Path.Combine(_directory, "in.epub");
    private string Output => Path.Combine(_directory, "out.epub");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteBook(params (string Name, byte[] Bytes)[] extra)
    {
        using var zip = ZipFile.Open(Input, ZipArchiveMode.Create);
        Add(zip, "mimetype", "application/epub+zip"u8.ToArray(), CompressionLevel.NoCompression);
        Add(zip, "META-INF/container.xml", "<container>damn</container>"u8.ToArray());
        foreach (var (name, bytes) in extra)
        {
            Add(zip, name, bytes);
        }
    }

    private static void Add(
        ZipArchive zip,
        string name,
        byte[] bytes,
        CompressionLevel level = CompressionLevel.Optimal
    )
    {
        using var stream = zip.CreateEntry(name, level).Open();
        stream.Write(bytes);
    }

    private byte[] Read(string name)
    {
        using var zip = ZipFile.OpenRead(Output);
        using var stream = zip.GetEntry(name)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private void Filter() => _filter.Filter(Input, Output, _tally);

    [Fact]
    public void AChapterIsCensored()
    {
        WriteBook(("OEBPS/c1.xhtml", "<p>damn</p>"u8.ToArray()));

        Filter();

        Read("OEBPS/c1.xhtml").ShouldBe("<p>****</p>"u8.ToArray());
    }

    [Fact]
    public void TheTableOfContentsIsCensored()
    {
        WriteBook(("toc.ncx", "<text>damn</text>"u8.ToArray()));

        Filter();

        Read("toc.ncx").ShouldBe("<text>****</text>"u8.ToArray());
    }

    [Fact]
    public void PackageMetadataIsCensoredOnlyWhereAReaderShowsIt()
    {
        WriteBook(
            (
                "content.opf",
                "<dc:identifier>damn</dc:identifier><dc:description>damn</dc:description>"u8.ToArray()
            )
        );

        Filter();

        Encoding
            .UTF8.GetString(Read("content.opf"))
            .ShouldBe("<dc:identifier>damn</dc:identifier><dc:description>****</dc:description>");
    }

    [Fact]
    public void EverythingElseIsCopiedByteForByte()
    {
        byte[] image = [0xFF, 0xD8, (byte)'d', (byte)'a', (byte)'m', (byte)'n'];
        WriteBook(("OEBPS/i.jpg", image), ("OEBPS/s.css", "/* damn */"u8.ToArray()));

        Filter();

        Read("OEBPS/i.jpg").ShouldBe(image);
        Read("OEBPS/s.css").ShouldBe("/* damn */"u8.ToArray());
        Read("META-INF/container.xml").ShouldBe("<container>damn</container>"u8.ToArray());
    }

    [Fact]
    public void MimetypeStaysFirstAndStored()
    {
        WriteBook(("c.xhtml", "<p>x</p>"u8.ToArray()));

        Filter();

        using var zip = ZipFile.OpenRead(Output);
        zip.Entries[0].FullName.ShouldBe("mimetype");
        zip.Entries[0].CompressedLength.ShouldBe(zip.Entries[0].Length);
    }

    [Fact]
    public void AByteOrderMarkAndDeclaredEncodingSurvive()
    {
        var latin1 = Encoding.Latin1;
        byte[] chapter = latin1.GetBytes(
            "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><p>café damn</p>"
        );
        byte[] marked = [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "<p>damn</p>"u8.ToArray()];
        WriteBook(("a.xhtml", chapter), ("b.xhtml", marked));

        Filter();

        latin1
            .GetString(Read("a.xhtml"))
            .ShouldBe("<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><p>café ****</p>");
        Read("b.xhtml")
            .ShouldBe([.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "<p>****</p>"u8.ToArray()]);
    }

    [Fact]
    public void AFileThatIsNotAZipIsRefusedByName()
    {
        File.WriteAllBytes(Input, "Rar!\u001a\u0007\0 rest"u8.ToArray());

        Should.Throw<InvalidDataException>(Filter).Message.ShouldContain("RAR");
        File.Exists(Output).ShouldBeFalse();
    }

    [Fact]
    public void AZipThatIsNotAnEpubIsRefused()
    {
        using (var zip = ZipFile.Open(Input, ZipArchiveMode.Create))
        {
            Add(zip, "readme.txt", "hi"u8.ToArray());
        }

        Should.Throw<InvalidDataException>(Filter);
    }

    [Fact]
    public void AnEncryptedChapterIsRefusedRatherThanPassedThroughUncensored()
    {
        WriteBook(
            ("c.xhtml", "x"u8.ToArray()),
            (
                "META-INF/encryption.xml",
                "<encryption><CipherReference URI=\"c.xhtml\"/></encryption>"u8.ToArray()
            )
        );

        Should.Throw<InvalidDataException>(Filter).Message.ShouldContain("encrypted");
        File.Exists(Output).ShouldBeFalse();
    }

    [Fact]
    public void AnObfuscatedFontIsNotAReasonToRefuse()
    {
        WriteBook(
            ("f.ttf", "x"u8.ToArray()),
            (
                "META-INF/encryption.xml",
                "<encryption><CipherReference URI=\"f.ttf\"/></encryption>"u8.ToArray()
            )
        );

        Should.NotThrow(Filter);
    }
}
