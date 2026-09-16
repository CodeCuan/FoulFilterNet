using System.Text;
using FoulFilterNet.Domain;

namespace FoulFilterNet.Pipeline.Tests;

/// <summary>Shared constants for the transcript cache specs.</summary>
internal static class CacheFixture
{
    /// <summary>A plausible content digest; the store only ever treats it as an opaque key.</summary>
    public const string Digest = "0123456789abcdef0123456789abcdef";

    /// <summary>The digest of some other file entirely.</summary>
    public const string OtherDigest = "ffffffffffffffffffffffffffffffff";

    public static IReadOnlyList<Segment> Segments { get; } =
        [new Segment(0.0, 1.2, "damn it"), new Segment(1.2, 2.4, "quite fine")];

    public static IReadOnlyList<Word> Words { get; } =
        [new Word("damn", 0.1, 0.5), new Word("it", 0.5, 0.7)];

    public static Transcript Cached { get; } =
        new(Transcript.CurrentVersion, Digest, Segments, Words);

    /// <summary>A cache entry as it looks on disk, so the persisted shape is asserted rather than assumed.</summary>
    public static string Entry(int version, string fileHash, string text) =>
        $$"""
          {"version":{{version}},"file_hash":"{{fileHash}}",
           "segments":[{"start":0.0,"end":1.0,"text":"{{text}}"}],"words":[]}
          """;

    public static string Write(string directory, string fileName, string json)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    public static IReadOnlyList<string> JsonFileNames(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*.json").Select(f => Path.GetFileName(f) ?? string.Empty)];
}

public class WhenSavingAndReloadingATranscript : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;
    private readonly string _fileName;
    private readonly string _rawJson;

    public WhenSavingAndReloadingATranscript()
    {
        var sut = new TranscriptStore(_directory.Path);
        sut.SaveAsync(CacheFixture.Cached, "audiobook").GetAwaiter().GetResult();
        _found = sut.FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();

        var file = Directory.EnumerateFiles(_directory.Path, "*.json").Single();
        _fileName = Path.GetFileName(file) ?? string.Empty;
        _rawJson = File.ReadAllText(file);

        _found.ShouldNotBeNull();
    }

    [Fact]
    public void ReturnsTheTranscriptForThatFileRatherThanTranscribingAgain() =>
        _found!.FileHash.ShouldBe(CacheFixture.Digest);

    [Fact]
    public void KeepsEverySegment() => _found!.Segments.Count.ShouldBe(2);

    [Fact]
    public void KeepsSegmentTimesAndText() =>
        _found!.Segments[0].ShouldBe(new Segment(0.0, 1.2, "damn it"));

    [Fact]
    public void KeepsAlignedWords() => _found!.Words.ShouldBe(CacheFixture.Words);

    [Fact]
    public void StampsTheSchemaVersionItWasWrittenWith() =>
        _found!.Version.ShouldBe(Transcript.CurrentVersion);

    [Fact]
    public void NamesTheFileByDigestThenReadableName() =>
        _fileName.ShouldBe($"{CacheFixture.Digest}_audiobook.json");

    [Fact]
    public void RecordsTheHashUnderTheNameTheLegacyCacheUsed() =>
        _rawJson.ShouldContain("\"file_hash\"");

    [Fact]
    public void LeavesNoHalfWrittenTemporaryFileBehind() =>
        Directory.EnumerateFiles(_directory.Path).Count().ShouldBe(1);

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Audiobooks run to gigabytes, so the digest is accumulated a chunk at a time.
/// The expected digests were computed independently of this code, which is what
/// makes a broken chunk loop visible rather than merely self-consistent.
/// </summary>
public class WhenHashingAMediaFile : IDisposable
{
    private const string ShortContentDigest = "6229f211c8b48fc66c1329a49980c419";
    private const string ThreeMegabyteDigest = "b9e8be962fa541bad8cd7e526acd4ffc";

