using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FoulFilterNet.Books;

/// <summary>Writes a censored copy of an EPUB; only entries that changed differ.</summary>
public sealed partial class EpubFilter
{
    private const string MimetypeEntry = "mimetype";
    private const string ContainerEntry = "META-INF/container.xml";
    private const string EncryptionEntry = "META-INF/encryption.xml";

    // The package holds identifiers and manifest paths that must not change, and a
    // reader only ever shows these.
    private static readonly HashSet<string> PackageElementsShown = new(StringComparer.Ordinal)
    {
        "dc:title",
        "dc:description",
        "dc:subject",
    };

    private readonly MarkupCensor _censor;

    static EpubFilter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public EpubFilter(MarkupCensor censor) => _censor = censor;

    [GeneratedRegex("""encoding\s*=\s*["']([A-Za-z0-9_.:-]+)["']""")]
    private static partial Regex DeclaredEncoding();

    [GeneratedRegex("""CipherReference[^>]*URI\s*=\s*["']([^"']+)["']""")]
    private static partial Regex CipherReference();

    /// <exception cref="InvalidDataException">The file is not an EPUB that can be censored.</exception>
    public void Filter(string input, string output, BookTally tally)
    {
        ArgumentNullException.ThrowIfNull(tally);

        RefuseOtherFormats(input);

        using var source = ZipFile.OpenRead(input);
        RefuseUnreadable(source);

        // Via a temporary file, so a failure part way through never leaves a
        // half-written book looking like a finished one.
        var temporary = output + ".tmp";
        try
        {
            using (var target = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                // OCF requires the mimetype first and stored, or readers reject the book.
                foreach (var entry in source.Entries.OrderBy(e => e.FullName != MimetypeEntry))
                {
                    Copy(entry, target, tally);
                }
            }

            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static void RefuseOtherFormats(string input)
    {
        Span<byte> head = stackalloc byte[4];
        using (var stream = File.OpenRead(input))
        {
            stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        }

        if (head.SequenceEqual("Rar!"u8))
        {
            throw new InvalidDataException(
                "this is really a RAR archive with an .epub name; extract the book first"
            );
        }

        if (head.SequenceEqual("%PDF"u8))
        {
            throw new InvalidDataException("this is really a PDF with an .epub name");
        }

        if (!head.SequenceEqual("PK\u0003\u0004"u8))
        {
            throw new InvalidDataException("this is not a zip file, so it cannot be an EPUB");
        }
    }

    private static void RefuseUnreadable(ZipArchive source)
    {
        if (source.GetEntry(MimetypeEntry) is null && source.GetEntry(ContainerEntry) is null)
        {
            throw new InvalidDataException("this zip has no EPUB mimetype or container");
        }

        // Font obfuscation also lists itself here and is harmless. An encrypted
        // chapter would pass through looking censored and not being so.
        if (source.GetEntry(EncryptionEntry) is { } encryption)
        {
            using var reader = new StreamReader(encryption.Open());
            foreach (Match reference in CipherReference().Matches(reader.ReadToEnd()))
            {
                if (IsCensorable(reference.Groups[1].Value))
                {
                    throw new InvalidDataException(
                        "this book's text is encrypted (DRM), so it cannot be censored"
                    );
                }
            }
        }
    }

    private static bool IsCensorable(string name) =>
        Path.GetExtension(name).ToUpperInvariant()
            is ".XHTML"
                or ".HTML"
                or ".HTM"
                or ".NCX"
                or ".OPF";

    private void Copy(ZipArchiveEntry entry, ZipArchive target, BookTally tally)
    {
        var bytes = ReadAll(entry);

        if (IsCensorable(entry.FullName))
        {
            bytes = CensorEntry(entry.FullName, bytes, tally);
        }

        var level =
            entry.FullName == MimetypeEntry
                ? CompressionLevel.NoCompression
                : CompressionLevel.Optimal;
        var copy = target.CreateEntry(entry.FullName, level);
        copy.LastWriteTime = entry.LastWriteTime;
        using var stream = copy.Open();
        stream.Write(bytes);
    }

    private byte[] CensorEntry(string name, byte[] bytes, BookTally tally)
    {
        var (encoding, preamble) = Detect(bytes);
        var original = encoding.GetString(bytes, preamble, bytes.Length - preamble);
        var onlyInside = name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)
            ? PackageElementsShown
            : null;

        var censored = _censor.Censor(original, tally, onlyInside);
        if (ReferenceEquals(censored, original) || censored == original)
        {
            return bytes;
        }

        return [.. bytes.AsSpan(0, preamble), .. encoding.GetBytes(censored)];
    }

    private static (Encoding Encoding, int Preamble) Detect(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return (new UTF8Encoding(false), 3);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);
        }

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 256));
        var declaration = head.StartsWith("<?xml", StringComparison.Ordinal)
            ? DeclaredEncoding().Match(head)
            : Match.Empty;
        return (
            declaration.Success
                ? Encoding.GetEncoding(declaration.Groups[1].Value)
                : new UTF8Encoding(false),
            0
        );
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
