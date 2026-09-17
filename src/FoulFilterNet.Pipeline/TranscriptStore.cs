using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using FoulFilterNet.Domain;
using FoulFilterNet.Domain.Abstractions;

namespace FoulFilterNet.Pipeline;

/// <summary>
/// The transcript cache on disk: one JSON file per media file, named
/// <c>{digest}_{readable name}.json</c>. Ported from the cache helpers in
/// <c>Legacy/src/pipeline.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Transcription is by far the most expensive stage, and ADR-0002 makes this the
/// one thing that outlives a job: everything else - uploads, scratch, queue
/// state - is ephemeral. A hit here skips the GPU entirely (Resume).
/// </para>
/// <para>
/// Every failure to read an entry is a <em>miss</em>, never an exception. A
/// poisoned cache file may cost a re-transcription; it may not fail a job.
/// Cancellation is the one thing that does propagate - a cancelled job must stop
/// rather than fall through to transcribing again.
/// </para>
/// </remarks>
public sealed class TranscriptStore : ITranscriptStore
{
    /// <summary>The Python's read size. Audiobooks are far too large to hash in one go.</summary>
    private const int ChunkSize = 1 << 20;

    /// <summary>How much of the media file's name survives into the cache file's name.</summary>
    private const int MaxReadableLength = 80;

    /// <summary>Stands in when nothing readable survives sanitising.</summary>
    private const string PlaceholderName = "transcript";

    private const string JsonExtension = ".json";

    /// <summary>Suffix of a file still being written, deliberately not <c>.json</c>.</summary>
    private const string PartialExtension = ".writing";

    /// <summary>
    /// Snake case so the file reads like the Python's did. That is also why a
    /// transcript left behind by the Python parses rather than blowing up: it is
    /// rejected on its schema version, which is the check finding 4 asked for.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;

    /// <summary>Cache transcripts under <paramref name="directory"/>, creating it on first save.</summary>
    public TranscriptStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>Where this store keeps its files.</summary>
    public string DirectoryPath => _directory;

    /// <summary>
    /// MD5 of the file's content, lowercase hex, accumulated a megabyte at a time
    /// so a multi-gigabyte audiobook is never held in memory.
    /// </summary>
    /// <remarks>
    /// MD5 because that is the key the Python cache was built on; this is a cache
    /// key over local files, not a security boundary, and collision resistance
    /// buys nothing here.
    /// </remarks>
    public async Task<string> ComputeHashAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();

#pragma warning disable CA5351 // Cache key over local media, not a security primitive.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            await using var stream = Open(filePath);

            int read;
            while (
                (read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken))
                > 0
            )
            {
                digest.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    /// <summary>
    /// The cached transcript for <paramref name="digest"/>, or null when there is
    /// none this build can use.
    /// </summary>
    /// <remarks>
    /// Three things make an entry unusable, and all three are misses rather than
    /// errors: it cannot be read or parsed, its schema version is not
    /// <see cref="Transcript.CurrentVersion"/> (finding 4), or the hash inside it
    /// disagrees with the one it was found under - a file can be renamed, and
    /// reusing another file's transcript would censor the wrong timestamps.
    /// </remarks>
    public async Task<Transcript?> FindAsync(
        string digest,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var path in EntriesFor(digest))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transcript = await TryReadAsync(path, cancellationToken);
            if (transcript is null || transcript.Version != Transcript.CurrentVersion)
            {
                continue;
            }

            if (!string.Equals(transcript.FileHash, digest, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Complete(transcript);
        }

        return null;
    }

    /// <summary>
    /// Persist <paramref name="transcript"/> under its own file hash, with
    /// <paramref name="baseName"/> supplying the human-readable half of the name.
    /// </summary>
    /// <remarks>
    /// Written to a temporary file and then moved into place, so an interrupted
    /// save leaves the previous entry intact rather than a half-written one. Any
    /// earlier entry for the same digest is removed afterwards: a job saves again
    /// once alignment has run, and a word-less file left alongside could shadow
    /// the refined one.
    /// </remarks>
    public async Task SaveAsync(
        Transcript transcript,
        string baseName,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(transcript);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_directory);

        var fileName = FileNameFor(transcript.FileHash, baseName);
        var path = Path.Combine(_directory, fileName);
        var partial = path + PartialExtension;

        try
        {
            await using (
                var stream = new FileStream(
                    partial,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    ChunkSize,
                    useAsync: true
                )
            )
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    transcript,
                    SerializerOptions,
                    cancellationToken
                );
            }

            File.Move(partial, path, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        RemoveSupersededEntries(transcript.FileHash, fileName);
    }

    /// <summary>
    /// The name an entry takes: the digest, so it can be found, then a scrubbed
    /// and truncated file name, so the cache directory can be read by a human.
    /// </summary>
    /// <remarks>
    /// The Python truncated to 80 characters and replaced anything outside
    /// <c>[A-Za-z0-9-_.]</c> with <c>_</c>. Keeping that is not only fidelity:
    /// since the readable half comes from an uploaded file name, scrubbing it is
    /// also what stops a separator or a <c>..</c> from steering the write out of
    /// the cache directory.
    /// </remarks>
    public static string FileNameFor(string digest, string baseName) =>
        $"{digest}_{Readable(baseName)}{JsonExtension}";

    private static string Readable(string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return PlaceholderName;
        }

        var truncated =
            baseName.Length > MaxReadableLength ? baseName[..MaxReadableLength] : baseName;

        return string.Create(
            truncated.Length,
            truncated,
            static (span, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    var c = source[i];
                    span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
                }
            }
        );
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true);

    /// <summary>
    /// A transcript deserialized from a file that omitted <c>segments</c> or
    /// <c>words</c> arrives with nulls where the record promises lists.
    /// </summary>
    private static Transcript Complete(Transcript transcript) =>
        transcript with
        {
            Segments = transcript.Segments ?? [],
            Words = transcript.Words ?? [],
        };

    private static async Task<Transcript?> TryReadAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var stream = Open(path);
            return await JsonSerializer.DeserializeAsync<Transcript>(
                stream,
                SerializerOptions,
                cancellationToken
            );
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every cache file whose name starts with the digest, in a stable order, so
    /// that a poisoned entry is skipped rather than allowed to hide a good one.
    /// </summary>
    private IReadOnlyList<string> EntriesFor(string digest)
    {
        try
        {
            return
            [
                .. Directory
                    .EnumerateFiles(_directory, "*" + JsonExtension)
                    .Where(path =>
                        Path.GetFileName(path)
                            .StartsWith(digest, StringComparison.OrdinalIgnoreCase)
                    )
                    .Order(StringComparer.Ordinal),
            ];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void RemoveSupersededEntries(string digest, string keep)
    {
        foreach (var path in EntriesFor(digest))
        {
            if (!string.Equals(Path.GetFileName(path), keep, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(path);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