    private readonly TempDirectory _directory = new();
    private readonly string _shortDigest;
    private readonly string _largeDigest;
    private readonly string _repeatedDigest;
    private readonly string _otherDigest;

    public WhenHashingAMediaFile()
    {
        var sut = new TranscriptStore(_directory.Path);

        var small = Path.Combine(_directory.Path, "small.mp3");
        File.WriteAllBytes(small, Encoding.UTF8.GetBytes("foulfilter"));

        var large = Path.Combine(_directory.Path, "large.m4b");
        var bytes = new byte[3 * 1024 * 1024];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        File.WriteAllBytes(large, bytes);

        var other = Path.Combine(_directory.Path, "other.mp3");
        File.WriteAllBytes(other, Encoding.UTF8.GetBytes("foulfilteR"));

        _shortDigest = sut.ComputeHashAsync(small).GetAwaiter().GetResult();
        _repeatedDigest = sut.ComputeHashAsync(small).GetAwaiter().GetResult();
        _largeDigest = sut.ComputeHashAsync(large).GetAwaiter().GetResult();
        _otherDigest = sut.ComputeHashAsync(other).GetAwaiter().GetResult();

        _shortDigest.ShouldNotBeNullOrEmpty();
        _largeDigest.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void MatchesTheDigestOfTheContent() => _shortDigest.ShouldBe(ShortContentDigest);

    [Fact]
    public void HashesAFileLargerThanOneChunkCorrectly() => _largeDigest.ShouldBe(ThreeMegabyteDigest);

    [Fact]
    public void IsStableAcrossCalls() => _repeatedDigest.ShouldBe(_shortDigest);

    [Fact]
    public void DiffersWhenTheContentDiffers() => _otherDigest.ShouldNotBe(_shortDigest);

    [Fact]
    public void IsLowercaseHexSoItCanNameAFileOnAnyFileSystem() =>
        _shortDigest.ShouldAllBe(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Finding 4: the Python wrote <c>{"version": 3}</c> and never read it back, so a
/// transcript written by a differently shaped build was reused as if current.
/// That the constructor completes at all is half the point - a mismatch is a
/// miss, not an exception.
/// </summary>
public class WhenACachedTranscriptWasWrittenByAnotherSchema : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;
    private readonly string _path;

    public WhenACachedTranscriptWasWrittenByAnotherSchema()
    {
        _path = CacheFixture.Write(
            _directory.Path,
            $"{CacheFixture.Digest}_audiobook.json",
            CacheFixture.Entry(99, CacheFixture.Digest, "damn"));

        _found = new TranscriptStore(_directory.Path)
            .FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void TreatsItAsAMissRatherThanReusingIt() => _found.ShouldBeNull();

    [Fact]
    public void LeavesTheFileForWhoeverOwnsThatSchema() => File.Exists(_path).ShouldBeTrue();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A poisoned cache entry may cost a re-transcription; it may never fail a job.</summary>
public class WhenACachedTranscriptIsTruncated : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;

    public WhenACachedTranscriptIsTruncated()
    {
        CacheFixture.Write(
            _directory.Path,
            $"{CacheFixture.Digest}_audiobook.json",
            "{\"version\": 1, \"file_hash\": \"01234");

        _found = new TranscriptStore(_directory.Path)
            .FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsAMissRatherThanThrowing() => _found.ShouldBeNull();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenACacheFileIsNotJsonAtAll : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;

    public WhenACacheFileIsNotJsonAtAll()
    {
        CacheFixture.Write(
            _directory.Path, $"{CacheFixture.Digest}_binary.json", "not json at all");

        _found = new TranscriptStore(_directory.Path)
            .FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsAMissRatherThanThrowing() => _found.ShouldBeNull();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>One unreadable file must not hide a good entry sitting next to it.</summary>
public class WhenAPoisonedEntrySitsBesideAUsableOne : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;

    public WhenAPoisonedEntrySitsBesideAUsableOne()
    {
        // "_audiobook" sorts ahead of "_good", so the usable entry is only
        // reached by stepping past the poisoned one.
        CacheFixture.Write(_directory.Path, $"{CacheFixture.Digest}_audiobook.json", "{ broken");
        CacheFixture.Write(
            _directory.Path,
            $"{CacheFixture.Digest}_good.json",
            CacheFixture.Entry(Transcript.CurrentVersion, CacheFixture.Digest, "damn"));

        _found = new TranscriptStore(_directory.Path)
            .FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void KeepsLookingUntilItFindsOneItCanUse() => _found.ShouldNotBeNull();

    [Fact]
    public void ReturnsTheContentOfTheUsableEntry() => _found!.Segments[0].Text.ShouldBe("damn");

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The digest is in the file name, but the file carries it too - names get copied
/// and renamed, and reusing another file's transcript would censor timestamps
/// that belong to different audio. The Python checked this and so does the port.
/// </summary>
public class WhenACachedTranscriptBelongsToAnotherFile : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;

    public WhenACachedTranscriptBelongsToAnotherFile()
    {
        CacheFixture.Write(
            _directory.Path,
            $"{CacheFixture.Digest}_audiobook.json",
            CacheFixture.Entry(Transcript.CurrentVersion, CacheFixture.OtherDigest, "damn"));

        _found = new TranscriptStore(_directory.Path)
            .FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void TreatsTheMismatchAsAMiss() => _found.ShouldBeNull();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenNothingHasBeenCachedYet : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _missing;
    private readonly Transcript? _found;

    public WhenNothingHasBeenCachedYet()
    {
        _missing = Path.Combine(_directory.Path, "transcripts");

        _found = new TranscriptStore(_missing)
            .FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsAMissForAnAbsentCacheDirectory() => _found.ShouldBeNull();

    [Fact]
    public void DoesNotCreateTheDirectoryJustByLooking() => Directory.Exists(_missing).ShouldBeFalse();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenSavingIntoACacheDirectoryThatDoesNotExistYet : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _missing;
    private readonly Transcript? _found;

    public WhenSavingIntoACacheDirectoryThatDoesNotExistYet()
    {
        _missing = Path.Combine(_directory.Path, "transcripts");

        var sut = new TranscriptStore(_missing);
        sut.SaveAsync(CacheFixture.Cached, "audiobook").GetAwaiter().GetResult();
        _found = sut.FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void CreatesTheDirectory() => Directory.Exists(_missing).ShouldBeTrue();

    [Fact]
    public void StoresSomethingItCanReadBack() => _found.ShouldNotBeNull();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenTheCacheHoldsOnlyOtherFilesTranscripts : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;

    public WhenTheCacheHoldsOnlyOtherFilesTranscripts()
    {
        CacheFixture.Write(
            _directory.Path,
            $"{CacheFixture.OtherDigest}_other.json",
            CacheFixture.Entry(Transcript.CurrentVersion, CacheFixture.OtherDigest, "damn"));

        _found = new TranscriptStore(_directory.Path)
            .FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void ReportsAMiss() => _found.ShouldBeNull();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The readable half of the name is decoration: it exists so an operator can see
/// what is in the cache directory, which is why it is truncated and scrubbed
/// rather than trusted.
/// </summary>
public class WhenTheFileNameWouldBeAwkward : IDisposable
{
    // 35 characters, then padding that takes it past the 80 the Python allowed.
    private const string Awkward = "../My Book: Chapter 1 (unabridged) ";

    private readonly TempDirectory _directory = new();
    private readonly string _fileName;
    private readonly Transcript? _found;

    public WhenTheFileNameWouldBeAwkward()
    {
        var sut = new TranscriptStore(_directory.Path);
        sut.SaveAsync(CacheFixture.Cached, Awkward + new string('x', 100)).GetAwaiter().GetResult();

        _fileName = CacheFixture.JsonFileNames(_directory.Path).Single();
        _found = sut.FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void KeepsOnlyTheCharactersTheLegacyCacheAllowed() =>
        _fileName.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.');

    [Fact]
    public void TruncatesTheReadableHalfToEightyCharacters() =>
        _fileName.ShouldBe($"{CacheFixture.Digest}_.._My_Book__Chapter_1__unabridged__{new string('x', 45)}.json");

    [Fact]
    public void WritesInsideTheCacheDirectoryRatherThanAboveIt() =>
        Directory.EnumerateFiles(_directory.Path, "*.json").Count().ShouldBe(1);

    [Fact]
    public void StillFindsItByDigest() => _found.ShouldNotBeNull();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

public class WhenTheNameHasNothingReadableInIt : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly string _fileName;
    private readonly Transcript? _found;

    public WhenTheNameHasNothingReadableInIt()
    {
        var sut = new TranscriptStore(_directory.Path);
        sut.SaveAsync(CacheFixture.Cached, "   ").GetAwaiter().GetResult();

        _fileName = CacheFixture.JsonFileNames(_directory.Path).Single();
        _found = sut.FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
    }

    [Fact]
    public void FallsBackToAPlaceholder() =>
        _fileName.ShouldBe($"{CacheFixture.Digest}_transcript.json");

    [Fact]
    public void StillFindsItByDigest() => _found.ShouldNotBeNull();

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A re-run saves the same digest again, usually with words the first pass did
/// not have. Leaving the earlier file behind would let a word-less transcript
/// shadow the refined one, so one digest keeps one file.
/// </summary>
public class WhenSavingOverAnEarlierTranscript : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Transcript? _found;
    private readonly IReadOnlyList<string> _files;

    public WhenSavingOverAnEarlierTranscript()
    {
        var sut = new TranscriptStore(_directory.Path);
        var unaligned = new Transcript(
            Transcript.CurrentVersion, CacheFixture.Digest, CacheFixture.Segments, []);

        sut.SaveAsync(unaligned, "book").GetAwaiter().GetResult();
        sut.SaveAsync(CacheFixture.Cached, "renamed book").GetAwaiter().GetResult();

        _found = sut.FindAsync(CacheFixture.Digest).GetAwaiter().GetResult();
        _files = CacheFixture.JsonFileNames(_directory.Path);

        _found.ShouldNotBeNull();
    }

    [Fact]
    public void KeepsOneFilePerDigest() => _files.Count.ShouldBe(1);

    [Fact]
    public void KeepsTheMostRecentlySavedName() =>
        _files[0].ShouldBe($"{CacheFixture.Digest}_renamed_book.json");

    [Fact]
    public void ReturnsTheRefinedTranscriptRatherThanTheWordLessOne() =>
        _found!.Words.Count.ShouldBe(2);

    public void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Cancellation is not a cache miss: a cancelled job must stop, not re-transcribe.</summary>
public class WhenTheJobIsCancelledDuringALookup : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly CancellationTokenSource _source = new();
    private readonly TranscriptStore _sut;
    private readonly string _media;

    public WhenTheJobIsCancelledDuringALookup()
    {
        _sut = new TranscriptStore(_directory.Path);
        _sut.SaveAsync(CacheFixture.Cached, "audiobook").GetAwaiter().GetResult();

        _media = Path.Combine(_directory.Path, "book.mp3");
        File.WriteAllBytes(_media, Encoding.UTF8.GetBytes("foulfilter"));

        _source.Cancel();
    }

    [Fact]
    public async Task StopsHashingRatherThanFinishingTheFile() =>
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _sut.ComputeHashAsync(_media, _source.Token));

    [Fact]
    public async Task StopsLookingRatherThanReportingAMiss() =>
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _sut.FindAsync(CacheFixture.Digest, _source.Token));

    public void Dispose()
    {
        _source.Dispose();
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ffn-pipeline-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
